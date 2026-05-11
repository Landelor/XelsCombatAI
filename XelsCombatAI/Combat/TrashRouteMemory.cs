using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal sealed record TrashRouteMemoryUpdate(
    IReadOnlyList<MovementCandidate> Candidates,
    TrashRouteMemoryDiagnostics Diagnostics,
    bool SuppressBroadTrashCandidates);

internal sealed class TrashRouteMemory
{
    public const string CandidateSource = "Trash route memory";

    private static readonly TimeSpan TrailDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OffsetSideHold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LocalDestinationHold = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RouteSourceHold = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan FailedRouteDestinationHold = TimeSpan.FromSeconds(2.5);
    private const float SampleMinSpacing = 1f;
    private const float PartyTrailClusterRadius = 4f;
    private const float PartyClusterNearGroupDistance = 5f;
    private const float PartyRouteAwaySlack = 1f;
    private const float LookAheadMinDistance = 6f;
    private const float LookAheadMaxDistance = 10f;
    private const float LocalStepMaxDistance = 9.5f;
    private const float MinimumBehindTankDistance = 2f;
    private const float LateralOffsetMin = 1.5f;
    private const float LateralOffsetMax = 3f;
    private const float DefaultLateralOffset = 2.2f;
    private const float ActiveConfidence = 0.65f;
    private const float CloseEnoughPackSlack = 2f;
    private const float MinimumPackFlowRange = 8f;
    private const float LongRangePackFlowRange = 20f;
    private const float LongRangePackFlowFactor = 0.75f;
    private const float LongRangePackFlowMinRange = 14f;
    private const float LongRangePackFlowMaxRange = 20f;
    private const float BurningBridgeMinPackDistance = 6f;
    private const float BacktrackDotThreshold = -0.45f;
    private const float BacktrackSlack = 0.75f;
    private const float VerticalStepThreshold = 0.75f;
    private const float VerticalRouteYThreshold = 1.25f;
    private const float LocalDestinationHoldDistance = 2.25f;
    private const float FailedRouteDestinationRadius = 1.5f;
    private static readonly float[] BreadcrumbLookAheadDistances = [9.5f, 6f, 3.5f];

    private readonly List<TrailSample> tankTrail = [];
    private readonly List<RouteFailure> routeFailures = [];
    private readonly Dictionary<ulong, List<TrailSample>> partyTrails = [];
    private int offsetSide;
    private DateTime offsetSideHeldUntilUtc = DateTime.MinValue;
    private float routeProgressDistance;
    private DateTime lastActiveUtc = DateTime.MinValue;
    private Vector3? heldLocalDestination;
    private DateTime heldLocalDestinationUntilUtc = DateTime.MinValue;
    private string heldRouteSource = "<none>";
    private DateTime heldRouteSourceUntilUtc = DateTime.MinValue;
    private DateTime invalidatedUntilUtc = DateTime.MinValue;
    private string lastInvalidationReason = "<none>";
    private TrashRouteMemoryDiagnostics diagnostics = TrashRouteMemoryDiagnostics.Empty;

    public TrashRouteMemoryDiagnostics Diagnostics => this.diagnostics;

    public TrashRouteMemoryUpdate Update(MovementPlannerContext context, TrashPullDiagnostics trash, Configuration config)
    {
        this.SampleTankTrail(context.NowUtc, trash.TankPosition);
        this.SamplePartyTrails(context.NowUtc, trash.PartyMembers, trash.TankObjectId);
        this.PruneRouteFailures(context.NowUtc);
        if (this.TryGetInactiveReason(context, trash, config, out var inactiveReason))
        {
            if (trash.Phase is TrashPullPhase.None or TrashPullPhase.Burning or TrashPullPhase.Disrupted)
            {
                this.routeProgressDistance = 0f;
                this.heldLocalDestination = null;
                this.heldRouteSource = "<none>";
            }

            return this.SetInactive(inactiveReason);
        }

        if (this.TryBuildBreadcrumbCandidates(context, trash, out var breadcrumbCandidates, out var breadcrumbDiagnostics))
        {
            this.diagnostics = breadcrumbDiagnostics;
            return new(breadcrumbCandidates, this.diagnostics, true);
        }

        if (this.TryBuildFallbackCandidate(context, trash, out var fallbackCandidate, out var fallbackDiagnostics))
        {
            this.diagnostics = fallbackDiagnostics;
            return new([fallbackCandidate], this.diagnostics, true);
        }

        return this.SetInactive("route unavailable");
    }

    public void Reset(string reason = "reset")
    {
        this.tankTrail.Clear();
        this.routeFailures.Clear();
        this.partyTrails.Clear();
        this.offsetSide = 0;
        this.offsetSideHeldUntilUtc = DateTime.MinValue;
        this.routeProgressDistance = 0f;
        this.lastActiveUtc = DateTime.MinValue;
        this.heldLocalDestination = null;
        this.heldLocalDestinationUntilUtc = DateTime.MinValue;
        this.heldRouteSource = "<none>";
        this.heldRouteSourceUntilUtc = DateTime.MinValue;
        this.invalidatedUntilUtc = DateTime.MinValue;
        this.lastInvalidationReason = "<none>";
        this.diagnostics = TrashRouteMemoryDiagnostics.Empty with
        {
            State = "reset",
            Reason = reason
        };
    }

