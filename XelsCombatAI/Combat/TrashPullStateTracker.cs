using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal enum TrashPullPhase
{
    None,
    Gathering,
    Stabilizing,
    Burning,
    Disrupted
}

internal readonly record struct TrashPullActorPosition(ulong ObjectId, Vector3 Position);

internal sealed record TrashPullObservation(
    DateTime NowUtc,
    bool InCombat,
    bool TrashContext,
    bool BossContext,
    bool ManualSuppressed,
    bool BmrSafetyPressure,
    Vector3 PlayerPosition,
    float EngagementRange,
    float PackAoeRange,
    TrashPullActorPosition? Tank,
    IReadOnlyList<TrashPullActorPosition> PartyMembers,
    IReadOnlyList<TargetSnapshot> DominantTargets,
    IReadOnlyList<TargetSnapshot> AllTargets);

internal sealed record TrashPullDiagnostics(
    TrashPullPhase Phase,
    float Confidence,
    string Reason,
    ulong TankObjectId,
    Vector3? TankPosition,
    Vector3? TankVelocity,
    float TankSpeed,
    Vector3? ProjectedTankPosition,
    Vector3? LeadDestination,
    bool LeadCandidateActive,
    bool LeadClampApplied,
    float? BehindDistance,
    Vector3? PackCentroid,
    Vector3? PackVelocity,
    float PackSpeed,
    float? PartyMedianSpeed,
    IReadOnlyList<TrashPullActorPosition> PartyMembers,
    int DominantTargetCount,
    int StragglerTargetCount,
    IReadOnlyList<ulong> DominantTargetIds,
    IReadOnlyList<ulong> StragglerTargetIds,
    string LeadRejectionReason)
{
    public static TrashPullDiagnostics Empty { get; } = new(
        TrashPullPhase.None,
        0f,
        "not evaluated",
        0,
        null,
        null,
        0f,
        null,
        null,
        false,
        false,
        null,
        null,
        null,
        0f,
        null,
        [],
        0,
        0,
        [],
        [],
        "<none>");
}

internal sealed class TrashPullStateTracker
{
    private static readonly TimeSpan GatheringWarmup = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GatheringCornerHold = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan BurningSettleTime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TargetStableTime = TimeSpan.FromSeconds(2);
    private const float TankGatheringSpeed = 2f;
    private const float TankSettledSpeed = 1.2f;
    private const float PackFluidSpeed = 0.75f;
    private const float PackSettledSpeed = 0.8f;
    private const float PartyFluidSpeed = 1.5f;
    private const float LeadActivateBehindDistance = 7f;
    private const float LeadDeactivateBehindDistance = 4f;
    private const float LeadMinimumBehindTank = 2f;
    private const float LeadFarPackExtraDistance = 5f;
    private const float CatchUpPackDistance = 16f;
    private const float CatchUpTankDistance = 12f;
    private const float MaxObservedSpeed = 12f;

    private TrashPullPhase phase = TrashPullPhase.None;
    private DateTime phaseEnteredAtUtc = DateTime.MinValue;
    private DateTime tankMovingSinceUtc = DateTime.MinValue;
    private DateTime tankSlowSinceUtc = DateTime.MinValue;
    private DateTime packSlowSinceUtc = DateTime.MinValue;
    private DateTime targetCountChangedAtUtc = DateTime.MinValue;
    private int lastTargetCount = -1;
    private Vector3? lastTankPosition;
    private DateTime lastTankAtUtc = DateTime.MinValue;
    private Vector3? lastPackCentroid;
    private DateTime lastPackAtUtc = DateTime.MinValue;
    private readonly Dictionary<ulong, (Vector3 Position, DateTime AtUtc)> lastPartyPositions = [];
    private bool leadActive;

    public TrashPullDiagnostics Current { get; private set; } = TrashPullDiagnostics.Empty;

    public void Reset(string reason = "reset")
    {
        this.phase = TrashPullPhase.None;
        this.phaseEnteredAtUtc = DateTime.MinValue;
        this.tankMovingSinceUtc = DateTime.MinValue;
        this.tankSlowSinceUtc = DateTime.MinValue;
        this.packSlowSinceUtc = DateTime.MinValue;
        this.targetCountChangedAtUtc = DateTime.MinValue;
        this.lastTargetCount = -1;
        this.lastTankPosition = null;
        this.lastTankAtUtc = DateTime.MinValue;
        this.lastPackCentroid = null;
        this.lastPackAtUtc = DateTime.MinValue;
        this.lastPartyPositions.Clear();
        this.leadActive = false;
        this.Current = TrashPullDiagnostics.Empty with { Reason = reason };
    }

