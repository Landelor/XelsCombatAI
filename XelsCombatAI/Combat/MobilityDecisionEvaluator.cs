using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Combat;

[Flags]
internal enum MobilityIntent
{
    None = 0,
    Uptime = 1,
    Safety = 2,
    PathRecovery = 4
}

internal enum MobilityDecisionState
{
    NotChecked,
    Candidate,
    Used,
    Rejected,
    Idle
}

internal sealed record MobilityDecisionDiagnostics(
    DateTime TimestampUtc,
    MobilityDecisionState State,
    MobilityIntent Intent,
    string IntentLabel,
    string ActionName,
    uint ActionId,
    Vector3? Destination,
    float MoveDistance,
    float SafetyGain,
    float UptimeGain,
    float PathGain,
    string SafetyReason,
    string UptimeReason,
    string PathReason,
    string RiskReason)
{
    public static MobilityDecisionDiagnostics Empty { get; } = new(
        DateTime.MinValue,
        MobilityDecisionState.NotChecked,
        MobilityIntent.None,
        "none",
        "<none>",
        0,
        null,
        0f,
        0f,
        0f,
        0f,
        "not checked",
        "not checked",
        "not checked",
        "not checked");
}

internal sealed class MobilityDecisionEvaluator(BossModReflectionSafety bossModSafety, VNavmeshIpc vnavmesh, JobRangeProvider jobRangeProvider)
{
    private const float MinimumMeaningfulGain = 0.1f;
    private const float GreedyUnsafeEscapeMinimumGain = 5f;
    private const float GreedyUnsafeEscapeMinimumDirectionDot = 0.65f;
    private const float GreedyUnsafeEscapeMaxOffMeshDistance = 1.25f;
    private const float GreedyUnsafeEscapeMaxVerticalDrop = 3f;
    private const float DirectionLengthEpsilon = 0.0001f;

    public MobilityDecisionDiagnostics LastDecision { get; private set; } = MobilityDecisionDiagnostics.Empty;

    public void Reset()
    {
        this.LastDecision = MobilityDecisionDiagnostics.Empty;
    }

    public void RecordIdle(MobilityIntent intent, string actionName, string reason)
    {
        this.LastDecision = new(
            DateTime.UtcNow,
            MobilityDecisionState.Idle,
            intent,
            FormatIntentLabel(intent),
            actionName,
            0,
            null,
            0f,
            0f,
            0f,
            0f,
            "idle",
            "idle",
            "idle",
            reason);
    }

    public MobilityDecisionDiagnostics RecordActionResult(MobilityDecisionDiagnostics decision, bool used, string reason)
    {
        var updated = decision with
        {
            TimestampUtc = DateTime.UtcNow,
            State = used ? MobilityDecisionState.Used : MobilityDecisionState.Rejected,
            RiskReason = reason
        };
        this.LastDecision = updated;
        return updated;
    }