    public void ReportValidation(DateTime now, IReadOnlyList<MovementCandidateScore> routeScores)
    {
        this.PruneRouteFailures(now);
        if (routeScores.Count == 0)
        {
            return;
        }

        var accepted = routeScores.Any(score => score.Accepted);
        var hardFailures = routeScores
            .Where(score => !score.Accepted && IsHardInvalidation(score.RejectionReason))
            .ToArray();
        foreach (var failure in hardFailures)
        {
            this.RememberFailedRouteDestination(now, failure.Destination, failure.RejectionReason);
        }

        if (accepted)
        {
            this.invalidatedUntilUtc = DateTime.MinValue;
            this.lastInvalidationReason = "<none>";
            if (hardFailures.Length > 0)
            {
                this.diagnostics = this.diagnostics with
                {
                    InvalidationReason = hardFailures[0].RejectionReason
                };
            }

            return;
        }

        if (hardFailures.Length == 0)
        {
            return;
        }

        var primary = hardFailures[0];
        this.heldLocalDestination = null;
        this.lastInvalidationReason = primary.RejectionReason;
        this.diagnostics = this.diagnostics with
        {
            InvalidationReason = primary.RejectionReason
        };
    }

    public void Suppress(DateTime now, TrashPullDiagnostics trash, string reason)
    {
        this.SampleTankTrail(now, trash.TankPosition);
        this.SamplePartyTrails(now, trash.PartyMembers, trash.TankObjectId);
        if (trash.Phase is TrashPullPhase.None or TrashPullPhase.Burning or TrashPullPhase.Disrupted)
        {
            this.routeProgressDistance = 0f;
            this.heldLocalDestination = null;
            this.heldRouteSource = "<none>";
        }

        this.diagnostics = TrashRouteMemoryDiagnostics.Empty with
        {
            State = "suppressed",
            Reason = reason,
            TankTrail = this.VisibleTrail()
        };
    }

    private bool TryGetInactiveReason(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        Configuration config,
        out string reason)
    {
        if (!config.LeadTrashPullsWithTank)
        {
            reason = "Lead trash pulls with tank disabled";
            return true;
        }

        if (!config.ManageMovement || !config.ManageTargetUptime)
        {
            reason = "movement or target uptime disabled";
            return true;
        }

        if (trash.Phase is not (TrashPullPhase.Gathering or TrashPullPhase.Stabilizing) &&
            !this.ShouldBridgeBlockedBurningRoute(context, trash))
        {
            reason = $"phase {trash.Phase}";
            return true;
        }

        if (trash.Confidence < ActiveConfidence)
        {
            reason = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"trash confidence {trash.Confidence:0.00}");
            return true;
        }

        if (context.NowUtc < this.invalidatedUntilUtc)
        {
            reason = $"route invalidated:{this.lastInvalidationReason}";
            return true;
        }

        if (trash.Phase == TrashPullPhase.Gathering && trash.BehindDistance is < -1f)
        {
            reason = "player ahead of tank";
            return true;
        }

        if (this.IsCloseEnoughForNormalAoe(context, trash))
        {
            reason = "close enough for normal AoE";
            return true;
        }

        if (context.AutomatedMovementSuppressed)
        {
            reason = "manual movement suppressed";
            return true;
        }

        if (context.BossModEncounterActive)
        {
            reason = "boss module active";
            return true;
        }

        if (context.BmrForcedMovement is { } forced && forced.LengthSquared() > 0.01f)
        {
            reason = "BMR forced movement";
            return true;
        }

