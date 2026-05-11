using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Combat;

internal enum DashReturnKind
{
    TargetDash,
    Regress
}

internal sealed record DashReturnRequest(
    uint ActionId,
    string ActionName,
    DashReturnKind Kind,
    DateTime EarliestUtc,
    DateTime ExpiresUtc,
    string SourceActionName);

internal sealed record DashStyleCandidate<T>(
    T Source,
    Vector3 Destination,
    MobilityDecisionDiagnostics Decision,
    string Reason,
    float Score);

internal readonly record struct DashStyleReengageOpportunity(
    bool Active,
    string Reason,
    bool AllowsShortDash,
    bool RequiresStrongGain)
{
    public static DashStyleReengageOpportunity None { get; } = new(false, "normal", false, false);
}

internal sealed class DashStyleController(Configuration config, JobRangeProvider jobRangeProvider, ArenaEdgePositioningController arenaEdgePositioningController)
{
    private const float KnockbackDistanceThreshold = 6f;
    private const float KnockbackObservationWindowSeconds = 1f;
    private const float ShortReengageMinimumDistance = 4f;
    private const float StrongStyleGain = 3f;
    private static readonly TimeSpan KnockbackRecoveryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PairedReturnMinimumDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PairedReturnWindow = TimeSpan.FromSeconds(4);

    private DashReturnRequest? pendingReturn;
    private DateTime knockbackRecoveryUntil = DateTime.MinValue;
    private DateTime lastObservationUtc = DateTime.MinValue;
    private Vector3? lastObservedPlayerPosition;
    private ulong lastObservedTargetId;
    private float? lastObservedTargetDistance;

    public string LastStyleReason { get; private set; } = "not checked";
    public bool GreedStyleActive => config.CombatStyle != CombatStyle.Normal;
    public bool ReengageStyleActive => config.UseGapCloser && this.GreedStyleActive;
    public bool EscapeStyleActive => config.UseGapCloser && this.GreedStyleActive;

    public void Reset()
    {
        this.pendingReturn = null;
        this.knockbackRecoveryUntil = DateTime.MinValue;
        this.lastObservationUtc = DateTime.MinValue;
        this.lastObservedPlayerPosition = null;
        this.lastObservedTargetId = 0;
        this.lastObservedTargetDistance = null;
        this.LastStyleReason = "reset";
    }

    public void RecordStyleUse(string reason)
    {
        this.LastStyleReason = reason;
    }

    public void RecordPairedReturn(uint escapeActionId, string escapeActionName)
    {
        if (!this.EscapeStyleActive)
        {
            return;
        }

        DashReturnRequest? request = escapeActionId switch
        {
            ActionUse.SamuraiYatenActionId => this.CreatePairedReturn(ActionUse.SamuraiGyotenActionId, "Gyoten", DashReturnKind.TargetDash, escapeActionName),
            ActionUse.DragoonElusiveJumpActionId => this.CreatePairedReturn(ActionUse.DragoonWingedGlideActionId, "Winged Glide", DashReturnKind.TargetDash, escapeActionName),
            ActionUse.RedMageDisplacementActionId => this.CreatePairedReturn(ActionUse.RedMageCorpsACorpsActionId, "Corps-a-corps", DashReturnKind.TargetDash, escapeActionName),
            ActionUse.ReaperHellsIngressActionId => this.CreatePairedReturn(ActionUse.ReaperRegressActionId, "Regress", DashReturnKind.Regress, escapeActionName),
            ActionUse.ReaperHellsEgressActionId => this.CreatePairedReturn(ActionUse.ReaperRegressActionId, "Regress", DashReturnKind.Regress, escapeActionName),
            _ => null
        };

        if (request == null)
        {
            return;
        }

        this.pendingReturn = request;
        this.LastStyleReason = $"paired return armed: {escapeActionName} -> {request.ActionName}";
    }

    public bool TryGetPairedReturn(out DashReturnRequest request, out string reason)
    {
        request = null!;
        reason = "no paired return";

        if (!this.ReengageStyleActive)
        {
            return false;
        }

        if (this.pendingReturn == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now > this.pendingReturn.ExpiresUtc)
        {
            reason = "paired return expired";
            this.pendingReturn = null;
            this.LastStyleReason = reason;
            return false;
        }

        if (now < this.pendingReturn.EarliestUtc)
        {
            reason = "paired return waiting";
            return false;
        }

        request = this.pendingReturn;
        reason = "paired return";
        return true;
    }

    public void ClearPairedReturn(string reason)
    {
        this.pendingReturn = null;
        this.LastStyleReason = reason;
    }

    public DashStyleReengageOpportunity EvaluateReengageOpportunity(
        IBattleChara player,
        IGameObject target,
        float distanceToHitbox,
        uint? actionId,
        float normalMinimumDistance)
    {
        this.ObserveReengageState(player.Position, target.GameObjectId, distanceToHitbox);
        if (!this.ReengageStyleActive)
        {
            return DashStyleReengageOpportunity.None;
        }

        var now = DateTime.UtcNow;
        var recoveryActive = now <= this.knockbackRecoveryUntil;
        var cappedCharge = actionId.HasValue && ActionUse.GetCurrentCharges(actionId.Value) >= 2;
        var shortDistance = distanceToHitbox >= ShortReengageMinimumDistance && distanceToHitbox < normalMinimumDistance;
        if (shortDistance && recoveryActive)
        {
            return new(true, "knockback recovery", true, false);
        }

        if (shortDistance && cappedCharge)
        {
            return new(true, "capped charge", true, true);
        }

        return new(true, "safe-side reengage", false, false);
    }