    public bool TryValidateDashDestination(
        IBattleChara player,
        Vector3 destination,
        IBattleChara? target,
        Vector3? safeMovementDestination,
        MobilityIntent requestedIntent,
        string actionName,
        uint actionId,
        float minimumDistance,
        bool requireSafetyProgress,
        bool requireUptimeProgress,
        bool requireVnavReachable,
        out MobilityDecisionDiagnostics decision)
    {
        var moveDistance = IsFinite(destination) ? Geometry.Distance2D(player.Position, destination) : 0f;
        if (!IsFinite(destination))
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, "invalid landing position", out decision);
        }

        if (moveDistance < minimumDistance)
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, $"landing under {minimumDistance:0}y", out decision);
        }

        var safety = this.EvaluateSafety(player, destination, safeMovementDestination);
        if (!safety.CanLand)
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, safety.RiskReason, safety, null, out decision);
        }

        if (requireVnavReachable && !vnavmesh.TryValidateReachable(player.Position, destination, out var vnavmeshReason))
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, vnavmeshReason, safety, null, out decision);
        }

        var uptime = EvaluateUptime(player, destination, target);
        var path = EvaluatePathGain(player.Position, destination, safeMovementDestination);
        if (requireSafetyProgress && safety.Gain <= MinimumMeaningfulGain)
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, "landing does not improve safety", safety, uptime, path, out decision);
        }

        if (requireUptimeProgress && uptime.Gain <= MinimumMeaningfulGain)
        {
            return this.Reject(requestedIntent, actionName, actionId, destination, moveDistance, "landing does not improve uptime", safety, uptime, path, out decision);
        }

        var effectiveIntent = requestedIntent;
        if (safety.Gain > MinimumMeaningfulGain)
        {
            effectiveIntent |= MobilityIntent.Safety;
        }

        if (uptime.Gain > MinimumMeaningfulGain)
        {
            effectiveIntent |= MobilityIntent.Uptime;
        }

        if (path.Gain > MinimumMeaningfulGain)
        {
            effectiveIntent |= MobilityIntent.PathRecovery;
        }

        decision = new(
            DateTime.UtcNow,
            MobilityDecisionState.Candidate,
            effectiveIntent,
            FormatIntentLabel(effectiveIntent),
            actionName,
            actionId,
            destination,
            moveDistance,
            safety.Gain,
            uptime.Gain,
            path.Gain,
            safety.Reason,
            uptime.Reason,
            path.Reason,
            "landing accepted");
        this.LastDecision = decision;
        return true;
    }

    public bool TryValidateGreedyUnsafeEscapeDashDestination(
        IBattleChara player,
        Vector3 destination,
        IBattleChara? target,
        Vector3 safeMovementDestination,
        string actionName,
        uint actionId,
        float minimumDistance,
        out MobilityDecisionDiagnostics decision)
    {
        var moveDistance = IsFinite(destination) ? Geometry.Distance2D(player.Position, destination) : 0f;
        if (!IsFinite(destination) || !IsFinite(safeMovementDestination))
        {
            return this.Reject(MobilityIntent.Safety, actionName, actionId, destination, moveDistance, "invalid landing position", out decision);
        }

        if (moveDistance < minimumDistance)
        {
            return this.Reject(MobilityIntent.Safety, actionName, actionId, destination, moveDistance, $"landing under {minimumDistance:0}y", out decision);
        }

        var safety = this.EvaluateGreedyUnsafeEscapeSafety(player, destination, safeMovementDestination);
        var uptime = EvaluateUptime(player, destination, target);
        var path = EvaluatePathGain(player.Position, destination, safeMovementDestination);
        if (!safety.CanLand)
        {
            return this.Reject(MobilityIntent.Safety, actionName, actionId, destination, moveDistance, safety.RiskReason, safety, uptime, path, out decision);
        }

        var effectiveIntent = MobilityIntent.Safety;
        if (uptime.Gain > MinimumMeaningfulGain)
        {
            effectiveIntent |= MobilityIntent.Uptime;
        }

        if (path.Gain > MinimumMeaningfulGain)
        {
            effectiveIntent |= MobilityIntent.PathRecovery;
        }

        decision = new(
            DateTime.UtcNow,
            MobilityDecisionState.Candidate,
            effectiveIntent,
            FormatIntentLabel(effectiveIntent),
            actionName,
            actionId,
            destination,
            moveDistance,
            safety.Gain,
            uptime.Gain,
            path.Gain,
            safety.Reason,
            uptime.Reason,
            path.Reason,
            "unsafe emergency landing accepted");
        this.LastDecision = decision;
        return true;
    }

    public bool TryValidateStrictLandingSupport(Vector3 playerPosition, Vector3 destination, out string reason)
    {
        var landing = this.EvaluateStrictLandingSupport(playerPosition, destination);
        reason = landing.Reason;
        return landing.CanLand;
    }

    public static string FormatIntentLabel(MobilityIntent intent)
    {
        if (intent == MobilityIntent.None)
        {
            return "none";
        }

        var parts = new List<string>();
        if (intent.HasFlag(MobilityIntent.Safety))
        {
            parts.Add("safety");
        }

        if (intent.HasFlag(MobilityIntent.Uptime))
        {
            parts.Add("uptime");
        }

        if (intent.HasFlag(MobilityIntent.PathRecovery))
        {
            parts.Add("path recovery");
        }

        return string.Join(" + ", parts);
    }

    private bool Reject(
        MobilityIntent intent,
        string actionName,
        uint actionId,
        Vector3? destination,
        float moveDistance,
        string reason,
        out MobilityDecisionDiagnostics decision)
    {
        decision = new(
            DateTime.UtcNow,
            MobilityDecisionState.Rejected,
            intent,
            FormatIntentLabel(intent),
            actionName,
            actionId,
            destination,
            moveDistance,
            0f,
            0f,
            0f,
            "not evaluated",
            "not evaluated",
            "not evaluated",
            reason);
        this.LastDecision = decision;
        return false;
    }

    private bool Reject(
        MobilityIntent intent,
        string actionName,
        uint actionId,
        Vector3 destination,
        float moveDistance,
        string reason,
        SafetyEvaluation safety,
        UptimeEvaluation? uptime,
        out MobilityDecisionDiagnostics decision)
    {
        return this.Reject(intent, actionName, actionId, destination, moveDistance, reason, safety, uptime, null, out decision);
    }

    private bool Reject(
        MobilityIntent intent,
        string actionName,
        uint actionId,
        Vector3 destination,
        float moveDistance,
        string reason,
        SafetyEvaluation safety,
        UptimeEvaluation? uptime,
        PathEvaluation? path,
        out MobilityDecisionDiagnostics decision)
    {
        decision = new(
            DateTime.UtcNow,
            MobilityDecisionState.Rejected,
            intent,
            FormatIntentLabel(intent),
            actionName,
            actionId,
            destination,
            moveDistance,
            safety.Gain,
            uptime?.Gain ?? 0f,
            path?.Gain ?? 0f,
            safety.Reason,
            uptime?.Reason ?? "not evaluated",
            path?.Reason ?? "not evaluated",
            reason);
        this.LastDecision = decision;
        return false;
    }

    private SafetyEvaluation EvaluateSafety(IBattleChara player, Vector3 destination, Vector3? safeMovementDestination)
    {
        var currentSafeKnown = bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason);
        if (!bossModSafety.TryIsPositionSafe(destination, out var destinationSafe, out var destinationReason))
        {
            return new(false, 0f, destinationReason, destinationReason);
        }

        if (!destinationSafe)
        {
            return new(false, 0f, "landing is unsafe", "landing is unsafe");
        }

        if (!bossModSafety.TryIsDashSafe(player.Position, destination, out var dashReason))
        {
            return new(false, 0f, dashReason, dashReason);
        }

        var gain = 0f;
        var reason = "safe landing";
        if (safeMovementDestination.HasValue)
        {
            var currentDistance = Geometry.Distance2D(player.Position, safeMovementDestination.Value);
            var landingDistance = Geometry.Distance2D(destination, safeMovementDestination.Value);
            gain = MathF.Max(gain, currentDistance - landingDistance);
            reason = gain > MinimumMeaningfulGain
                ? $"closer to BMR safe movement by {gain:0.0}y"
                : "safe landing but not closer to BMR safe movement";
        }

        if (currentSafeKnown && !currentSafe)
        {
            gain = MathF.Max(gain, MathF.Max(MinimumMeaningfulGain * 2f, Geometry.Distance2D(player.Position, destination)));
            reason = $"{reason}; current position dangerous";
        }
        else if (!currentSafeKnown)
        {
            reason = $"{reason}; current safety unknown: {currentReason}";
        }

        return new(true, gain, reason, "landing accepted");
    }

    private SafetyEvaluation EvaluateGreedyUnsafeEscapeSafety(IBattleChara player, Vector3 destination, Vector3 safeMovementDestination)
    {
        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            return new(false, 0f, currentReason, currentReason);
        }

        if (currentSafe)
        {
            return new(false, 0f, "current position safe", "current position safe");
        }

        if (!bossModSafety.TryIsPositionSafe(destination, out var destinationSafe, out var destinationReason))
        {
            return new(false, 0f, destinationReason, destinationReason);
        }

        if (destinationSafe)
        {
            return new(false, 0f, "landing is safe; normal escape validation required", "landing is safe; normal escape validation required");
        }

        if (!bossModSafety.TryCanAttemptDashNow(out var dashReason))
        {
            return new(false, 0f, dashReason, dashReason);
        }

        var currentDistance = Geometry.Distance2D(player.Position, safeMovementDestination);
        var landingDistance = Geometry.Distance2D(destination, safeMovementDestination);
        var gain = currentDistance - landingDistance;
        if (gain < GreedyUnsafeEscapeMinimumGain)
        {
            return new(false, MathF.Max(0f, gain), $"escape gain {gain:0.0}y", "not enough escape gain");
        }

        if (!TryDirectionDot(player.Position, destination, safeMovementDestination, out var dot))
        {
            return new(false, gain, "could not compare escape direction", "landing direction not aligned");
        }

        if (dot < GreedyUnsafeEscapeMinimumDirectionDot)
        {
            return new(false, gain, $"escape direction dot {dot:0.00}", "landing direction not aligned");
        }

        var landing = this.EvaluateStrictLandingSupport(player.Position, destination);
        if (!landing.CanLand)
        {
            return new(false, gain, landing.Reason, landing.Reason);
        }

        return new(
            true,
            gain,
            $"unsafe emergency landing; closer to BMR safe movement by {gain:0.0}y; {landing.Reason}",
            "unsafe emergency landing accepted");
    }

    private LandingSupportEvaluation EvaluateStrictLandingSupport(Vector3 playerPosition, Vector3 destination)
    {
        var route = vnavmesh.GetRouteResult(playerPosition, destination);
        if (route.Status != VNavmeshPathStatus.Reachable)
        {
            return new(false, route.Reason);
        }

        var point = vnavmesh.GetPointDiagnostics(destination);
        if (string.Equals(point.Status, "Unavailable", StringComparison.Ordinal))
        {
            return new(false, "vnavmesh unavailable");
        }

        if (!TrySelectLandingSupport(point, out var support, out var supportDistance, out var supportKind))
        {
            return new(false, "off-mesh landing");
        }

        var verticalDrop = playerPosition.Y - support.Y;
        if (verticalDrop > GreedyUnsafeEscapeMaxVerticalDrop)
        {
            return new(false, $"vertical drop {verticalDrop:0.0}y");
        }

        return new(true, $"{supportKind} support {supportDistance:0.0}y");
    }

    private UptimeEvaluation EvaluateUptime(IBattleChara player, Vector3 destination, IBattleChara? target)
    {
        if (target == null)
        {
            return new(0f, "no target");
        }

        var currentSurfaceDistance = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var landingSurfaceDistance = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        var engagementRange = MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange);
        var rangeBonus = landingSurfaceDistance <= engagementRange ? 1f : 0f;
        var gain = MathF.Max(0f, currentSurfaceDistance - landingSurfaceDistance) + rangeBonus;
        var reason = gain > MinimumMeaningfulGain
            ? $"target surface {currentSurfaceDistance:0.0}->{landingSurfaceDistance:0.0}y"
            : "does not improve target range";
        return new(gain, reason);
    }

    private static PathEvaluation EvaluatePathGain(Vector3 playerPosition, Vector3 destination, Vector3? safeMovementDestination)
    {
        if (!safeMovementDestination.HasValue)
        {
            return new(0f, "no BMR movement destination");
        }

        var currentDistance = Geometry.Distance2D(playerPosition, safeMovementDestination.Value);
        var landingDistance = Geometry.Distance2D(destination, safeMovementDestination.Value);
        var gain = MathF.Max(0f, currentDistance - landingDistance);
        return gain > MinimumMeaningfulGain
            ? new(gain, $"dash saves {gain:0.0}y toward movement target")
            : new(0f, "dash does not shorten movement target");
    }

    private static bool TryDirectionDot(Vector3 from, Vector3 destination, Vector3 safeMovementDestination, out float dot)
    {
        var dash = destination - from;
        var escape = safeMovementDestination - from;
        dash.Y = 0f;
        escape.Y = 0f;
        if (dash.LengthSquared() <= DirectionLengthEpsilon || escape.LengthSquared() <= DirectionLengthEpsilon)
        {
            dot = 0f;
            return false;
        }

        dot = Vector3.Dot(Vector3.Normalize(dash), Vector3.Normalize(escape));
        return true;
    }

    private static bool TrySelectLandingSupport(VNavmeshPointDiagnostics point, out Vector3 support, out float supportDistance, out string supportKind)
    {
        support = default;
        supportDistance = float.MaxValue;
        supportKind = string.Empty;

        if (point.NearestReachablePoint.HasValue &&
            point.NearestReachablePointDistance.HasValue &&
            point.NearestReachablePointDistance.Value <= GreedyUnsafeEscapeMaxOffMeshDistance)
        {
            support = point.NearestReachablePoint.Value;
            supportDistance = point.NearestReachablePointDistance.Value;
            supportKind = "reachable";
        }

        if (point.FloorPoint.HasValue &&
            point.FloorPointDistance.HasValue &&
            point.FloorPointDistance.Value <= GreedyUnsafeEscapeMaxOffMeshDistance &&
            point.FloorPointDistance.Value < supportDistance)
        {
            support = point.FloorPoint.Value;
            supportDistance = point.FloorPointDistance.Value;
            supportKind = "floor";
        }

        return supportKind.Length > 0;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private sealed record SafetyEvaluation(bool CanLand, float Gain, string Reason, string RiskReason);
    private sealed record UptimeEvaluation(float Gain, string Reason);
    private sealed record PathEvaluation(float Gain, string Reason);
    private sealed record LandingSupportEvaluation(bool CanLand, string Reason);
}