    public TrashPullDiagnostics Update(TrashPullObservation observation)
    {
        var now = observation.NowUtc;
        var allTargets = DistinctTargets(observation.AllTargets);
        var dominantTargets = DistinctTargets(observation.DominantTargets);
        var dominantIds = dominantTargets.Select(target => target.InstanceId).ToArray();
        var dominantSet = dominantIds.ToHashSet();
        var stragglerIds = allTargets
            .Where(target => !dominantSet.Contains(target.InstanceId))
            .Select(target => target.InstanceId)
            .ToArray();
        var packCentroid = dominantTargets.Count > 0
            ? new Vector3(
                dominantTargets.Average(target => target.Position.X),
                observation.PlayerPosition.Y,
                dominantTargets.Average(target => target.Position.Y))
            : (Vector3?)null;
        var packVelocity = this.ComputeVelocity(packCentroid, now, this.lastPackCentroid, this.lastPackAtUtc);
        var packSpeed = ClampSpeed(Length2D(packVelocity));
        var tankPosition = observation.Tank?.Position;
        var tankVelocity = this.ComputeVelocity(tankPosition, now, this.lastTankPosition, this.lastTankAtUtc);
        var tankSpeed = ClampSpeed(Length2D(tankVelocity));
        var partyMedianSpeed = this.ComputePartyMedianSpeed(observation.PartyMembers, now);

        this.UpdateStableTimers(now, allTargets.Count, tankSpeed, packSpeed);

        if (packCentroid.HasValue)
        {
            this.lastPackCentroid = packCentroid.Value;
            this.lastPackAtUtc = now;
        }

        if (tankPosition.HasValue)
        {
            this.lastTankPosition = tankPosition.Value;
            this.lastTankAtUtc = now;
        }

        var phaseReason = this.ResolvePhase(
            observation,
            now,
            tankSpeed,
            packSpeed,
            partyMedianSpeed,
            tankPosition.HasValue,
            tankPosition,
            packCentroid,
            dominantTargets.Count,
            allTargets.Count,
            out var nextPhase,
            out var confidence);
        this.SetPhase(nextPhase, now);

        var lead = this.BuildLeadDiagnostics(
            observation,
            tankPosition,
            tankVelocity,
            tankSpeed,
            packCentroid,
            packVelocity,
            partyMedianSpeed,
            dominantTargets.Count,
            stragglerIds.Length,
            dominantIds,
            stragglerIds,
            confidence,
            phaseReason);

        this.Current = lead;
        return this.Current;
    }