        if (context.BmrMoveRequested || context.BmrMoveImminent || context.BmrForbiddenZones > 0)
        {
            reason = "BMR safety pressure";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool ShouldBridgeBlockedBurningRoute(MovementPlannerContext context, TrashPullDiagnostics trash)
    {
        if (trash.Phase != TrashPullPhase.Burning ||
            !trash.PackCentroid.HasValue ||
            !context.LineOfSight.Checked ||
            !context.LineOfSight.Blocked ||
            !this.HasFreshBreadcrumbTrail(context.NowUtc))
        {
            return false;
        }

        var packDistance = Distance2D(context.PlayerPosition, trash.PackCentroid.Value);
        if (packDistance < BurningBridgeMinPackDistance)
        {
            return false;
        }

        return true;
    }

    private bool HasFreshBreadcrumbTrail(DateTime now)
    {
        if (this.tankTrail.Count >= 2 &&
            now - this.tankTrail[^1].AtUtc <= TrailDuration)
        {
            return true;
        }

        foreach (var trail in this.partyTrails.Values)
        {
            if (trail.Count >= 2 &&
                now - trail[^1].AtUtc <= TrailDuration)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCloseEnoughForNormalAoe(MovementPlannerContext context, TrashPullDiagnostics trash)
    {
        if (context.LineOfSight.Checked && context.LineOfSight.Blocked)
        {
            return false;
        }

        if (!trash.PackCentroid.HasValue)
        {
            return false;
        }

        var usefulPackRange = ResolveCloseEnoughPackRange(context.PackAoeRange);
        return Distance2D(context.PlayerPosition, trash.PackCentroid.Value) <= usefulPackRange;
    }

    private static float ResolveCloseEnoughPackRange(float packAoeRange)
    {
        if (packAoeRange >= LongRangePackFlowRange)
        {
            return Math.Clamp(
                packAoeRange * LongRangePackFlowFactor,
                LongRangePackFlowMinRange,
                LongRangePackFlowMaxRange) + CloseEnoughPackSlack;
        }

        return MathF.Max(MinimumPackFlowRange, packAoeRange) + CloseEnoughPackSlack;
    }

    private TrashRouteMemoryUpdate SetInactive(string reason)
    {
        this.diagnostics = TrashRouteMemoryDiagnostics.Empty with
        {
            State = "inactive",
            Reason = reason,
            TankTrail = this.VisibleTrail()
        };
        return new([], this.diagnostics, false);
    }

    private void SampleTankTrail(DateTime now, Vector3? tankPosition)
    {
        this.tankTrail.RemoveAll(sample => now - sample.AtUtc > TrailDuration);
        if (!tankPosition.HasValue)
        {
            return;
        }

        SampleTrail(this.tankTrail, now, tankPosition.Value);
    }

    private void SamplePartyTrails(DateTime now, IReadOnlyList<TrashPullActorPosition> partyMembers, ulong tankObjectId)
    {
        foreach (var trail in this.partyTrails.Values)
        {
            trail.RemoveAll(sample => now - sample.AtUtc > TrailDuration);
        }

        foreach (var empty in this.partyTrails.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray())
        {
            this.partyTrails.Remove(empty);
        }

        foreach (var member in partyMembers)
        {
            if (member.ObjectId == 0 || member.ObjectId == tankObjectId)
            {
                continue;
            }

            if (!this.partyTrails.TryGetValue(member.ObjectId, out var trail))
            {
                trail = [];
                this.partyTrails[member.ObjectId] = trail;
            }

            SampleTrail(trail, now, member.Position);
        }
    }

    private static void SampleTrail(List<TrailSample> trail, DateTime now, Vector3 position)
    {
        if (trail.Count > 0)
        {
            var last = trail[^1];
            var moved = Distance2D(last.Position, position);
            var vertical = MathF.Abs(last.Position.Y - position.Y);
            if (now - last.AtUtc < SampleInterval &&
                moved < SampleMinSpacing &&
                vertical < VerticalStepThreshold)
            {
                return;
            }

            if (moved < SampleMinSpacing && vertical < VerticalStepThreshold)
            {
                return;
            }
        }

        PruneShortBacktracks(trail, position);
        trail.Add(new(now, position));
    }

    private static void PruneShortBacktracks(List<TrailSample> trail, Vector3 next)
    {
        while (trail.Count >= 2)
        {
            var previous = trail[^2].Position;
            var current = trail[^1].Position;
            var a = ToVector2(current - previous);
            var b = ToVector2(next - current);
            if (a.LengthSquared() <= 0.01f || b.LengthSquared() <= 0.01f)
            {
                break;
            }

            a = Vector2.Normalize(a);
            b = Vector2.Normalize(b);
            if (Vector2.Dot(a, b) > BacktrackDotThreshold)
            {
                break;
            }

            if (Distance2D(previous, next) > Distance2D(previous, current) + BacktrackSlack)
            {
                break;
            }

            trail.RemoveAt(trail.Count - 1);
        }
    }

    private bool TryBuildBreadcrumbCandidates(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        out IReadOnlyList<MovementCandidate> candidates,
        out TrashRouteMemoryDiagnostics routeDiagnostics)
    {
        var preferHeldPartyRoute = this.heldRouteSource.Equals("party-trail", StringComparison.Ordinal) &&
                                   context.NowUtc <= this.heldRouteSourceUntilUtc;
        if (preferHeldPartyRoute &&
            this.TryBuildPartyTrailCandidates(context, trash, out candidates, out routeDiagnostics))
        {
            return true;
        }

        if (this.TryBuildTankTrailCandidates(context, trash, out candidates, out routeDiagnostics))
        {
            return true;
        }

        if (!preferHeldPartyRoute &&
            this.TryBuildPartyTrailCandidates(context, trash, out candidates, out routeDiagnostics))
        {
            return true;
        }

        candidates = [];
        routeDiagnostics = TrashRouteMemoryDiagnostics.Empty;
        return false;
    }

    private bool TryBuildTankTrailCandidates(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        out IReadOnlyList<MovementCandidate> candidates,
        out TrashRouteMemoryDiagnostics routeDiagnostics)
    {
        candidates = [];
        routeDiagnostics = TrashRouteMemoryDiagnostics.Empty;
        if (this.tankTrail.Count < 2)
        {
            return false;
        }

        var cumulative = BuildCumulativeDistances(this.tankTrail);
        var totalDistance = cumulative[^1];
        if (totalDistance < MinimumBehindTankDistance + 1f)
        {
            return false;
        }

        var projection = ProjectOnTrail(context.PlayerPosition, this.tankTrail, cumulative);
        if (context.NowUtc - this.lastActiveUtc > TrailDuration)
        {
            this.routeProgressDistance = 0f;
        }

        this.routeProgressDistance = Math.Min(this.routeProgressDistance, totalDistance);
        var baseProgress = Math.Max(projection.DistanceAlong, this.routeProgressDistance);
        var maxGoalDistance = Math.Max(0f, totalDistance - MinimumBehindTankDistance);
        if (baseProgress > maxGoalDistance)
        {
            baseProgress = maxGoalDistance;
        }

        var lookAhead = Math.Clamp((trash.BehindDistance ?? LookAheadMaxDistance) * 0.65f, LookAheadMinDistance, LookAheadMaxDistance);
        var goalDistance = Math.Min(maxGoalDistance, baseProgress + lookAhead);
        if (goalDistance <= 0f)
        {
            return false;
        }

        var verticalStep = ContainsVerticalStep(this.tankTrail);
        var reason = "following tank trail with stable lateral offset";
        if (verticalStep)
        {
            reason += "; vertical tank step present, vnav validation required";
        }

        var routeCandidates = new List<MovementCandidate>(3);
        Vector3? primaryRouteGoal = null;
        Vector3? primaryLocalDestination = null;
        var primarySegment = projection.SegmentIndex;
        var offsetSide = this.offsetSide;
        var offsetDistance = Math.Clamp(DefaultLateralOffset, LateralOffsetMin, LateralOffsetMax);
        foreach (var horizon in BuildBreadcrumbLookAheads(lookAhead))
        {
            goalDistance = Math.Min(maxGoalDistance, baseProgress + horizon);
            if (goalDistance <= baseProgress + 0.5f)
            {
                continue;
            }

            var routeGoal = SampleAtDistance(this.tankTrail, cumulative, goalDistance);
            var routeDirection = DirectionAtDistance(this.tankTrail, cumulative, goalDistance, trash.TankVelocity);
            if (routeDirection.LengthSquared() <= 0.01f)
            {
                continue;
            }

            routeDirection = Vector3.Normalize(routeDirection);
            offsetSide = this.ResolveOffsetSide(context, routeGoal, routeDirection);
            var offsetVector = new Vector3(-routeDirection.Z, 0f, routeDirection.X) * offsetSide * offsetDistance;
            var offsetGoal = routeGoal + offsetVector;
            var localDestination = ClampLocalStep(context.PlayerPosition, offsetGoal);
            localDestination.Y = ResolveRouteDestinationY(context.PlayerPosition, routeGoal, verticalStep);
            if (this.IsFailedRouteDestination(context.NowUtc, localDestination))
            {
                continue;
            }

            if (routeCandidates.Count == 0)
            {
                localDestination = this.HoldStableLocalDestination(context.NowUtc, localDestination);
                if (this.IsFailedRouteDestination(context.NowUtc, localDestination))
                {
                    continue;
                }

                primaryRouteGoal = routeGoal;
                primaryLocalDestination = localDestination;
                primarySegment = projection.SegmentIndex;
            }

            if (routeCandidates.Any(candidate => Distance2D(candidate.Destination, localDestination) < 0.75f))
            {
                continue;
            }

            routeCandidates.Add(new(
                CandidateSource,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{reason}; lookahead {horizon:0.0}y"),
                localDestination,
                1.75f,
                MovementCandidatePriority.ActiveAoe,
                1f,
                Math.Clamp(horizon / LookAheadMaxDistance, 0.55f, 1f),
                0.45f,
                0.95f));

            if (routeCandidates.Count >= 3)
            {
                break;
            }
        }

        if (routeCandidates.Count == 0 || primaryRouteGoal == null || primaryLocalDestination == null)
        {
            return false;
        }

        this.routeProgressDistance = Math.Max(this.routeProgressDistance, baseProgress);
        this.lastActiveUtc = context.NowUtc;
        this.MarkRouteSource("tank-trail", context.NowUtc);

        candidates = routeCandidates;
        routeDiagnostics = new TrashRouteMemoryDiagnostics(
            true,
            "active",
            "tank-trail",
            reason,
            primaryRouteGoal,
            primaryLocalDestination,
            null,
            offsetSide,
            offsetDistance,
            (context.NowUtc - this.tankTrail[0].AtUtc).TotalMilliseconds,
            primarySegment,
            this.tankTrail.Count,
            "NotChecked",
            0,
            0,
            verticalStep ? "tank vertical shortcut possible" : "<none>",
            this.VisibleTrail());
        return true;
    }

    private bool TryBuildPartyTrailCandidates(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        out IReadOnlyList<MovementCandidate> candidates,
        out TrashRouteMemoryDiagnostics routeDiagnostics)
    {
        candidates = [];
        routeDiagnostics = TrashRouteMemoryDiagnostics.Empty;
        if (this.partyTrails.Count == 0)
        {
            return false;
        }

        var routePoints = new List<PartyRoutePoint>();
        foreach (var entry in this.partyTrails)
        {
            var trail = entry.Value;
            if (trail.Count < 2)
            {
                continue;
            }

            var cumulative = BuildCumulativeDistances(trail);
            var totalDistance = cumulative[^1];
            if (totalDistance < 2f)
            {
                continue;
            }

            var projection = ProjectOnTrail(context.PlayerPosition, trail, cumulative);
            foreach (var horizon in BreadcrumbLookAheadDistances)
            {
                var distance = Math.Min(totalDistance, projection.DistanceAlong + horizon);
                if (distance <= projection.DistanceAlong + 0.5f)
                {
                    continue;
                }

                var point = SampleAtDistance(trail, cumulative, distance);
                var direction = DirectionAtDistance(trail, cumulative, distance, fallbackVelocity: null);
                if (direction.LengthSquared() <= 0.01f)
                {
                    continue;
                }

                routePoints.Add(new(
                    entry.Key,
                    point,
                    distance,
                    projection.SegmentIndex,
                    trail.Count,
                    (context.NowUtc - trail[0].AtUtc).TotalMilliseconds,
                    ContainsVerticalStep(trail),
                    trail.TakeLast(32).Select(sample => sample.Position).ToArray()));
            }
        }

        var partyCenter = ResolvePartyCenter(trash, context.PlayerPosition.Y);
        var clusters = BuildPartyRouteClusters(routePoints);
        var viableClusters = clusters
            .Select(cluster => BuildViablePartyCluster(context, trash, partyCenter, cluster))
            .Where(cluster => cluster != null)
            .Cast<ViablePartyRouteCluster>()
            .OrderByDescending(cluster => cluster.ContributorCount)
            .ThenByDescending(cluster => cluster.ProgressScore)
            .ThenBy(cluster => cluster.MoveDistance)
            .Take(3)
            .OrderByDescending(cluster => cluster.MoveDistance)
            .ToArray();
        if (viableClusters.Length == 0)
        {
            return false;
        }

        var routeCandidates = new List<MovementCandidate>(viableClusters.Length);
        for (var i = 0; i < viableClusters.Length; ++i)
        {
            var cluster = viableClusters[i];
            var localDestination = ClampLocalStep(context.PlayerPosition, cluster.Center);
            localDestination.Y = ResolveRouteDestinationY(context.PlayerPosition, cluster.Center, cluster.VerticalStep);
            if (this.IsFailedRouteDestination(context.NowUtc, localDestination))
            {
                continue;
            }

            if (i == 0)
            {
                localDestination = this.HoldStableLocalDestination(context.NowUtc, localDestination);
                if (this.IsFailedRouteDestination(context.NowUtc, localDestination))
                {
                    continue;
                }
            }

            if (routeCandidates.Any(candidate => Distance2D(candidate.Destination, localDestination) < 0.75f))
            {
                continue;
            }

            var reason = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"following party breadcrumb route; contributors={cluster.ContributorCount}; progress={cluster.ProgressScore:0.0}y");
            if (cluster.VerticalStep)
            {
                reason += "; vertical party step present, vnav validation required";
            }

            routeCandidates.Add(new(
                CandidateSource,
                reason,
                localDestination,
                1.75f,
                MovementCandidatePriority.ActiveAoe,
                Math.Clamp(0.65f + (cluster.ContributorCount * 0.1f), 0.7f, 1f),
                Math.Clamp(cluster.ProgressScore / LookAheadMaxDistance, 0.5f, 0.9f),
                0.4f,
                0.85f));
        }

        if (routeCandidates.Count == 0)
        {
            return false;
        }

        var primary = viableClusters[0];
        this.lastActiveUtc = context.NowUtc;
        this.MarkRouteSource("party-trail", context.NowUtc);
        candidates = routeCandidates;
        routeDiagnostics = new TrashRouteMemoryDiagnostics(
            true,
            "active",
            "party-trail",
            "following coherent party breadcrumb route",
            primary.Center,
            routeCandidates[0].Destination,
            null,
            0,
            0f,
            primary.RouteAgeMilliseconds,
            primary.SegmentIndex,
            primary.WaypointCount,
            "NotChecked",
            0,
            0,
            primary.VerticalStep ? "party vertical shortcut possible" : "<none>",
            primary.VisibleTrail);
        return true;
    }

    private void MarkRouteSource(string source, DateTime now)
    {
        this.heldRouteSource = source;
        this.heldRouteSourceUntilUtc = now.Add(RouteSourceHold);
    }

    private static IReadOnlyList<float> BuildBreadcrumbLookAheads(float maxDistance)
    {
        var result = new List<float>(3);
        foreach (var distance in BreadcrumbLookAheadDistances)
        {
            var clamped = Math.Min(distance, maxDistance);
            if (clamped < 2f || result.Any(existing => MathF.Abs(existing - clamped) < 0.75f))
            {
                continue;
            }

            result.Add(clamped);
        }

        return result;
    }

    private static Vector3? ResolvePartyCenter(TrashPullDiagnostics trash, float y)
    {
        if (trash.PartyMembers.Count == 0)
        {
            return null;
        }

        var count = 0;
        var sum = Vector3.Zero;
        foreach (var member in trash.PartyMembers)
        {
            if (member.ObjectId == trash.TankObjectId)
            {
                continue;
            }

            sum += new Vector3(member.Position.X, y, member.Position.Z);
            ++count;
        }

        return count == 0 ? null : sum / count;
    }

    private static IReadOnlyList<List<PartyRoutePoint>> BuildPartyRouteClusters(IReadOnlyList<PartyRoutePoint> routePoints)
    {
        var clusters = new List<List<PartyRoutePoint>>();
        foreach (var point in routePoints)
        {
            List<PartyRoutePoint>? bestCluster = null;
            var bestDistance = float.MaxValue;
            foreach (var cluster in clusters)
            {
                var center = AveragePoint(cluster);
                var distance = Distance2D(center, point.Position);
                if (distance <= PartyTrailClusterRadius && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCluster = cluster;
                }
            }

            if (bestCluster == null)
            {
                bestCluster = [];
                clusters.Add(bestCluster);
            }

            bestCluster.Add(point);
        }

        return clusters;
    }

    private static ViablePartyRouteCluster? BuildViablePartyCluster(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        Vector3? partyCenter,
        IReadOnlyList<PartyRoutePoint> cluster)
    {
        if (cluster.Count == 0)
        {
            return null;
        }

        var center = AveragePoint(cluster);
        var verticalStep = cluster.Any(point => point.VerticalStep);
        center.Y = ResolveRouteDestinationY(context.PlayerPosition, center, verticalStep);
        var contributorCount = cluster.Select(point => point.ObjectId).Distinct().Count();
        var closeToPartyCluster = partyCenter.HasValue &&
                                  Distance2D(center, partyCenter.Value) <= PartyClusterNearGroupDistance;
        if (contributorCount < 2 && !closeToPartyCluster)
        {
            return null;
        }

        var packProgress = 0f;
        var bestEndpointPackProgress = 0f;
        if (trash.PackCentroid.HasValue)
        {
            var currentPackDistance = Distance2D(context.PlayerPosition, trash.PackCentroid.Value);
            packProgress = currentPackDistance - Distance2D(center, trash.PackCentroid.Value);
            bestEndpointPackProgress = cluster.Max(point => currentPackDistance - Distance2D(point.Position, trash.PackCentroid.Value));
        }

        var partyProgress = partyCenter.HasValue
            ? Distance2D(context.PlayerPosition, partyCenter.Value) - Distance2D(center, partyCenter.Value)
            : 0f;
        var routeBlocked = context.LineOfSight.Checked && context.LineOfSight.Blocked;
        if (packProgress < -PartyRouteAwaySlack &&
            partyProgress < -PartyRouteAwaySlack &&
            !(routeBlocked && bestEndpointPackProgress > 0.5f))
        {
            return null;
        }

        var progressScore = Math.Max(packProgress, partyProgress);
        if (progressScore < -0.5f)
        {
            return null;
        }

        var representative = cluster
            .OrderByDescending(point => point.DistanceAlong)
            .ThenByDescending(point => point.WaypointCount)
            .First();
        return new(
            center,
            contributorCount,
            progressScore,
            Distance2D(context.PlayerPosition, center),
            verticalStep,
            representative.SegmentIndex,
            representative.WaypointCount,
            representative.RouteAgeMilliseconds,
            representative.VisibleTrail);
    }

    private bool TryBuildFallbackCandidate(
        MovementPlannerContext context,
        TrashPullDiagnostics trash,
        out MovementCandidate candidate,
        out TrashRouteMemoryDiagnostics routeDiagnostics)
    {
        candidate = null!;
        routeDiagnostics = TrashRouteMemoryDiagnostics.Empty;
        var fallbackGoal = trash.LeadDestination ?? trash.ProjectedTankPosition ?? trash.PackCentroid;
        if (!fallbackGoal.HasValue)
        {
            return false;
        }

        var localDestination = ClampLocalStep(context.PlayerPosition, fallbackGoal.Value);
        localDestination.Y = ResolveRouteDestinationY(
            context.PlayerPosition,
            fallbackGoal.Value,
            MathF.Abs(fallbackGoal.Value.Y - context.PlayerPosition.Y) >= VerticalRouteYThreshold);
        localDestination = this.HoldStableLocalDestination(context.NowUtc, localDestination);
        if (this.IsFailedRouteDestination(context.NowUtc, localDestination))
        {
            return false;
        }

        this.lastActiveUtc = context.NowUtc;

        const string reason = "breadcrumb route unavailable; using bounded fallback";
        candidate = new MovementCandidate(
            CandidateSource,
            reason,
            localDestination,
            1.75f,
            MovementCandidatePriority.ActiveAoe,
            0.85f,
            0.55f,
            0.35f,
            0.75f);
        routeDiagnostics = new TrashRouteMemoryDiagnostics(
            true,
            "active",
            "fallback",
            reason,
            fallbackGoal,
            localDestination,
            null,
            this.offsetSide,
            0f,
            this.tankTrail.Count == 0 ? 0d : (context.NowUtc - this.tankTrail[0].AtUtc).TotalMilliseconds,
            0,
            this.tankTrail.Count,
            "NotChecked",
            0,
            0,
            "breadcrumb route unavailable",
            this.VisibleTrail());
        this.MarkRouteSource("fallback", context.NowUtc);
        return true;
    }

    private void RememberFailedRouteDestination(DateTime now, Vector3 destination, string reason)
    {
        this.routeFailures.RemoveAll(failure => Distance2D(failure.Destination, destination) <= FailedRouteDestinationRadius);
        this.routeFailures.Add(new(destination, reason, now.Add(FailedRouteDestinationHold)));
    }

    private bool IsFailedRouteDestination(DateTime now, Vector3 destination)
    {
        this.PruneRouteFailures(now);
        return this.routeFailures.Any(failure => Distance2D(failure.Destination, destination) <= FailedRouteDestinationRadius);
    }

    private void PruneRouteFailures(DateTime now)
    {
        this.routeFailures.RemoveAll(failure => now >= failure.UntilUtc);
    }

    private Vector3 HoldStableLocalDestination(DateTime now, Vector3 destination)
    {
        if (this.heldLocalDestination.HasValue &&
            now <= this.heldLocalDestinationUntilUtc &&
            Distance2D(this.heldLocalDestination.Value, destination) <= LocalDestinationHoldDistance)
        {
            return this.heldLocalDestination.Value;
        }

        this.heldLocalDestination = destination;
        this.heldLocalDestinationUntilUtc = now.Add(LocalDestinationHold);
        return destination;
    }

    private int ResolveOffsetSide(MovementPlannerContext context, Vector3 routeGoal, Vector3 routeDirection)
    {
        if (this.offsetSide != 0 && context.NowUtc <= this.offsetSideHeldUntilUtc)
        {
            return this.offsetSide;
        }

        var left = new Vector3(-routeDirection.Z, 0f, routeDirection.X);
        var playerDelta = context.PlayerPosition - routeGoal;
        var sideValue = Dot2D(playerDelta, left);
        var side = sideValue >= 0f ? 1 : -1;
        if (MathF.Abs(sideValue) < 0.5f && this.offsetSide != 0)
        {
            side = this.offsetSide;
        }

        this.offsetSide = side;
        this.offsetSideHeldUntilUtc = context.NowUtc.Add(OffsetSideHold);
        return side;
    }

    private IReadOnlyList<Vector3> VisibleTrail()
    {
        return this.tankTrail
            .TakeLast(32)
            .Select(sample => sample.Position)
            .ToArray();
    }

    private static Vector3 AveragePoint(IReadOnlyList<PartyRoutePoint> points)
    {
        if (points.Count == 0)
        {
            return Vector3.Zero;
        }

        var sum = Vector3.Zero;
        foreach (var point in points)
        {
            sum += point.Position;
        }

        return sum / points.Count;
    }

    private static float[] BuildCumulativeDistances(IReadOnlyList<TrailSample> samples)
    {
        var cumulative = new float[samples.Count];
        for (var i = 1; i < samples.Count; ++i)
        {
            cumulative[i] = cumulative[i - 1] + Distance2D(samples[i - 1].Position, samples[i].Position);
        }

        return cumulative;
    }

    private static TrailProjection ProjectOnTrail(Vector3 point, IReadOnlyList<TrailSample> samples, IReadOnlyList<float> cumulative)
    {
        var bestDistanceSq = float.MaxValue;
        var bestAlong = 0f;
        var bestSegment = 0;
        for (var i = 0; i < samples.Count - 1; ++i)
        {
            var distanceSq = DistanceToSegmentSq2D(point, samples[i].Position, samples[i + 1].Position, out var t);
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            bestAlong = cumulative[i] + (Distance2D(samples[i].Position, samples[i + 1].Position) * t);
            bestSegment = i;
        }

        return new(bestAlong, bestSegment);
    }

    private static Vector3 SampleAtDistance(IReadOnlyList<TrailSample> samples, IReadOnlyList<float> cumulative, float distance)
    {
        if (samples.Count == 0)
        {
            return Vector3.Zero;
        }

        if (distance <= 0f)
        {
            return samples[0].Position;
        }

        for (var i = 0; i < samples.Count - 1; ++i)
        {
            var from = cumulative[i];
            var to = cumulative[i + 1];
            if (distance > to)
            {
                continue;
            }

            var segmentLength = MathF.Max(0.001f, to - from);
            var t = Math.Clamp((distance - from) / segmentLength, 0f, 1f);
            return Vector3.Lerp(samples[i].Position, samples[i + 1].Position, t);
        }

        return samples[^1].Position;
    }

    private static Vector3 DirectionAtDistance(
        IReadOnlyList<TrailSample> samples,
        IReadOnlyList<float> cumulative,
        float distance,
        Vector3? fallbackVelocity)
    {
        for (var i = 0; i < samples.Count - 1; ++i)
        {
            if (distance > cumulative[i + 1])
            {
                continue;
            }

            var direction = samples[i + 1].Position - samples[i].Position;
            direction.Y = 0f;
            if (direction.LengthSquared() > 0.01f)
            {
                return direction;
            }
        }

        if (fallbackVelocity.HasValue)
        {
            var velocity = fallbackVelocity.Value;
            velocity.Y = 0f;
            if (velocity.LengthSquared() > 0.01f)
            {
                return velocity;
            }
        }

        var tail = samples[^1].Position - samples[^2].Position;
        tail.Y = 0f;
        return tail;
    }

    private static Vector3 ClampLocalStep(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.Y = 0f;
        var distance = delta.Length();
        if (distance <= LocalStepMaxDistance || distance <= 0.001f)
        {
            return to;
        }

        var clamped = from + (delta / distance * LocalStepMaxDistance);
        clamped.Y = to.Y;
        return clamped;
    }

    private static float ResolveRouteDestinationY(Vector3 playerPosition, Vector3 routePoint, bool verticalRoute)
    {
        return verticalRoute || MathF.Abs(routePoint.Y - playerPosition.Y) >= VerticalRouteYThreshold
            ? routePoint.Y
            : playerPosition.Y;
    }

    private static bool ContainsVerticalStep(IReadOnlyList<TrailSample> samples)
    {
        for (var i = Math.Max(1, samples.Count - 8); i < samples.Count; ++i)
        {
            if (MathF.Abs(samples[i].Position.Y - samples[i - 1].Position.Y) >= VerticalStepThreshold &&
                Distance2D(samples[i].Position, samples[i - 1].Position) <= LookAheadMaxDistance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHardInvalidation(string reason)
    {
        return reason is "BmrUnsafe"
            or "BmrPathBlocked"
            or "BmrPathActiveDanger"
            or "OutsidePathfindMap"
            or "OffMeshDestination"
            or "Unreachable"
            or "BmrBoundaryHugging";
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static float DistanceToSegmentSq2D(Vector3 point, Vector3 a, Vector3 b, out float t)
    {
        var ab = ToVector2(b - a);
        var ap = ToVector2(point - a);
        var lenSq = ab.LengthSquared();
        if (lenSq <= 0.0001f)
        {
            t = 0f;
            return ap.LengthSquared();
        }

        t = Math.Clamp(Vector2.Dot(ap, ab) / lenSq, 0f, 1f);
        var closest = ToVector2(a) + (ab * t);
        var delta = ToVector2(point) - closest;
        return delta.LengthSquared();
    }

    private static float Dot2D(Vector3 a, Vector3 b)
    {
        return (a.X * b.X) + (a.Z * b.Z);
    }

    private static Vector2 ToVector2(Vector3 value)
    {
        return new(value.X, value.Z);
    }

    private readonly record struct TrailSample(DateTime AtUtc, Vector3 Position);

    private readonly record struct RouteFailure(Vector3 Destination, string Reason, DateTime UntilUtc);

    private readonly record struct TrailProjection(float DistanceAlong, int SegmentIndex);

    private sealed record PartyRoutePoint(
        ulong ObjectId,
        Vector3 Position,
        float DistanceAlong,
        int SegmentIndex,
        int WaypointCount,
        double RouteAgeMilliseconds,
        bool VerticalStep,
        IReadOnlyList<Vector3> VisibleTrail);

    private sealed record ViablePartyRouteCluster(
        Vector3 Center,
        int ContributorCount,
        float ProgressScore,
        float MoveDistance,
        bool VerticalStep,
        int SegmentIndex,
        int WaypointCount,
        double RouteAgeMilliseconds,
        IReadOnlyList<Vector3> VisibleTrail);
}
