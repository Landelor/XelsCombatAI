using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Combat;

internal enum FacingRequestSource
{
    SocialTurning,
    DirectionalDash
}

internal enum FacingBossModPolicy
{
    Conservative,
    AssistValidatedDash,
    AssistBmrMovementDash
}

internal sealed record FacingRequest(
    FacingRequestSource Source,
    float DesiredRotation,
    float ToleranceRadians,
    float MaxCorrectionRadians,
    DateTime ExpiresUtc,
    int Priority,
    string Reason)
{
    public FacingBossModPolicy BossModPolicy { get; init; } = FacingBossModPolicy.Conservative;

    public Vector3? DashDestination { get; init; }

    public Vector3? AssistDestination { get; init; }
}

internal sealed record FacingStatus(
    FacingRequestSource? Source,
    string Reason,
    float? DesiredRotation,
    float? CurrentRotation,
    float? DeltaRadians,
    bool Applied,
    string RejectionReason,
    int ConsensusMembers)
{
    public static FacingStatus Empty { get; } = new(null, "not checked", null, null, null, false, "not checked", 0);
}

internal sealed class FacingController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    ManualMovementInputDetector manualMovement,
    LocalPlayerFacingActuator actuator)
{
    private static readonly TimeSpan SocialConsensusStability = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SocialCorrectionCooldown = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SocialRequestLifetime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DirectionalDashRequestLifetime = TimeSpan.FromMilliseconds(350);

    private const float SocialAllyRange = 35f;
    private const float SocialConsensusWindowRadians = 35f * MathF.PI / 180f;
    private const float SocialConsensusResetRadians = 12f * MathF.PI / 180f;
    private const float SocialStopToleranceRadians = 22.5f * MathF.PI / 180f;
    private const float SocialSmallSnapRadians = 45f * MathF.PI / 180f;
    private const float SocialMaxCorrectionRadians = 160f * MathF.PI / 180f;
    internal const float DirectionalDashToleranceRadians = 8f * MathF.PI / 180f;
    private const float DirectionalDashMaxCorrectionRadians = 135f * MathF.PI / 180f;
    private const float BmrAssistDirectionDot = 0.65f;
    private const float BmrAssistMinimumDestinationGain = 1f;
    private const int SocialRequestPriority = 10;
    private const int DirectionalDashRequestPriority = 50;

    private FacingRequest? pendingRequest;
    private float? stableSocialRotation;
    private DateTime stableSocialSince = DateTime.MinValue;
    private DateTime nextSocialCorrection = DateTime.MinValue;
    private int stableSocialMembers;

    public FacingStatus Status { get; private set; } = FacingStatus.Empty;

    public static FacingRequest CreateDirectionalDashRequest(float desiredRotation, Vector3 dashDestination, string reason, FacingBossModPolicy bossModPolicy, Vector3? assistDestination = null)
    {
        return new(
            FacingRequestSource.DirectionalDash,
            desiredRotation,
            DirectionalDashToleranceRadians,
            DirectionalDashMaxCorrectionRadians,
            DateTime.UtcNow.Add(DirectionalDashRequestLifetime),
            DirectionalDashRequestPriority,
            reason)
        {
            BossModPolicy = bossModPolicy,
            DashDestination = dashDestination,
            AssistDestination = assistDestination
        };
    }

    public void RequestFacing(FacingRequest request)
    {
        var now = DateTime.UtcNow;
        if (request.ExpiresUtc <= now)
        {
            return;
        }

        var normalized = request with
        {
            DesiredRotation = Geometry.NormalizeRadians(request.DesiredRotation),
            ToleranceRadians = MathF.Max(0f, request.ToleranceRadians),
            MaxCorrectionRadians = MathF.Max(0f, request.MaxCorrectionRadians)
        };

        if (this.pendingRequest == null ||
            normalized.Priority >= this.pendingRequest.Priority ||
            this.pendingRequest.ExpiresUtc <= now)
        {
            this.pendingRequest = normalized;
        }
    }

    public void Update(DateTime now, bool suppressAutomatedMovement, BossModMovementDiagnostics bossModMovement)
    {
        if (this.pendingRequest?.ExpiresUtc <= now)
        {
            this.pendingRequest = null;
        }

        if (this.pendingRequest != null)
        {
            if (this.TryApplyRequest(this.pendingRequest, now, suppressAutomatedMovement, bossModMovement))
            {
                this.pendingRequest = null;
            }

            return;
        }

        if (!this.TryCreateSocialTurningRequest(now, out var request, out var reason, out var consensusMembers))
        {
            this.Status = FacingStatus.Empty with
            {
                Source = FacingRequestSource.SocialTurning,
                Reason = reason,
                RejectionReason = reason,
                ConsensusMembers = consensusMembers
            };
            return;
        }

        _ = this.TryApplyRequest(request, now, suppressAutomatedMovement, bossModMovement);
    }

    public void Reset()
    {
        this.pendingRequest = null;
        this.stableSocialRotation = null;
        this.stableSocialSince = DateTime.MinValue;
        this.nextSocialCorrection = DateTime.MinValue;
        this.stableSocialMembers = 0;
        this.Status = FacingStatus.Empty;
    }

    private bool TryCreateSocialTurningRequest(DateTime now, out FacingRequest request, out string reason, out int consensusMembers)
    {
        request = null!;
        consensusMembers = 0;

        if (!config.ManageSocialTurning)
        {
            this.ResetSocialConsensus();
            reason = "social turning disabled";
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.ResetSocialConsensus();
            reason = "missing player";
            return false;
        }

        if (!this.IsDowntimeLikely(out reason))
        {
            this.ResetSocialConsensus();
            return false;
        }

        if (!this.TryGetPartyFacingConsensus(player, out var desiredRotation, out consensusMembers, out reason))
        {
            this.ResetSocialConsensus();
            return false;
        }

        if (this.stableSocialRotation == null ||
            Geometry.AbsAngleDelta(this.stableSocialRotation.Value, desiredRotation) > SocialConsensusResetRadians)
        {
            this.stableSocialRotation = desiredRotation;
            this.stableSocialSince = now;
            this.stableSocialMembers = consensusMembers;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"social consensus stabilizing ({consensusMembers} allies)");
            return false;
        }

        this.stableSocialMembers = consensusMembers;
        if (now - this.stableSocialSince < SocialConsensusStability)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"social consensus waiting ({consensusMembers} allies)");
            return false;
        }

        if (now < this.nextSocialCorrection)
        {
            reason = "social correction cooldown";
            return false;
        }

        var delta = Geometry.NormalizeRadians(desiredRotation - player.Rotation);
        if (MathF.Abs(delta) <= SocialStopToleranceRadians)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"roughly aligned with party ({MathF.Abs(delta) * 180f / MathF.PI:0} deg)");
            return false;
        }

        request = new FacingRequest(
            FacingRequestSource.SocialTurning,
            desiredRotation,
            SocialStopToleranceRadians,
            SocialMaxCorrectionRadians,
            now.Add(SocialRequestLifetime),
            SocialRequestPriority,
            string.Create(CultureInfo.InvariantCulture, $"party facing consensus ({consensusMembers} allies)"));
        reason = request.Reason;
        return true;
    }

    private bool TryApplyRequest(FacingRequest request, DateTime now, bool suppressAutomatedMovement, BossModMovementDiagnostics bossModMovement)
    {
        if (!this.CanApplyFacing(request, suppressAutomatedMovement, bossModMovement, out var rejectionReason))
        {
            this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, null, null, false, rejectionReason, this.stableSocialMembers);
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, null, null, false, "missing player", this.stableSocialMembers);
            return false;
        }

        var currentRotation = Geometry.NormalizeRadians(player.Rotation);
        var delta = Geometry.NormalizeRadians(request.DesiredRotation - currentRotation);
        var absDelta = MathF.Abs(delta);
        if (absDelta <= request.ToleranceRadians)
        {
            this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, currentRotation, delta, false, "within tolerance", this.stableSocialMembers);
            return true;
        }

        var correction = this.CalculateCorrection(request, delta, absDelta);
        if (MathF.Abs(correction) <= 0.001f)
        {
            this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, currentRotation, delta, false, "correction too small", this.stableSocialMembers);
            return true;
        }

        var nextRotation = Geometry.NormalizeRadians(currentRotation + correction);
        if (!actuator.TrySetRotation(player, nextRotation, out rejectionReason))
        {
            this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, currentRotation, delta, false, rejectionReason, this.stableSocialMembers);
            return false;
        }

        if (request.Source == FacingRequestSource.SocialTurning)
        {
            this.nextSocialCorrection = now.Add(SocialCorrectionCooldown);
        }

        this.Status = new FacingStatus(request.Source, request.Reason, request.DesiredRotation, currentRotation, delta, true, string.Empty, this.stableSocialMembers);
        return true;
    }

    private float CalculateCorrection(FacingRequest request, float delta, float absDelta)
    {
        if (request.Source == FacingRequestSource.SocialTurning)
        {
            if (absDelta <= SocialSmallSnapRadians)
            {
                return delta;
            }

            var roughCorrection = MathF.Max(0f, absDelta - request.ToleranceRadians);
            return MathF.CopySign(MathF.Min(roughCorrection, request.MaxCorrectionRadians), delta);
        }

        return MathF.CopySign(MathF.Min(absDelta, request.MaxCorrectionRadians), delta);
    }

    private bool CanApplyFacing(FacingRequest request, bool suppressAutomatedMovement, BossModMovementDiagnostics bossModMovement, out string reason)
    {
        reason = string.Empty;

        if (!config.Enabled)
        {
            reason = "plugin disabled";
            return false;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services))
        {
            reason = "not in combat";
            return false;
        }

        if (services.Condition[ConditionFlag.Unconscious])
        {
            reason = "player dead";
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "missing player";
            return false;
        }

        if (player.IsCasting)
        {
            reason = "player casting";
            return false;
        }

        if (ActionUse.HasAnimationLock())
        {
            reason = "animation lock";
            return false;
        }

        if (suppressAutomatedMovement)
        {
            reason = "manual movement suppression active";
            return false;
        }

        if (manualMovement.IsManualMovementRequested())
        {
            reason = "manual movement input";
            return false;
        }

        if (manualMovement.IsManualFacingRequested())
        {
            reason = "manual facing input";
            return false;
        }

        if (this.HasBossModSafetyPressure(request, bossModMovement, out reason))
        {
            return false;
        }

        reason = request.Source.ToString();
        return true;
    }

    private bool HasBossModSafetyPressure(FacingRequest request, BossModMovementDiagnostics bossModMovement, out string reason)
    {
        if (IsBlockingSpecialMode(bossModMovement.HintDetails.ImminentSpecialMode))
        {
            reason = $"BossMod special mode active: {bossModMovement.HintDetails.ImminentSpecialMode}";
            return true;
        }

        var activeBossModMovement =
            bossModMovement.ControllerDetails.NavigationTarget.HasValue ||
            bossModMovement.NavigationDestinationPosition.HasValue ||
            bossModMovement.NavigationNextWaypointPosition.HasValue ||
            HasVector(bossModMovement.HintDetails.ForcedMovement) ||
            HasVector(bossModMovement.MovementDetails.DesiredDirection) ||
            HasVector(bossModMovement.MovementDetails.ActualMove);

        if (activeBossModMovement &&
            !this.CanAssistBossModMovement(request, bossModMovement, out reason))
        {
            return true;
        }

        if (activeBossModMovement)
        {
            reason = string.Empty;
            return false;
        }

        if (bossModMovement.HintDetails.ForbiddenDirections.GetValueOrDefault() > 0)
        {
            if (!CanAssistPassiveBossModPressure(request))
            {
                reason = "BossMod forbidden direction active";
                return true;
            }
        }

        if (bossModMovement.HintDetails.ForbiddenZones.GetValueOrDefault() > 0)
        {
            if (!CanAssistPassiveBossModPressure(request))
            {
                reason = "BossMod danger zone active";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private bool CanAssistBossModMovement(FacingRequest request, BossModMovementDiagnostics bossModMovement, out string reason)
    {
        if (request.BossModPolicy != FacingBossModPolicy.AssistBmrMovementDash)
        {
            reason = "BossMod movement active";
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "missing player";
            return false;
        }

        if (!TryGetHorizontalDirection(player.Position, request.DashDestination, out var dashDirection))
        {
            reason = "dash direction unavailable";
            return false;
        }

        if (!TryGetBossModMovementDirection(player.Position, bossModMovement, out var bossModDirection, out var bossModReason))
        {
            reason = bossModReason;
            return false;
        }

        var dot = Vector2.Dot(dashDirection, bossModDirection);
        if (dot < BmrAssistDirectionDot)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"BossMod movement conflicts with dash ({dot:0.00})");
            return false;
        }

        var dashDestination = request.DashDestination.GetValueOrDefault();
        if (request.AssistDestination.HasValue)
        {
            var currentDistance = Geometry.Distance2D(player.Position, request.AssistDestination.Value);
            var landingDistance = Geometry.Distance2D(dashDestination, request.AssistDestination.Value);
            if (currentDistance - landingDistance < BmrAssistMinimumDestinationGain)
            {
                reason = "dash does not advance BossMod destination";
                return false;
            }
        }

        reason = "dash assists BossMod movement";
        return true;
    }

    private static bool CanAssistPassiveBossModPressure(FacingRequest request)
    {
        return request.DashDestination.HasValue &&
               request.BossModPolicy is FacingBossModPolicy.AssistValidatedDash or FacingBossModPolicy.AssistBmrMovementDash;
    }

    private static bool TryGetBossModMovementDirection(Vector3 playerPosition, BossModMovementDiagnostics bossModMovement, out Vector2 direction, out string reason)
    {
        if (TryGetHorizontalDirection(bossModMovement.HintDetails.ForcedMovement, out direction))
        {
            reason = "BossMod forced movement";
            return true;
        }

        if (TryGetHorizontalDirection(bossModMovement.MovementDetails.DesiredDirection, out direction))
        {
            reason = "BossMod desired movement";
            return true;
        }

        if (TryGetHorizontalDirection(bossModMovement.MovementDetails.ActualMove, out direction))
        {
            reason = "BossMod actual movement";
            return true;
        }

        if (TryGetHorizontalDirection(playerPosition, bossModMovement.NavigationNextWaypointPosition, out direction))
        {
            reason = "BossMod next waypoint";
            return true;
        }

        if (TryGetHorizontalDirection(playerPosition, bossModMovement.NavigationDestinationPosition, out direction))
        {
            reason = "BossMod destination";
            return true;
        }

        if (TryGetHorizontalDirection(playerPosition, bossModMovement.ControllerDetails.NavigationTarget, out direction))
        {
            reason = "BossMod navigation target";
            return true;
        }

        reason = "BossMod movement direction unavailable";
        direction = default;
        return false;
    }

    private bool IsDowntimeLikely(out string reason)
    {
        var target = services.TargetManager.Target;
        if (target == null)
        {
            reason = "target missing";
            return true;
        }

        if (!target.IsTargetable)
        {
            reason = "target not targetable";
            return true;
        }

        var nextDowntimeStart = bossMod.NextDowntimeIn();
        var nextDowntimeEnd = bossMod.NextDowntimeEndIn();
        if (IsFiniteTimelineValue(nextDowntimeEnd) &&
            (!IsFiniteTimelineValue(nextDowntimeStart) || nextDowntimeEnd < nextDowntimeStart))
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"BossMod downtime active until {nextDowntimeEnd:0.0}s");
            return true;
        }

        reason = "target active";
        return false;
    }

    private bool TryGetPartyFacingConsensus(IBattleChara player, out float desiredRotation, out int consensusMembers, out string reason)
    {
        desiredRotation = 0f;
        consensusMembers = 0;

        var allies = PartyAllyProvider
            .GetVisiblePartyAllies(services, player)
            .Members
            .Where(ally => ally.ObjectKind == ObjectKind.Pc)
            .Where(ally => Geometry.Distance2D(player.Position, ally.Position) <= SocialAllyRange)
            .Select(ally => Geometry.NormalizeRadians(ally.Rotation))
            .ToArray();

        var requiredMembers = services.PartyList.Count >= 8 ? 4 : 2;
        if (allies.Length < requiredMembers)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"not enough visible party facing data ({allies.Length}/{requiredMembers})");
            return false;
        }

        List<float>? bestCluster = null;
        var bestConcentration = 0f;
        var ordered = allies
            .Select(ToPositiveRadians)
            .OrderBy(rotation => rotation)
            .ToArray();
        var extended = ordered
            .Concat(ordered.Select(rotation => rotation + MathF.Tau))
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var cluster = new List<float>();
            for (var j = i; j < i + ordered.Length && extended[j] - extended[i] <= SocialConsensusWindowRadians; j++)
            {
                cluster.Add(Geometry.NormalizeRadians(extended[j]));
            }

            if (!TryCalculateMeanRotation(cluster, out _, out var concentration))
            {
                continue;
            }

            if (bestCluster == null ||
                cluster.Count > bestCluster.Count ||
                cluster.Count == bestCluster.Count && concentration > bestConcentration)
            {
                bestCluster = cluster;
                bestConcentration = concentration;
            }
        }

        if (bestCluster == null || bestCluster.Count < requiredMembers)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"no party facing consensus ({allies.Length} visible)");
            return false;
        }

        if (!TryCalculateMeanRotation(bestCluster, out desiredRotation, out _))
        {
            reason = "party facing consensus ambiguous";
            return false;
        }

        consensusMembers = bestCluster.Count;
        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"party facing consensus ({bestCluster.Count}/{allies.Length})");
        return true;
    }

    private void ResetSocialConsensus()
    {
        this.stableSocialRotation = null;
        this.stableSocialSince = DateTime.MinValue;
        this.stableSocialMembers = 0;
    }

    private static bool TryCalculateMeanRotation(IReadOnlyList<float> rotations, out float mean, out float concentration)
    {
        mean = 0f;
        concentration = 0f;
        if (rotations.Count == 0)
        {
            return false;
        }

        var sin = 0f;
        var cos = 0f;
        foreach (var rotation in rotations)
        {
            var (s, c) = MathF.SinCos(rotation);
            sin += s;
            cos += c;
        }

        var length = MathF.Sqrt((sin * sin) + (cos * cos));
        if (length <= 0.001f)
        {
            return false;
        }

        mean = Geometry.NormalizeRadians(MathF.Atan2(sin, cos));
        concentration = length / rotations.Count;
        return true;
    }

    private static float ToPositiveRadians(float rotation)
    {
        rotation = Geometry.NormalizeRadians(rotation);
        return rotation < 0f ? rotation + MathF.Tau : rotation;
    }

    private static bool TryGetHorizontalDirection(Vector3 playerPosition, Vector3? destination, out Vector2 direction)
    {
        if (!destination.HasValue)
        {
            direction = default;
            return false;
        }

        return TryGetHorizontalDirection(playerPosition, new Vector2(destination.Value.X, destination.Value.Z), out direction);
    }

    private static bool TryGetHorizontalDirection(Vector3 playerPosition, Vector2? destination, out Vector2 direction)
    {
        if (!destination.HasValue)
        {
            direction = default;
            return false;
        }

        return TryGetHorizontalDirection(playerPosition, destination.Value, out direction);
    }

    private static bool TryGetHorizontalDirection(Vector3 playerPosition, Vector2 destination, out Vector2 direction)
    {
        var delta = destination - new Vector2(playerPosition.X, playerPosition.Z);
        if (delta.LengthSquared() <= 0.01f)
        {
            direction = default;
            return false;
        }

        direction = Vector2.Normalize(delta);
        return true;
    }

    private static bool TryGetHorizontalDirection(Vector3? vector, out Vector2 direction)
    {
        if (!vector.HasValue)
        {
            direction = default;
            return false;
        }

        return TryGetHorizontalDirection(new Vector2(vector.Value.X, vector.Value.Z), out direction);
    }

    private static bool TryGetHorizontalDirection(Vector2? vector, out Vector2 direction)
    {
        if (!vector.HasValue)
        {
            direction = default;
            return false;
        }

        return TryGetHorizontalDirection(vector.Value, out direction);
    }

    private static bool TryGetHorizontalDirection(Vector2 vector, out Vector2 direction)
    {
        if (vector.LengthSquared() <= 0.01f)
        {
            direction = default;
            return false;
        }

        direction = Vector2.Normalize(vector);
        return true;
    }

    private static bool HasVector(Vector3? value)
    {
        return value.HasValue && ((value.Value.X * value.Value.X) + (value.Value.Z * value.Value.Z)) > 0.01f;
    }

    private static bool HasVector(Vector2? value)
    {
        return value.HasValue && value.Value.LengthSquared() > 0.01f;
    }

    private static bool IsBlockingSpecialMode(string value)
    {
        return value.Contains("Pyretic", StringComparison.Ordinal) ||
               value.Contains("NoMovement", StringComparison.Ordinal) ||
               value.Contains("Freezing", StringComparison.Ordinal) ||
               value.Contains("Misdirection", StringComparison.Ordinal);
    }

    private static bool IsFiniteTimelineValue(float value)
    {
        return float.IsFinite(value) && value < float.MaxValue * 0.5f;
    }
}