    private TrashPullDiagnostics BuildLeadDiagnostics(
        TrashPullObservation observation,
        Vector3? tankPosition,
        Vector3? tankVelocity,
        float tankSpeed,
        Vector3? packCentroid,
        Vector3? packVelocity,
        float? partyMedianSpeed,
        int dominantTargetCount,
        int stragglerTargetCount,
        IReadOnlyList<ulong> dominantIds,
        IReadOnlyList<ulong> stragglerIds,
        float confidence,
        string phaseReason)
    {
        var projectedTank = (Vector3?)null;
        var leadDestination = (Vector3?)null;
        var behindDistance = (float?)null;
        var clampApplied = false;
        var leadCandidateActive = false;
        var rejectionReason = "<none>";

        if (this.phase != TrashPullPhase.Gathering)
        {
            this.leadActive = false;
            rejectionReason = $"phase {this.phase}";
        }
        else if (confidence < 0.65f)
        {
            this.leadActive = false;
            rejectionReason = string.Create(CultureInfo.InvariantCulture, $"confidence {confidence:0.00}");
        }
        else if (!tankPosition.HasValue)
        {
            this.leadActive = false;
            rejectionReason = "tank unavailable";
        }
        else if (!packCentroid.HasValue)
        {
            this.leadActive = false;
            rejectionReason = "pack unavailable";
        }
        else
        {
            var tank2 = ToVector2(tankPosition.Value);
            var player2 = ToVector2(observation.PlayerPosition);
            var pack2 = ToVector2(packCentroid.Value);
            var forward = ResolveForward(tankVelocity, packVelocity, tank2, player2, pack2);
            if (forward.LengthSquared() <= 0.01f)
            {
                this.leadActive = false;
                rejectionReason = "tank direction unavailable";
            }
            else
            {
                behindDistance = Vector2.Dot(tank2 - player2, forward);
                var trailingDistance = DesiredTrailingDistance(observation.EngagementRange, observation.PackAoeRange);
                var distanceToPack = Vector2.Distance(player2, pack2);
                var farBehindPack = distanceToPack > trailingDistance + LeadFarPackExtraDistance &&
                                    behindDistance.Value >= LeadMinimumBehindTank;
                var shouldLead = this.leadActive
                    ? behindDistance.Value > LeadDeactivateBehindDistance
                    : behindDistance.Value >= LeadActivateBehindDistance || farBehindPack;

                if (!shouldLead)
                {
                    this.leadActive = false;
                    rejectionReason = behindDistance.Value < 0f
                        ? string.Create(CultureInfo.InvariantCulture, $"ahead of tank by {-behindDistance.Value:0.0}y")
                        : string.Create(CultureInfo.InvariantCulture, $"close enough behind tank ({behindDistance.Value:0.0}y)");
                }
                else
                {
                    var projectSeconds = Math.Clamp(0.5f + (tankSpeed / 12f * 0.5f), 0.5f, 1f);
                    var projected2 = tank2 + forward * tankSpeed * projectSeconds;
                    projectedTank = ToVector3(projected2, tankPosition.Value.Y);
                    var trailingPoint = projected2 - forward * trailingDistance;
                    var packTrailingPoint = pack2 - forward * MathF.Min(trailingDistance, 4f);
                    var candidate = Vector2.Lerp(trailingPoint, packTrailingPoint, 0.35f);
                    var candidateLead = Vector2.Dot(candidate - tank2, forward);
                    if (candidateLead > -LeadMinimumBehindTank)
                    {
                        candidate -= forward * (candidateLead + LeadMinimumBehindTank);
                        clampApplied = true;
                    }

                    var finalLead = Vector2.Dot(candidate - tank2, forward);
                    if (finalLead > -LeadMinimumBehindTank + 0.05f)
                    {
                        this.leadActive = false;
                        rejectionReason = "destination would overtake tank";
                    }
                    else
                    {
                        this.leadActive = true;
                        leadCandidateActive = true;
                        leadDestination = ToVector3(candidate, tankPosition.Value.Y);
                        rejectionReason = clampApplied ? "clamped behind tank" : "<none>";
                    }
                }
            }
        }

        return new TrashPullDiagnostics(
            this.phase,
            confidence,
            phaseReason,
            observation.Tank?.ObjectId ?? 0,
            tankPosition,
            tankVelocity,
            tankSpeed,
            projectedTank,
            leadDestination,
            leadCandidateActive,
            clampApplied,
            behindDistance,
            packCentroid,
            packVelocity,
            ClampSpeed(Length2D(packVelocity)),
            partyMedianSpeed,
            observation.PartyMembers,
            dominantTargetCount,
            stragglerTargetCount,
            dominantIds,
            stragglerIds,
            rejectionReason);
    }

    private string ResolvePhase(
        TrashPullObservation observation,
        DateTime now,
        float tankSpeed,
        float packSpeed,
        float? partyMedianSpeed,
        bool hasTank,
        Vector3? tankPosition,
        Vector3? packCentroid,
        int dominantCount,
        int allTargetCount,
        out TrashPullPhase nextPhase,
        out float confidence)
    {
        confidence = 0f;
        if (!observation.InCombat)
        {
            nextPhase = TrashPullPhase.None;
            return "not in combat";
        }

        if (observation.BossContext)
        {
            nextPhase = TrashPullPhase.Disrupted;
            return "boss context";
        }

        if (!observation.TrashContext || dominantCount < 2 || allTargetCount < 2)
        {
            nextPhase = TrashPullPhase.None;
            return "not a trash pull";
        }

        if (observation.ManualSuppressed)
        {
            nextPhase = TrashPullPhase.Disrupted;
            return "manual movement suppression active";
        }

        if (observation.BmrSafetyPressure)
        {
            nextPhase = TrashPullPhase.Disrupted;
            return "BMR safety pressure active";
        }

        if (!hasTank)
        {
            nextPhase = TrashPullPhase.Disrupted;
            return "tank unavailable";
        }

        var playerDistanceToPack = packCentroid.HasValue
            ? Distance2D(observation.PlayerPosition, packCentroid.Value)
            : 0f;
        var playerDistanceToTank = tankPosition.HasValue
            ? Distance2D(observation.PlayerPosition, tankPosition.Value)
            : 0f;
        var catchUpDistance = MathF.Max(CatchUpPackDistance, observation.PackAoeRange + 14f);
        if (packCentroid.HasValue &&
            tankPosition.HasValue &&
            playerDistanceToPack >= catchUpDistance &&
            playerDistanceToTank >= CatchUpTankDistance)
        {
            nextPhase = TrashPullPhase.Gathering;
            confidence = 0.72f;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"catching up to remote tank pack: player-pack={playerDistanceToPack:0.0}y player-tank={playerDistanceToTank:0.0}y");
        }