    public bool MeetsStrongStyleGain(MobilityDecisionDiagnostics decision)
    {
        return MathF.Max(decision.UptimeGain, decision.PathGain) >= StrongStyleGain;
    }

    public DashStyleCandidate<T> ScoreCandidate<T>(
        T source,
        IBattleChara player,
        Vector3 destination,
        IBattleChara? target,
        Vector3? safeMovementDestination,
        MobilityDecisionDiagnostics decision,
        string reason)
    {
        var score = 0f;
        score += decision.SafetyGain * 4f;
        score += decision.PathGain * 3f;
        score += decision.UptimeGain * 2f;
        score += ScoreEscapeAlignment(player.Position, destination, safeMovementDestination) * 2f;
        score += ScoreMaxMelee(player, destination, target) * 1.5f;
        score += ScoreArenaEdge(destination);
        score -= decision.MoveDistance * 0.035f;

        if (string.Equals(reason, "precision Shukuchi", StringComparison.Ordinal))
        {
            score += 1.5f;
        }
        else if (string.Equals(reason, "ally anchor", StringComparison.Ordinal))
        {
            score += 1f;
        }
        else if (string.Equals(reason, "pack surf", StringComparison.Ordinal))
        {
            score += 0.75f;
        }

        return new(source, destination, decision, reason, score);
    }

    public bool TrySelectBest<T>(IReadOnlyList<DashStyleCandidate<T>> candidates, out DashStyleCandidate<T> selected)
    {
        if (candidates.Count == 0)
        {
            selected = default!;
            return false;
        }

        selected = candidates.OrderByDescending(candidate => candidate.Score).First();
        return true;
    }

    private DashReturnRequest CreatePairedReturn(uint actionId, string actionName, DashReturnKind kind, string sourceActionName)
    {
        var now = DateTime.UtcNow;
        return new(
            actionId,
            actionName,
            kind,
            now.Add(PairedReturnMinimumDelay),
            now.Add(PairedReturnWindow),
            sourceActionName);
    }

    private void ObserveReengageState(Vector3 playerPosition, ulong targetId, float distanceToHitbox)
    {
        var now = DateTime.UtcNow;
        if (targetId == 0 ||
            this.lastObservedTargetId != targetId ||
            this.lastObservedPlayerPosition == null ||
            this.lastObservedTargetDistance == null ||
            (now - this.lastObservationUtc).TotalSeconds > KnockbackObservationWindowSeconds)
        {
            this.RecordObservation(now, playerPosition, targetId, distanceToHitbox);
            return;
        }

        var playerDelta = Geometry.Distance2D(this.lastObservedPlayerPosition.Value, playerPosition);
        var targetDistanceDelta = MathF.Abs(distanceToHitbox - this.lastObservedTargetDistance.Value);
        if (playerDelta >= KnockbackDistanceThreshold || targetDistanceDelta >= KnockbackDistanceThreshold)
        {
            this.knockbackRecoveryUntil = now.Add(KnockbackRecoveryWindow);
            this.LastStyleReason = "knockback recovery window";
        }

        this.RecordObservation(now, playerPosition, targetId, distanceToHitbox);
    }

    private void RecordObservation(DateTime now, Vector3 playerPosition, ulong targetId, float distanceToHitbox)
    {
        this.lastObservationUtc = now;
        this.lastObservedPlayerPosition = playerPosition;
        this.lastObservedTargetId = targetId;
        this.lastObservedTargetDistance = distanceToHitbox;
    }

    private static float ScoreEscapeAlignment(Vector3 playerPosition, Vector3 destination, Vector3? safeMovementDestination)
    {
        if (!safeMovementDestination.HasValue)
        {
            return 0f;
        }

        var dash = destination - playerPosition;
        var escape = safeMovementDestination.Value - playerPosition;
        dash.Y = 0f;
        escape.Y = 0f;
        if (dash.LengthSquared() <= 0.0001f || escape.LengthSquared() <= 0.0001f)
        {
            return 0f;
        }

        return MathF.Max(0f, Vector3.Dot(Vector3.Normalize(dash), Vector3.Normalize(escape)));
    }

    private float ScoreMaxMelee(IBattleChara player, Vector3 destination, IBattleChara? target)
    {
        if (target == null)
        {
            return 0f;
        }

        var surfaceDistance = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        var engagementRange = MathF.Max(CombatConstants.MeleeActionRange, jobRangeProvider.EngagementRange);
        if (surfaceDistance > engagementRange + 0.25f)
        {
            return 0f;
        }

        var targetSurface = MathF.Min(CombatConstants.GapCloserDestinationMeleeRange, engagementRange);
        return MathF.Max(0f, 3f - MathF.Abs(surfaceDistance - targetSurface));
    }

    private float ScoreArenaEdge(Vector3 destination)
    {
        if (!config.AvoidArenaEdge || !arenaEdgePositioningController.TryGetObservedEdgeDistance(destination, out var edgeDistance))
        {
            return 0f;
        }

        if (edgeDistance <= 0f)
        {
            return -8f;
        }

        return MathF.Min(edgeDistance, 5f) * 0.35f;
    }
}