        var partyMoving = partyMedianSpeed.GetValueOrDefault() >= PartyFluidSpeed;
        var targetRecentlyChanged = now - this.targetCountChangedAtUtc < TargetStableTime;
        var tankMovingLongEnough = this.tankMovingSinceUtc != DateTime.MinValue &&
                                   now - this.tankMovingSinceUtc >= GatheringWarmup;
        var fluidPull = tankMovingLongEnough &&
                        (packSpeed >= PackFluidSpeed || partyMoving || targetRecentlyChanged || dominantCount >= 3);
        var gatheringHold = this.phase == TrashPullPhase.Gathering &&
                            now - this.phaseEnteredAtUtc <= GatheringCornerHold &&
                            (tankSpeed >= TankSettledSpeed || packSpeed >= PackSettledSpeed || partyMoving || targetRecentlyChanged);
        if (fluidPull || gatheringHold)
        {
            nextPhase = TrashPullPhase.Gathering;
            confidence = Math.Clamp(
                0.55f +
                (tankMovingLongEnough ? 0.18f : 0f) +
                (packSpeed >= PackFluidSpeed ? 0.12f : 0f) +
                (partyMoving ? 0.08f : 0f) +
                (targetRecentlyChanged ? 0.08f : 0f),
                0.65f,
                0.95f);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"tank dragging trash: tank={tankSpeed:0.0}y/s pack={packSpeed:0.0}y/s party={FormatNullable(partyMedianSpeed)}");
        }

        var targetStable = now - this.targetCountChangedAtUtc >= TargetStableTime;
        var packSettled = this.packSlowSinceUtc != DateTime.MinValue && now - this.packSlowSinceUtc >= BurningSettleTime;
        var tankSettled = this.tankSlowSinceUtc != DateTime.MinValue && now - this.tankSlowSinceUtc >= BurningSettleTime;
        if (targetStable && packSettled && tankSettled)
        {
            nextPhase = TrashPullPhase.Burning;
            confidence = 0.85f;
            return string.Create(CultureInfo.InvariantCulture, $"trash pack settled: targets={allTargetCount} pack={packSpeed:0.0}y/s tank={tankSpeed:0.0}y/s");
        }

        nextPhase = TrashPullPhase.Stabilizing;
        confidence = 0.7f;
        return string.Create(CultureInfo.InvariantCulture, $"trash pull stabilizing: targetsStable={targetStable} pack={packSpeed:0.0}y/s tank={tankSpeed:0.0}y/s");
    }

    private void UpdateStableTimers(DateTime now, int targetCount, float tankSpeed, float packSpeed)
    {
        if (this.lastTargetCount != targetCount)
        {
            var targetCountDecreasedDuringSettledBurn =
                this.phase == TrashPullPhase.Burning &&
                this.lastTargetCount > targetCount &&
                tankSpeed <= TankSettledSpeed &&
                packSpeed <= PackSettledSpeed;
            this.lastTargetCount = targetCount;
            if (!targetCountDecreasedDuringSettledBurn)
            {
                this.targetCountChangedAtUtc = now;
            }
        }
        else if (this.targetCountChangedAtUtc == DateTime.MinValue)
        {
            this.targetCountChangedAtUtc = now;
        }

        if (tankSpeed >= TankGatheringSpeed)
        {
            if (this.tankMovingSinceUtc == DateTime.MinValue)
            {
                this.tankMovingSinceUtc = now;
            }

            this.tankSlowSinceUtc = DateTime.MinValue;
        }
        else
        {
            this.tankMovingSinceUtc = DateTime.MinValue;
            if (tankSpeed <= TankSettledSpeed && this.tankSlowSinceUtc == DateTime.MinValue)
            {
                this.tankSlowSinceUtc = now;
            }
        }

        if (packSpeed <= PackSettledSpeed)
        {
            if (this.packSlowSinceUtc == DateTime.MinValue)
            {
                this.packSlowSinceUtc = now;
            }
        }
        else
        {
            this.packSlowSinceUtc = DateTime.MinValue;
        }
    }

    private void SetPhase(TrashPullPhase nextPhase, DateTime now)
    {
        if (this.phase == nextPhase)
        {
            return;
        }

        this.phase = nextPhase;
        this.phaseEnteredAtUtc = now;
        if (nextPhase != TrashPullPhase.Gathering)
        {
            this.leadActive = false;
        }
    }

    private Vector3? ComputeVelocity(Vector3? current, DateTime now, Vector3? previous, DateTime previousAt)
    {
        if (!current.HasValue || !previous.HasValue || previousAt == DateTime.MinValue)
        {
            return null;
        }

        var elapsed = (float)(now - previousAt).TotalSeconds;
        if (elapsed is < 0.15f or > 2f)
        {
            return null;
        }

        var velocity = (current.Value - previous.Value) / elapsed;
        velocity.Y = 0f;
        return velocity;
    }

    private float? ComputePartyMedianSpeed(IReadOnlyList<TrashPullActorPosition> members, DateTime now)
    {
        var speeds = new List<float>(members.Count);
        foreach (var member in members)
        {
            if (this.lastPartyPositions.TryGetValue(member.ObjectId, out var previous))
            {
                var elapsed = (float)(now - previous.AtUtc).TotalSeconds;
                if (elapsed is >= 0.15f and <= 2f)
                {
                    var speed = Distance2D(member.Position, previous.Position) / elapsed;
                    if (speed is > 0.2f and < MaxObservedSpeed)
                    {
                        speeds.Add(speed);
                    }
                }
            }

            this.lastPartyPositions[member.ObjectId] = (member.Position, now);
        }

        var visible = members.Select(member => member.ObjectId).ToHashSet();
        foreach (var stale in this.lastPartyPositions.Keys.Where(id => !visible.Contains(id)).ToArray())
        {
            this.lastPartyPositions.Remove(stale);
        }

        if (speeds.Count == 0)
        {
            return null;
        }

        speeds.Sort();
        return speeds[speeds.Count / 2];
    }

    private static IReadOnlyList<TargetSnapshot> DistinctTargets(IReadOnlyList<TargetSnapshot> targets)
    {
        return targets
            .GroupBy(target => target.InstanceId)
            .Select(group => group.First())
            .ToArray();
    }

    private static Vector2 ResolveForward(Vector3? tankVelocity, Vector3? packVelocity, Vector2 tank, Vector2 player, Vector2 pack)
    {
        var forward = ToVector2(tankVelocity);
        if (forward.LengthSquared() <= 0.01f)
        {
            forward = ToVector2(packVelocity);
        }

        if (forward.LengthSquared() <= 0.01f)
        {
            forward = tank - player;
        }

        if (forward.LengthSquared() <= 0.01f)
        {
            forward = pack - player;
        }

        return forward.LengthSquared() <= 0.01f ? Vector2.Zero : Vector2.Normalize(forward);
    }

    private static float DesiredTrailingDistance(float engagementRange, float packAoeRange)
    {
        var range = MathF.Max(engagementRange, packAoeRange);
        return Math.Clamp(range * 0.45f, 2.5f, 8f);
    }

    private static float ClampSpeed(float? speed)
    {
        return speed.HasValue && float.IsFinite(speed.Value)
            ? Math.Clamp(speed.Value, 0f, MaxObservedSpeed)
            : 0f;
    }

    private static float? Length2D(Vector3? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return MathF.Sqrt((value.Value.X * value.Value.X) + (value.Value.Z * value.Value.Z));
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static Vector2 ToVector2(Vector3? value)
    {
        return value.HasValue ? new Vector2(value.Value.X, value.Value.Z) : Vector2.Zero;
    }

    private static Vector2 ToVector2(Vector3 value)
    {
        return new Vector2(value.X, value.Z);
    }

    private static Vector3 ToVector3(Vector2 value, float y)
    {
        return new Vector3(value.X, y, value.Y);
    }

    private static string FormatNullable(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "y/s"
            : "n/a";
    }
}
