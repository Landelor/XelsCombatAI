using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Combat;

internal sealed class MovementIntentPlanner(
    Configuration config,
    DalamudServices services,
    BossModReflectionSafety bossModSafety,
    CombatLineOfSightChecker lineOfSight,
    VNavmeshIpc vnavmesh,
    JobRangeProvider jobRangeProvider,
    Func<bool> automatedMovementSuppressed,
    Func<TrashPullDiagnostics> trashPullDiagnostics,
    IReadOnlyList<IBossModGoalZoneContributor> legacyContributors,
    IReadOnlyList<IMovementCandidateSource> candidateSources)
    : IBossModGoalZoneContributor
{
    private const string LineOfSightReengageSource = "Line of sight reengage";
    private const string LineOfSightRecoverySource = "Line of sight recovery";
    private const string ObstacleRecoverySource = "Obstacle recovery";
    private const string BmrSafetyEscapeSource = "BMR safety escape";
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan PendingPathHoldDuration = TimeSpan.FromSeconds(2);
    private const float MeaningfullyBetterMultiplier = 1.3f;
    private const float CloseScoreTie = 8f;
    private const float DestinationSameThreshold = 1.75f;
    private const float TinyMovementThreshold = 0.75f;
    private const float SlideCastWindowSeconds = 0.5f;
    private const float StuckProgressThreshold = 0.08f;
    private const float StuckDestinationDistanceBuffer = 1f;
    private const float MaxOffMeshDestinationDistance = 1.25f;
    private const float MaxOffMeshVerticalDistance = 2.75f;
    private const float MaxOffMeshSnapDistance = 3.5f;
    private const float MaxOffMeshSnapVerticalDistance = 8f;
    private const float HighDetourDirectDistance = 6f;
    private const float HighDetourExtraDistance = 12f;
    private const float HighDetourRatio = 2f;
    private const float PathCornerMinDistance = 3f;
    private const float PathCornerMinExtraDistance = 1.5f;
    private const float PathCornerMinDetourRatio = 1.25f;
    private const float PathCornerMinYawDelta = 0.75f;
    private const float PathCornerAcceptanceRadius = 1.25f;
    private const float ObstacleRecoveryTriggerDistance = 5f;
    private const float ObstacleRecoveryBoundarySearchRadius = 1.35f;
    private const float ObstacleRecoveryBoundaryAwayDot = 0.25f;
    private const float ObstacleRecoveryAcceptanceRadius = 1.1f;
    private const float BmrSafetyEscapeSearchRadius = 7f;
    private const float BmrSafetyEscapeBoundaryDistance = 1f;
    private const float BmrSafetyEscapeAcceptanceRadius = 1.1f;
    private const float LineOfSightRecoveryAcceptanceRadius = 1.1f;
    private const float LineOfSightRecoveryMaxAnchorDistance = 8f;
    private const float BossCenterRouteBuffer = 0.25f;
    private const float BossCenterRouteBossRadius = 4f;
    private const float MaxPendingFallbackDirectDistance = 12.5f;
    private const float LineOfSightReengageBlockedScore = -100f;
    private const float LineOfSightReengageVisibleOutOfRangeScore = 8f;
    private const float LineOfSightReengageVisibleInRangeScore = 20f;
    private const int MaxLoggedCandidates = 8;
    private static readonly float[] ObstacleRecoveryDistances = [2.5f, 4f];
    private static readonly float[] ObstacleBoundaryRecoveryDistances = [2.25f, 3.5f];
    private static readonly float[] LineOfSightRecoveryDistances = [2.25f, 3.75f, 5.5f, 7.5f];
    private static readonly TimeSpan StuckIntentDuration = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan StuckDestinationCooldown = TimeSpan.FromSeconds(3);

    private ResolvedHintMembers? resolvedMembers;
    private Type? resolvedHintsType;
    private bool bmrMoveRequested;
    private bool bmrMoveImminent;
    private bool bossModEncounterActive;
    private MovementIntent? currentIntent;
    private long nextIntentId;
    private MovementPlannerDiagnostics diagnostics = MovementPlannerDiagnostics.Empty;
    private long progressIntentId;
    private Vector3? progressLastPosition;
    private DateTime progressLastMovedUtc = DateTime.MinValue;
    private string? stuckSuppressedSource;
    private Vector3? stuckSuppressedDestination;
    private DateTime stuckSuppressedUntilUtc = DateTime.MinValue;
    private readonly TrashRouteMemory trashRouteMemory = new();

    public MovementPlannerDiagnostics Diagnostics => this.diagnostics;

    public void SetHookState(string state)
    {
        foreach (var contributor in legacyContributors)
        {
            contributor.SetHookState(state);
        }
    }

    public void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
        this.bmrMoveRequested = moveRequested;
        this.bmrMoveImminent = moveImminent;
        foreach (var contributor in legacyContributors)
        {
            contributor.SetBossModMovementState(moveRequested, moveImminent);
        }
    }

    public void SetBossModEncounterState(bool activeModule)
    {
        this.bossModEncounterActive = activeModule;
        foreach (var contributor in legacyContributors)
        {
            contributor.SetBossModEncounterState(activeModule);
        }
    }

    public void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions)
    {
        var legacyContributions = new List<BossModGoalContribution>();
        foreach (var contributor in legacyContributors)
        {
            try
            {
                contributor.TryInjectGoal(hints, legacyContributions);
            }
            catch (Exception ex)
            {
                services.Log.Verbose(ex, $"Movement planner source '{contributor.GetType().Name}' failed.");
            }
        }

        var now = DateTime.UtcNow;
        if (!this.TryBuildContext(hints, now, out var context, out var buildFailure))
        {
            this.ClearIntent(buildFailure);
            this.trashRouteMemory.Reset(buildFailure);
            this.diagnostics = this.BuildDiagnostics(
                now,
                switchReason: buildFailure,
                suppressionReason: buildFailure,
                generatedCount: 0,
                acceptedCount: 0,
                rejectedByReason: new Dictionary<string, int>(),
                topCandidates: [],
                context: null,
                chosenScore: null);
            return;
        }

        if (this.TryGetSuppressionReason(context, out var suppressionReason))
        {
            this.ClearIntent(suppressionReason);
            this.trashRouteMemory.Suppress(now, trashPullDiagnostics(), suppressionReason);
            this.diagnostics = this.BuildDiagnostics(
                now,
                switchReason: suppressionReason,
                suppressionReason: suppressionReason,
                generatedCount: 0,
                acceptedCount: 0,
                rejectedByReason: new Dictionary<string, int>(),
                topCandidates: [],
                context,
                chosenScore: null);
            return;
        }

        if (this.ShouldUseLineOfSightReengage(context))
        {
            this.ClearIntent(LineOfSightReengageSource);
            this.trashRouteMemory.Suppress(now, trashPullDiagnostics(), LineOfSightReengageSource);
            var reengageSwitchReason = LineOfSightReengageSource;
            if (this.TryCreateLineOfSightReengageGoal(context, out var reengageGoal, out var reengageFailure))
            {
                contributions.Add(new(reengageGoal, BossModGoalPriority.ImmediateAction, LineOfSightReengageSource, BossModGoalScoreMode.Raw));
            }
            else
            {
                reengageSwitchReason = $"{LineOfSightReengageSource}:{reengageFailure}";
            }

            this.diagnostics = this.BuildDiagnostics(
                now,
                reengageSwitchReason,
                suppressionReason: "<none>",
                generatedCount: 0,
                acceptedCount: 0,
                rejectedByReason: new Dictionary<string, int>(),
                topCandidates: [],
                context,
                chosenScore: null);
            return;
        }

        this.UpdateCurrentIntentProgress(context);
        var routeMemory = this.trashRouteMemory.Update(context, trashPullDiagnostics(), config);

        var candidates = new List<MovementCandidate>();
        if (routeMemory.Candidates.Count > 0)
        {
            candidates.AddRange(routeMemory.Candidates);
        }

        if (!routeMemory.SuppressBroadTrashCandidates)
        {
            this.AddTargetRangeCandidates(context, candidates);
        }

        this.AddBmrSafetyEscapeCandidates(context, candidates);
        this.AddLineOfSightRecoveryCandidates(context, candidates);
        this.AddObstacleRecoveryCandidates(context, candidates);
        foreach (var source in candidateSources)
        {
            try
            {
                source.AddMovementCandidates(context, candidates);
            }
            catch (Exception ex)
            {
                services.Log.Verbose(ex, $"Movement candidate source '{source.GetType().Name}' failed.");
            }
        }

        if (routeMemory.SuppressBroadTrashCandidates)
        {
            SuppressBroadTrashCandidates(context, candidates);
        }

        if (this.ShouldAllowBmrSafetyEscapeAssist(context))
        {
            SuppressNonBmrSafetyEscapeCandidates(candidates);
        }

        var queryBudget = MovementPlannerQueryBudget.ForRouteMemory(routeMemory.SuppressBroadTrashCandidates);
        var rejectedByReason = new Dictionary<string, int>(StringComparer.Ordinal);
        var routeScores = new List<MovementCandidateScore>();
        var topCandidates = new List<MovementCandidateScore>(MaxLoggedCandidates);
        MovementCandidateScore? best = null;
        var acceptedCount = 0;
        foreach (var candidate in candidates)
        {
            var score = this.ScoreCandidate(context, candidate, queryBudget);
            if (score.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal))
            {
                routeScores.Add(score);
            }

            if (score.Accepted)
            {
                ++acceptedCount;
                if (best == null || CompareAcceptedCandidates(score, best) > 0)
                {
                    best = score;
                }
            }
            else
            {
                rejectedByReason[score.RejectionReason] = rejectedByReason.TryGetValue(score.RejectionReason, out var count)
                    ? count + 1
                    : 1;
            }

            InsertTopCandidate(topCandidates, score);
        }

        this.trashRouteMemory.ReportValidation(now, routeScores);

        MovementCandidateScore? currentValidation = null;
        var currentSourceStillGenerated = this.CurrentIntentSourceStillGenerated(candidates);
        var currentValid = this.currentIntent != null &&
                           currentSourceStillGenerated &&
                           this.ValidateCurrentIntent(context, out currentValidation);
        var chosen = this.SelectIntent(now, best, currentSourceStillGenerated, currentValid, currentValidation, out var switchReason);
        if (chosen == null && string.Equals(switchReason, "no_candidate", StringComparison.Ordinal))
        {
            switchReason = BuildNoCandidateReason(candidates.Count, rejectedByReason);
        }
        else if (chosen == null && string.Equals(switchReason, "current_invalid", StringComparison.Ordinal) && rejectedByReason.Count > 0)
        {
            switchReason = string.Create(CultureInfo.InvariantCulture, $"current_invalid:{TopRejectedReason(rejectedByReason)}");
        }

        this.currentIntent = chosen;

        this.diagnostics = this.BuildDiagnostics(
            now,
            switchReason,
            suppressionReason: chosen == null ? switchReason : "<none>",
            candidates.Count,
            acceptedCount,
            rejectedByReason,
            topCandidates,
            context,
            chosen?.Score,
            queryBudget);

        if (chosen == null)
        {
            return;
        }

        var members = this.resolvedMembers;
        if (members == null)
        {
            this.ClearIntent("BMR planner reflection unavailable");
            return;
        }

        var goal = new FlatTopGoal(chosen.Destination, chosen.AcceptanceRadius)
            .CreateGoalDelegate(members.WPosType, members.WPosXField, members.WPosZField);
        contributions.Add(new(goal, BossModGoalPriority.ImmediateAction, "Movement intent"));
    }

    public void Reset()
    {
        this.resolvedMembers = null;
        this.resolvedHintsType = null;
        this.bmrMoveRequested = false;
        this.bmrMoveImminent = false;
        this.bossModEncounterActive = false;
        this.currentIntent = null;
        this.ResetProgressTracking();
        this.stuckSuppressedSource = null;
        this.stuckSuppressedDestination = null;
        this.stuckSuppressedUntilUtc = DateTime.MinValue;
        this.trashRouteMemory.Reset();
        this.diagnostics = MovementPlannerDiagnostics.Empty with
        {
            SwitchReason = "reset",
            SuppressionReason = "reset"
        };

        foreach (var contributor in legacyContributors)
        {
            contributor.Reset();
        }
    }

    private static void InsertTopCandidate(List<MovementCandidateScore> topCandidates, MovementCandidateScore score)
    {
        for (var i = 0; i < topCandidates.Count; ++i)
        {
            if (CompareTopCandidates(score, topCandidates[i]) > 0)
            {
                topCandidates.Insert(i, score);
                if (topCandidates.Count > MaxLoggedCandidates)
                {
                    topCandidates.RemoveAt(MaxLoggedCandidates);
                }

                return;
            }
        }

        if (topCandidates.Count < MaxLoggedCandidates)
        {
            topCandidates.Add(score);
        }
    }

    private static int CompareTopCandidates(MovementCandidateScore left, MovementCandidateScore right)
    {
        var accepted = left.Accepted.CompareTo(right.Accepted);
        if (accepted != 0)
        {
            return accepted;
        }

        return CompareAcceptedCandidates(left, right);
    }

    private static int CompareAcceptedCandidates(MovementCandidateScore left, MovementCandidateScore right)
    {
        var total = left.TotalScore.CompareTo(right.TotalScore);
        if (total != 0)
        {
            return total;
        }

        return left.Priority.CompareTo(right.Priority);
    }

    private MovementIntent? SelectIntent(
        DateTime now,
        MovementCandidateScore? best,
        bool currentSourceStillGenerated,
        bool currentValid,
        MovementCandidateScore? currentValidation,
        out string switchReason)
    {
        if (this.currentIntent != null && !currentSourceStillGenerated)
        {
            this.currentIntent = null;
            switchReason = "source_expired";
            if (best == null)
            {
                return null;
            }

            return this.CreateIntent(best, now);
        }

        if (this.currentIntent != null && currentValid)
        {
            if (best == null)
            {
                if (now <= this.currentIntent.HoldUntilUtc)
                {
                    switchReason = "held_previous";
                    return this.currentIntent;
                }

                switchReason = "no_candidate";
                return null;
            }

            var sameDestination = Distance2D(best.Destination, this.currentIntent.Destination) <= DestinationSameThreshold;
            if (sameDestination)
            {
                switchReason = "same_destination";
                return this.currentIntent with
                {
                    Score = best,
                    HoldUntilUtc = now.Add(HoldDuration)
                };
            }

            var currentScore = currentValidation?.TotalScore ?? this.currentIntent.Score.TotalScore;
            if (this.IsPathRecoveryIntent(this.currentIntent) &&
                best.Source.Equals("Target range", StringComparison.Ordinal) &&
                best.TotalScore >= currentScore - CloseScoreTie)
            {
                switchReason = "recovery_complete";
                return this.CreateIntent(best, now);
            }

            if (this.ShouldTrashPriorityTakeover(this.currentIntent, best, currentScore))
            {
                switchReason = "priority_takeover";
                return this.CreateIntent(best, now);
            }

            if (now <= this.currentIntent.HoldUntilUtc &&
                best.TotalScore < currentScore * MeaningfullyBetterMultiplier)
            {
                switchReason = "hold_active";
                return this.currentIntent;
            }

            if (best.TotalScore < currentScore * MeaningfullyBetterMultiplier &&
                MathF.Abs(best.TotalScore - currentScore) <= CloseScoreTie &&
                best.Priority <= this.currentIntent.Score.Priority)
            {
                switchReason = "held_previous";
                return this.currentIntent;
            }

            switchReason = best.TotalScore >= currentScore * MeaningfullyBetterMultiplier
                ? "meaningfully_better"
                : "hold_expired";
            return this.CreateIntent(best, now);
        }

        if (this.currentIntent != null)
        {
            this.currentIntent = null;
            switchReason = "current_invalid";
            if (best == null)
            {
                return null;
            }

            return this.CreateIntent(best, now);
        }

        if (best == null)
        {
            switchReason = "no_candidate";
            return null;
        }

        switchReason = "new_intent";
        return this.CreateIntent(best, now);
    }

    private bool CurrentIntentSourceStillGenerated(IReadOnlyCollection<MovementCandidate> candidates)
    {
        if (this.currentIntent == null)
        {
            return true;
        }

        if (!RequiresSourceRefresh(this.currentIntent.Source))
        {
            return true;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Source.Equals(this.currentIntent.Source, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RequiresSourceRefresh(string source)
    {
        return source.Equals("AoE pack", StringComparison.Ordinal) ||
               source.Equals("Pack engagement", StringComparison.Ordinal) ||
               source.Equals("Tank pull lead", StringComparison.Ordinal) ||
               source.Equals(BmrSafetyEscapeSource, StringComparison.Ordinal) ||
               source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal) ||
               source.Equals(RedMageMeleeComboController.CandidateSource, StringComparison.Ordinal);
    }

    private MovementIntent CreateIntent(MovementCandidateScore score, DateTime now)
    {
        return new(
            ++this.nextIntentId,
            score.Source,
            score.Destination,
            score.AcceptanceRadius,
            score,
            now,
            now.Add(HoldDuration));
    }

    private bool ValidateCurrentIntent(MovementPlannerContext context, out MovementCandidateScore? score)
    {
        if (this.currentIntent == null)
        {
            score = null;
            return false;
        }

        var candidate = new MovementCandidate(
            this.currentIntent.Source,
            "current intent",
            this.currentIntent.Destination,
            this.currentIntent.AcceptanceRadius,
            this.currentIntent.Score.Priority,
            1f,
            this.currentIntent.Score.TargetRangeScore / 15f,
            this.currentIntent.Score.AoeScore / 15f,
            this.currentIntent.Score.HumanScore / 5f);
        score = this.ScoreCandidate(context, candidate, MovementPlannerQueryBudget.Unlimited());
        if (!score.Accepted &&
            score.RejectionReason == VNavmeshPathStatus.Pending.ToString() &&
            this.currentIntent.Score.PathStatus == VNavmeshPathStatus.Reachable.ToString() &&
            context.NowUtc <= this.currentIntent.CommittedAtUtc.Add(PendingPathHoldDuration))
        {
            score = this.currentIntent.Score;
            return true;
        }

        return score.Accepted;
    }

    private MovementCandidateScore ScoreCandidate(
        MovementPlannerContext context,
        MovementCandidate candidate,
        MovementPlannerQueryBudget queryBudget)
    {
        if (!IsFinite(candidate.Destination))
        {
            return this.Reject(context, candidate, "InvalidDestination");
        }

        if (this.IsRecentlyStuckDestination(context.NowUtc, candidate))
        {
            return this.Reject(context, candidate, "StuckRecently");
        }

        if (this.ShouldRejectObstacleBoundaryHug(context, candidate))
        {
            return this.Reject(context, candidate, "BmrBoundaryHugging");
        }

        var directDistance = Distance2D(context.PlayerPosition, candidate.Destination);
        if (directDistance < TinyMovementThreshold && candidate.Priority < MovementCandidatePriority.Defensive)
        {
            return this.Reject(context, candidate, "TinyMovement");
        }

        if (this.ShouldRejectBossCenterCrossing(context, candidate))
        {
            return this.Reject(context, candidate, "BossCenterCrossing");
        }

        if (this.ShouldHoldComfortMovementForCaster(context, candidate))
        {
            return this.Reject(context, candidate, "CasterComfortHold");
        }

        if (this.ShouldYieldComfortMovementToBmrGoals(context, candidate))
        {
            return this.Reject(context, candidate, "BmrGoalZoneActive");
        }

        if (!queryBudget.TryConsume(candidate, out var queryBudgetReason))
        {
            return this.Reject(context, candidate, queryBudgetReason);
        }

        var pointDiagnostics = vnavmesh.GetPointDiagnostics(candidate.Destination);
        if (this.ShouldRejectOffMeshDestination(candidate, pointDiagnostics))
        {
            if (!this.TrySnapOffMeshDestination(context, candidate, pointDiagnostics, out var snappedCandidate))
            {
                return this.Reject(context, candidate, "OffMeshDestination");
            }

            candidate = snappedCandidate;
            directDistance = Distance2D(context.PlayerPosition, candidate.Destination);
        }

        var insidePathfindMap = context.IsInsidePathfindMap(candidate.Destination);
        if (!insidePathfindMap && this.ShouldRequireBmrPathfindBounds(context))
        {
            return this.Reject(context, candidate, "OutsidePathfindMap");
        }

        if (!bossModSafety.TryIsPositionSafe(candidate.Destination, out var safe, out var safetyReason))
        {
            return this.Reject(context, candidate, $"BmrSafetyUnavailable:{safetyReason}");
        }

        if (!safe)
        {
            return this.Reject(context, candidate, "BmrUnsafe");
        }

        if (this.ShouldRejectBmrNavigationCell(context, candidate.Destination, out var destinationCellReason))
        {
            return this.Reject(context, candidate, destinationCellReason);
        }

        if (this.ShouldRejectDirectBlockedCandidateBeforeVnav(context, candidate, out var directBlockReason))
        {
            return this.Reject(context, candidate, directBlockReason);
        }

        var path = vnavmesh.GetPathResult(context.PlayerPosition, candidate.Destination);
        var usingPendingFallback = false;
        if (path.Status != VNavmeshPathStatus.Reachable)
        {
            if (!this.ShouldUsePendingFallback(context, candidate, path, directDistance))
            {
                return this.Reject(context, candidate, path.Status.ToString(), path);
            }

            usingPendingFallback = true;
        }

        var firstWaypoint = usingPendingFallback ? null : path.FirstWaypoint;
        var firstWaypointYawDelta = firstWaypoint.HasValue
            ? DirectionYawDelta(context.PlayerRotation, context.PlayerPosition, firstWaypoint.Value)
            : (float?)null;
        var firstWaypointDistance = firstWaypoint.HasValue
            ? Distance2D(context.PlayerPosition, firstWaypoint.Value)
            : (float?)null;
        var pathDistance = usingPendingFallback ? directDistance : path.PathDistance ?? directDistance;
        var extraPathDistance = MathF.Max(0f, pathDistance - directDistance);
        var pathDetourRatio = directDistance > 0.1f ? pathDistance / directDistance : 1f;
        if (!usingPendingFallback &&
            this.ShouldRejectHighDetourCandidate(candidate, directDistance, extraPathDistance, pathDetourRatio))
        {
            return this.Reject(context, candidate, "HighDetour", path);
        }

        var destination = candidate.Destination;
        var acceptanceRadius = candidate.AcceptanceRadius;
        var reason = candidate.Reason;
        var outputDirectDistance = directDistance;
        var route = usingPendingFallback ? "pending-direct" : "direct";
        var firstWaypointUseful = firstWaypoint.HasValue &&
                                  Distance2D(firstWaypoint.Value, candidate.Destination) > 0.5f;
        if (!usingPendingFallback &&
            firstWaypoint.HasValue &&
            firstWaypointUseful &&
            this.ShouldRouteViaFirstWaypoint(candidate, directDistance, extraPathDistance, pathDetourRatio, firstWaypointDistance, firstWaypointYawDelta))
        {
            var waypoint = firstWaypoint.Value;
            if (IsFinite(waypoint) &&
                context.IsInsidePathfindMap(waypoint) &&
                bossModSafety.TryIsPositionSafe(waypoint, out var waypointSafe, out _) &&
                waypointSafe &&
                !this.ShouldRejectBmrNavigationCell(context, waypoint, out _))
            {
                destination = waypoint;
                acceptanceRadius = MathF.Min(acceptanceRadius, PathCornerAcceptanceRadius);
                reason = $"{reason}; routing via vnav corner";
                outputDirectDistance = Distance2D(context.PlayerPosition, destination);
                route = "waypoint";
            }
        }

        if (this.ShouldCheckBmrNavigationLine(context) &&
            bossModSafety.TryCheckNavigationLine(context.PlayerPosition, destination, out var lineCheck) &&
            !lineCheck.Clear)
        {
            return this.Reject(
                context,
                candidate with
                {
                    Destination = destination,
                    AcceptanceRadius = acceptanceRadius,
                    Reason = reason
                },
                NavigationLineRejectionReason(lineCheck.Reason),
                path);
        }

        var vnavScore = usingPendingFallback
            ? 8f
            : 15f * Math.Clamp(1f - (extraPathDistance / 20f), 0.25f, 1f);
        var movementCostScore = 10f * Math.Clamp(1f - (outputDirectDistance / 30f), 0f, 1f);
        var turnScore = firstWaypointYawDelta.HasValue
            ? 10f * Math.Clamp(1f - (MathF.Abs(firstWaypointYawDelta.Value) / MathF.PI), 0f, 1f)
            : 5f;
        var previousScore = this.currentIntent == null
            ? 5f
            : 10f * Math.Clamp(1f - (Distance2D(destination, this.currentIntent.Destination) / 12f), 0f, 1f);
        var bmrSafetyScore = 20f;
        var targetScore = 15f * Math.Clamp(candidate.TargetRangeScore, 0f, 1f);
        var aoeScore = 15f * Math.Clamp(candidate.AoeScore, 0f, 1f);
        var priorityScore = PriorityScore(candidate.Priority);
        var humanScore = 5f * Math.Clamp(candidate.HumanScore, 0f, 1f);
        var total = bmrSafetyScore + vnavScore + targetScore + aoeScore + movementCostScore + turnScore + previousScore + humanScore + priorityScore;
        var breakdown = string.Create(
            CultureInfo.InvariantCulture,
            $"Bmr={bmrSafetyScore:0.0},Vnav={vnavScore:0.0},Range={targetScore:0.0},Aoe={aoeScore:0.0},Move={movementCostScore:0.0},Turn={turnScore:0.0},Prev={previousScore:0.0},Human={humanScore:0.0},Priority={priorityScore:0.0},Bounds={(insidePathfindMap ? "inside" : "vnav")},Route={route}");

        return new(
            candidate.Source,
            reason,
            destination,
            acceptanceRadius,
            candidate.Priority,
            true,
            string.Empty,
            total,
            bmrSafetyScore,
            vnavScore,
            targetScore,
            aoeScore,
            movementCostScore,
            turnScore,
            previousScore,
            humanScore,
            usingPendingFallback ? "PendingFallback" : path.Status.ToString(),
            usingPendingFallback ? null : path.PathDistance,
            outputDirectDistance,
            usingPendingFallback ? null : extraPathDistance,
            usingPendingFallback ? null : pathDetourRatio,
            usingPendingFallback ? null : path.WaypointCount,
            path.CacheAgeMilliseconds,
            firstWaypoint,
            firstWaypointDistance,
            firstWaypointYawDelta,
            breakdown);
    }

    private MovementCandidateScore Reject(
        MovementPlannerContext context,
        MovementCandidate candidate,
        string reason,
        VNavmeshPathResult? path = null)
    {
        var directDistance = IsFinite(candidate.Destination)
            ? Distance2D(context.PlayerPosition, candidate.Destination)
            : 0f;
        var pathDistance = path?.PathDistance;
        var extraPathDistance = pathDistance.HasValue
            ? MathF.Max(0f, pathDistance.Value - directDistance)
            : (float?)null;
        var pathDetourRatio = pathDistance.HasValue && directDistance > 0.1f
            ? pathDistance.Value / directDistance
            : (float?)null;
        var firstWaypointDistance = path?.FirstWaypoint.HasValue == true
            ? Distance2D(context.PlayerPosition, path.FirstWaypoint.Value)
            : (float?)null;

        return new(
            candidate.Source,
            candidate.Reason,
            candidate.Destination,
            candidate.AcceptanceRadius,
            candidate.Priority,
            false,
            reason,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            path?.Status.ToString() ?? "None",
            path?.PathDistance,
            directDistance,
            extraPathDistance,
            pathDetourRatio,
            path?.WaypointCount,
            path?.CacheAgeMilliseconds,
            path?.FirstWaypoint,
            firstWaypointDistance,
            path?.FirstWaypoint.HasValue == true
                ? DirectionYawDelta(context.PlayerRotation, context.PlayerPosition, path.FirstWaypoint.Value)
                : null,
            reason);
    }

    private bool TryGetSuppressionReason(MovementPlannerContext context, out string reason)
    {
        if (!config.Enabled)
        {
            reason = "Disabled";
            return true;
        }

        if (!config.ManageMovement)
        {
            reason = "MovementDisabled";
            return true;
        }

        if (!services.Condition[ConditionFlag.InCombat])
        {
            reason = "OutOfCombat";
            return true;
        }

        if (services.Condition[ConditionFlag.Unconscious] || context.Player.IsDead || context.Player.CurrentHp == 0)
        {
            reason = "PlayerDead";
            return true;
        }

        if (context.AutomatedMovementSuppressed)
        {
            reason = "ManualMovementSuppressed";
            return true;
        }

        if (context.BmrForcedMovement.HasValue && context.BmrForcedMovement.Value.LengthSquared() > 0.01f)
        {
            reason = "BmrForcedMovement";
            return true;
        }

        if (context.BmrForbiddenZones > 0 &&
            this.ShouldGloballySuppressForBmrSafety(context) &&
            !this.ShouldAllowBmrSafetyEscapeAssist(context))
        {
            reason = "BmrSafetyActive";
            return true;
        }

        if (context.Player.IsCasting && !IsInSlideCastWindow(context.Player))
        {
            reason = "PlayerCasting";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool ShouldGloballySuppressForBmrSafety(MovementPlannerContext context)
    {
        return context.BossModEncounterActive ||
               HasExplicitBmrMovement(context);
    }

    private bool ShouldAllowBmrSafetyEscapeAssist(MovementPlannerContext context)
    {
        if (!config.ManageMovement ||
            context.AutomatedMovementSuppressed ||
            !context.BossModEncounterActive ||
            context.BmrForbiddenZones <= 0 ||
            HasExplicitBmrMovement(context))
        {
            return false;
        }

        if (bossModSafety.TryIsPositionSafe(context.PlayerPosition, out var currentSafe, out _) && !currentSafe)
        {
            return true;
        }

        return bossModSafety.TryGetNearestNavigationBlocker(
                   context.PlayerPosition,
                   BmrSafetyEscapeBoundaryDistance,
                   includeAvoidBuffer: true,
                   out var blocker) &&
               blocker.Found;
    }

    private static bool HasExplicitBmrMovement(MovementPlannerContext context)
    {
        return context.BmrMoveRequested ||
               context.BmrMoveImminent ||
               context.BmrForcedMovement is { } forced && forced.LengthSquared() > 0.01f;
    }

    private bool ShouldUseLineOfSightReengage(MovementPlannerContext context)
    {
        if (!config.ManageTargetUptime ||
            !context.BossModEncounterActive ||
            context.Target == null ||
            context.Target.IsDead ||
            context.Target.CurrentHp == 0 ||
            !context.LineOfSight.Checked ||
            !context.LineOfSight.Blocked)
        {
            return false;
        }

        return context.BmrForbiddenZones == 0 &&
               !context.BmrMoveRequested &&
               !context.BmrMoveImminent &&
               (context.BmrForcedMovement is not { } forced || forced.LengthSquared() <= 0.01f);
    }

    private bool TryCreateLineOfSightReengageGoal(
        MovementPlannerContext context,
        [NotNullWhen(true)] out Delegate? goal,
        out string reason)
    {
        goal = null;
        reason = string.Empty;

        var members = this.resolvedMembers;
        if (members == null)
        {
            reason = "BMR planner reflection unavailable";
            return false;
        }

        if (context.Target == null)
        {
            reason = "target unavailable";
            return false;
        }

        if (context.PathfindMapCenter is not { } mapCenter)
        {
            reason = "BMR pathfind map center unavailable";
            return false;
        }

        var obstacleRegion = members.PathfindMapObstaclesField.GetValue(context.Hints);
        if (!TryCreateObstacleMapSnapshot(obstacleRegion, mapCenter, out var obstacleMap, out reason))
        {
            return false;
        }

        var targetRange = MathF.Max(1.5f, context.EngagementRange) +
                          context.Target.HitboxRadius +
                          context.PlayerHitboxRadius;
        goal = new LineOfSightReengageGoal(obstacleMap, context.Target.Position, targetRange)
            .CreateGoalDelegate(members.WPosType, members.WPosXField, members.WPosZField);
        reason = "ok";
        return true;
    }

    private bool ShouldHoldComfortMovementForCaster(MovementPlannerContext context, MovementCandidate candidate)
    {
        if (candidate.Priority != MovementCandidatePriority.Comfort ||
            context.Target == null ||
            JobRoles.GetRangeRole(context.Player) is not (RangeRole.Healer or RangeRole.MagicRanged))
        {
            return false;
        }

        var surfaceDistance = Geometry.DistanceToHitbox(
            context.PlayerPosition,
            context.PlayerHitboxRadius,
            context.Target.Position,
            context.Target.HitboxRadius);
        return surfaceDistance <= MathF.Max(CombatConstants.HealerCoverageAttackRange, context.EngagementRange) + 1f &&
               !IsInSlideCastWindow(context.Player);
    }

    private bool ShouldRejectObstacleBoundaryHug(MovementPlannerContext context, MovementCandidate candidate)
    {
        if (!IsPathRecoverySource(candidate.Source) ||
            !bossModSafety.TryGetNearestNavigationBlocker(
                context.PlayerPosition,
                ObstacleRecoveryBoundarySearchRadius,
                includeAvoidBuffer: true,
                out var blocker) ||
            blocker is not { Found: true, Point: { } blockerPoint })
        {
            return false;
        }

        var movement = new Vector2(candidate.Destination.X - context.PlayerPosition.X, candidate.Destination.Z - context.PlayerPosition.Z);
        var away = new Vector2(context.PlayerPosition.X - blockerPoint.X, context.PlayerPosition.Z - blockerPoint.Z);
        if (movement.LengthSquared() <= 0.01f || away.LengthSquared() <= 0.01f)
        {
            return false;
        }

        movement = Vector2.Normalize(movement);
        away = Vector2.Normalize(away);
        var minimumAwayDot = candidate.Source.Equals(LineOfSightRecoverySource, StringComparison.Ordinal)
            ? -0.15f
            : ObstacleRecoveryBoundaryAwayDot;
        return Vector2.Dot(movement, away) < minimumAwayDot;
    }

    private bool ShouldYieldComfortMovementToBmrGoals(MovementPlannerContext context, MovementCandidate candidate)
    {
        return candidate.Priority == MovementCandidatePriority.Comfort &&
               context.BossModEncounterActive &&
               (context.BmrForbiddenZones > 0 ||
                context.BmrMoveRequested ||
                context.BmrMoveImminent ||
                context.BmrForcedMovement is { } forced && forced.LengthSquared() > 0.01f);
    }

    private bool ShouldRejectBossCenterCrossing(MovementPlannerContext context, MovementCandidate candidate)
    {
        if (candidate.Priority == MovementCandidatePriority.Defensive ||
            candidate.Source.Equals("Boss center avoidance", StringComparison.Ordinal) ||
            context.Target == null ||
            !context.BossModEncounterActive ||
            context.Target.HitboxRadius < BossCenterRouteBossRadius)
        {
            return false;
        }

        var clearance = BossCenterAvoidanceController.AvoidanceRadius(context.Target.HitboxRadius) + BossCenterRouteBuffer;
        if (Distance2D(context.PlayerPosition, context.Target.Position) <= clearance)
        {
            return false;
        }

        return BossCenterRouteCrosses(context.PlayerPosition, candidate.Destination, context.Target.Position, clearance);
    }

    internal static bool BossCenterRouteCrosses(Vector3 from, Vector3 to, Vector3 bossCenter, float clearanceRadius)
    {
        if (!float.IsFinite(clearanceRadius) || clearanceRadius <= 0f)
        {
            return false;
        }

        return DistancePointToSegment2D(bossCenter, from, to) <= clearanceRadius;
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 a, Vector3 b)
    {
        var ax = a.X;
        var az = a.Z;
        var bx = b.X;
        var bz = b.Z;
        var abx = bx - ax;
        var abz = bz - az;
        var lengthSq = (abx * abx) + (abz * abz);
        if (lengthSq <= 0.0001f)
        {
            return Distance2D(point, a);
        }

        var t = (((point.X - ax) * abx) + ((point.Z - az) * abz)) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        var closest = new Vector3(ax + (abx * t), point.Y, az + (abz * t));
        return Distance2D(point, closest);
    }

    private bool ShouldRequireBmrPathfindBounds(MovementPlannerContext context)
    {
        return context.BossModEncounterActive ||
               context.BmrMoveRequested ||
               context.BmrMoveImminent ||
               context.BmrGoalZones > 0 ||
               context.BmrForbiddenZones > 0;
    }

    private bool ShouldCheckBmrNavigationLine(MovementPlannerContext context)
    {
        return context.BossModEncounterActive ||
               context.BmrMoveRequested ||
               context.BmrMoveImminent ||
               context.BmrGoalZones > 0 ||
               context.PathfindMapCenter.HasValue;
    }

    private bool ShouldRejectBmrNavigationCell(MovementPlannerContext context, Vector3 position, out string reason)
    {
        reason = string.Empty;
        if (!this.ShouldCheckBmrNavigationLine(context))
        {
            return false;
        }

        if (!bossModSafety.TryIsNavigationCellSafe(position, out var safe, out var cellReason) || safe)
        {
            return false;
        }

        reason = NavigationCellRejectionReason(cellReason);
        return true;
    }

    private static string NavigationCellRejectionReason(string cellReason)
    {
        return cellReason.Contains("active danger", StringComparison.OrdinalIgnoreCase) ||
               cellReason.Contains("future danger", StringComparison.OrdinalIgnoreCase)
            ? "BmrPathActiveDanger"
            : "BmrPathBlocked";
    }

    private static string NavigationLineRejectionReason(string lineReason)
    {
        return lineReason.Contains("active danger", StringComparison.OrdinalIgnoreCase) ||
               lineReason.Contains("future danger", StringComparison.OrdinalIgnoreCase)
            ? "BmrPathActiveDanger"
            : "BmrPathBlocked";
    }

    private bool ShouldUsePendingFallback(
        MovementPlannerContext context,
        MovementCandidate candidate,
        VNavmeshPathResult path,
        float directDistance)
    {
        if (path.Status != VNavmeshPathStatus.Pending ||
            context.BmrForbiddenZones > 0 ||
            context.BmrMoveRequested ||
            context.BmrMoveImminent ||
            context.BmrForcedMovement is { } forced && forced.LengthSquared() > 0.01f ||
            !IsPendingFallbackSource(candidate.Source) ||
            directDistance > MaxPendingFallbackDirectDistance)
        {
            return false;
        }

        if (this.ShouldCheckBmrNavigationLine(context) &&
            bossModSafety.TryCheckNavigationLine(context.PlayerPosition, candidate.Destination, out var lineCheck) &&
            !lineCheck.Clear)
        {
            return false;
        }

        return true;
    }

    private static bool IsPathRecoverySource(string source)
    {
        return source.Equals(ObstacleRecoverySource, StringComparison.Ordinal) ||
               source.Equals(LineOfSightRecoverySource, StringComparison.Ordinal);
    }

    private static bool IsPendingFallbackSource(string source)
    {
        return source.Equals("Pack engagement", StringComparison.Ordinal) ||
               source.Equals("AoE pack", StringComparison.Ordinal) ||
               source.Equals("Tank pull lead", StringComparison.Ordinal) ||
               source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal);
    }

    private static bool IsTrashEngagementSource(string source)
    {
        return source.Equals("Pack engagement", StringComparison.Ordinal) ||
               source.Equals("AoE pack", StringComparison.Ordinal) ||
               source.Equals("Tank pull lead", StringComparison.Ordinal) ||
               source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal);
    }

    private bool IsPathRecoveryIntent(MovementIntent intent)
    {
        return IsPathRecoverySource(intent.Source);
    }

    private bool ShouldTrashPriorityTakeover(MovementIntent current, MovementCandidateScore best, float currentScore)
    {
        if (!current.Source.Equals("Target range", StringComparison.Ordinal) ||
            best.Priority < MovementCandidatePriority.ActiveAoe ||
            !IsTrashEngagementSource(best.Source))
        {
            return false;
        }

        return best.TotalScore >= currentScore - CloseScoreTie;
    }

    private bool ShouldRejectDirectBlockedCandidateBeforeVnav(
        MovementPlannerContext context,
        MovementCandidate candidate,
        out string reason)
    {
        reason = string.Empty;
        if (!this.ShouldCheckBmrNavigationLine(context) ||
            IsPathRecoverySource(candidate.Source) ||
            candidate.Source.Equals(LineOfSightReengageSource, StringComparison.Ordinal) ||
            candidate.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal))
        {
            return false;
        }

        if (!bossModSafety.TryCheckNavigationLine(context.PlayerPosition, candidate.Destination, out var lineCheck) ||
            lineCheck.Clear)
        {
            return false;
        }

        reason = NavigationLineRejectionReason(lineCheck.Reason);
        return true;
    }

    private bool ShouldRejectOffMeshDestination(MovementCandidate candidate, VNavmeshPointDiagnostics point)
    {
        if (string.Equals(point.Status, "NoMeshPoint", StringComparison.Ordinal))
        {
            return true;
        }

        if (point.NearestReachablePointDistance > MaxOffMeshDestinationDistance)
        {
            return true;
        }

        return point.NearestReachablePoint.HasValue &&
               MathF.Abs(point.NearestReachablePoint.Value.Y - candidate.Destination.Y) > MaxOffMeshVerticalDistance;
    }

    private bool TrySnapOffMeshDestination(
        MovementPlannerContext context,
        MovementCandidate candidate,
        VNavmeshPointDiagnostics point,
        out MovementCandidate snapped)
    {
        snapped = candidate;
        if (!IsOffMeshSnapSource(candidate.Source))
        {
            return false;
        }

        var destination = (Vector3?)null;
        var reason = string.Empty;
        if (point.NearestReachablePoint.HasValue &&
            point.NearestReachablePointDistance is <= MaxOffMeshSnapDistance &&
            MathF.Abs(point.NearestReachablePoint.Value.Y - candidate.Destination.Y) <= MaxOffMeshSnapVerticalDistance)
        {
            destination = point.NearestReachablePoint.Value;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"snapped {point.NearestReachablePointDistance.Value:0.0}y to reachable navmesh");
        }
        else if (point.FloorPoint.HasValue &&
                 point.FloorPointDistance is <= MaxOffMeshSnapDistance &&
                 MathF.Abs(point.FloorPoint.Value.Y - candidate.Destination.Y) <= MaxOffMeshSnapVerticalDistance)
        {
            destination = point.FloorPoint.Value;
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"snapped {point.FloorPointDistance.Value:0.0}y to floor navmesh");
        }

        if (!destination.HasValue ||
            Distance2D(context.PlayerPosition, destination.Value) < TinyMovementThreshold)
        {
            return false;
        }

        snapped = candidate with
        {
            Destination = destination.Value,
            Reason = $"{candidate.Reason}; {reason}"
        };
        return true;
    }

    private static bool IsOffMeshSnapSource(string source)
    {
        return IsPathRecoverySource(source) ||
               source.Equals(BmrSafetyEscapeSource, StringComparison.Ordinal) ||
               source.Equals("Pack engagement", StringComparison.Ordinal) ||
               source.Equals("AoE pack", StringComparison.Ordinal) ||
               source.Equals("Tank pull lead", StringComparison.Ordinal) ||
               source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal);
    }

    private bool ShouldRejectHighDetourCandidate(
        MovementCandidate candidate,
        float directDistance,
        float extraPathDistance,
        float pathDetourRatio)
    {
        if (candidate.Priority == MovementCandidatePriority.Defensive ||
            directDistance < HighDetourDirectDistance ||
            candidate.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal) ||
            candidate.Source.Equals("Tank pull lead", StringComparison.Ordinal))
        {
            return false;
        }

        return extraPathDistance >= HighDetourExtraDistance ||
               pathDetourRatio >= HighDetourRatio;
    }

    private bool ShouldRouteViaFirstWaypoint(
        MovementCandidate candidate,
        float directDistance,
        float extraPathDistance,
        float pathDetourRatio,
        float? firstWaypointDistance,
        float? firstWaypointYawDelta)
    {
        if (candidate.Priority == MovementCandidatePriority.Defensive ||
            !firstWaypointDistance.HasValue ||
            firstWaypointDistance.Value < 1f ||
            directDistance < PathCornerMinDistance)
        {
            return false;
        }

        return extraPathDistance >= PathCornerMinExtraDistance ||
               pathDetourRatio >= PathCornerMinDetourRatio ||
               MathF.Abs(firstWaypointYawDelta ?? 0f) >= PathCornerMinYawDelta;
    }

    private static float PriorityScore(MovementCandidatePriority priority)
    {
        return priority switch
        {
            MovementCandidatePriority.Defensive => 35f,
            MovementCandidatePriority.ActiveAoe => 20f,
            MovementCandidatePriority.PathRecovery => 16f,
            MovementCandidatePriority.TargetRange => 5f,
            _ => 0f
        };
    }

    private static bool IsInSlideCastWindow(IBattleChara player)
    {
        if (!player.IsCasting)
        {
            return false;
        }

        var total = player.TotalCastTime;
        if (!float.IsFinite(total) || total <= 0f)
        {
            return false;
        }

        var current = player.CurrentCastTime;
        if (!float.IsFinite(current) || current < 0f)
        {
            return false;
        }

        return current + SlideCastWindowSeconds >= total;
    }

    private static void SuppressBroadTrashCandidates(MovementPlannerContext context, List<MovementCandidate> candidates)
    {
        candidates.RemoveAll(candidate =>
            candidate.Source.Equals("Target range", StringComparison.Ordinal) ||
            (candidate.Source.Equals("Pack engagement", StringComparison.Ordinal) &&
             Distance2D(context.PlayerPosition, candidate.Destination) > MaxPendingFallbackDirectDistance));
    }

    private static void SuppressNonBmrSafetyEscapeCandidates(List<MovementCandidate> candidates)
    {
        candidates.RemoveAll(candidate => !candidate.Source.Equals(BmrSafetyEscapeSource, StringComparison.Ordinal));
    }

    private void AddTargetRangeCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!config.ManageTargetUptime || context.Target == null)
        {
            return;
        }

        var target = context.Target;
        if (target.IsDead || target.CurrentHp == 0)
        {
            return;
        }

        if (target is IBattleNpc npc && npc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            return;
        }

        var currentSurfaceDistance = Geometry.DistanceToHitbox(
            context.PlayerPosition,
            context.PlayerHitboxRadius,
            target.Position,
            target.HitboxRadius);
        var engagementRange = MathF.Max(1.5f, context.EngagementRange);
        if (currentSurfaceDistance <= engagementRange)
        {
            return;
        }

        var desiredSurfaceDistance = Math.Clamp(engagementRange * 0.82f, 1.5f, engagementRange);
        var centerDistance = context.PlayerHitboxRadius + target.HitboxRadius + desiredSurfaceDistance;
        var target2 = new Vector2(target.Position.X, target.Position.Z);
        var player2 = new Vector2(context.PlayerPosition.X, context.PlayerPosition.Z);
        var fromTargetToPlayer = player2 - target2;
        if (fromTargetToPlayer.LengthSquared() <= 0.01f)
        {
            fromTargetToPlayer = new Vector2(0f, -1f);
        }

        fromTargetToPlayer = Vector2.Normalize(fromTargetToPlayer);
        foreach (var direction in this.EnumerateHumanLikeTargetDirections(target, fromTargetToPlayer))
        {
            var dest2 = target2 + direction * centerDistance;
            var dest = new Vector3(dest2.X, target.Position.Y, dest2.Y);
            var frontPenalty = this.TargetFrontPenalty(target, dest);
            candidates.Add(new(
                "Target range",
                string.Create(CultureInfo.InvariantCulture, $"target surface {currentSurfaceDistance:0.0}->{desiredSurfaceDistance:0.0}y"),
                dest,
                MathF.Max(1.5f, MathF.Min(3f, engagementRange * 0.25f)),
                MovementCandidatePriority.TargetRange,
                1f,
                Math.Clamp((currentSurfaceDistance - engagementRange) / 10f, 0.35f, 1f),
                0f,
                0.65f - frontPenalty));
        }
    }

    private void AddBmrSafetyEscapeCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!config.ManageMovement ||
            context.AutomatedMovementSuppressed)
        {
            return;
        }

        var safetyKnown = bossModSafety.TryIsPositionSafe(context.PlayerPosition, out var currentSafe, out var currentSafetyReason);
        var currentUnsafe = safetyKnown && !currentSafe;
        var safetyPressure = context.BmrForbiddenZones > 0;
        if (!currentUnsafe && !safetyPressure)
        {
            return;
        }

        var nearBoundary = safetyPressure &&
            bossModSafety.TryGetNearestNavigationBlocker(
                context.PlayerPosition,
                BmrSafetyEscapeBoundaryDistance,
                includeAvoidBuffer: true,
                out var blocker) &&
            blocker.Found;
        if (!currentUnsafe &&
            safetyKnown &&
            currentSafe &&
            !nearBoundary)
        {
            return;
        }

        if (!safetyKnown &&
            !nearBoundary)
        {
            return;
        }

        if (bossModSafety.TryGetSafeMovementIntent(context.PlayerPosition, out var bmrDestination, out var bmrReason))
        {
            candidates.Add(new(
                BmrSafetyEscapeSource,
                bmrReason,
                bmrDestination,
                BmrSafetyEscapeAcceptanceRadius,
                MovementCandidatePriority.Defensive,
                1f,
                0f,
                0f,
                0.95f));
        }

        if (bossModSafety.TryFindNearestSafeNavigationPoint(
                context.PlayerPosition,
                BmrSafetyEscapeSearchRadius,
                out var safePoint))
        {
            var reason = safetyKnown && !currentSafe
                ? string.Create(CultureInfo.InvariantCulture, $"escaping BMR danger: {currentSafetyReason}; {safePoint.Reason}")
                : string.Create(CultureInfo.InvariantCulture, $"stepping clear of BMR boundary: {safePoint.Reason}");
            candidates.Add(new(
                BmrSafetyEscapeSource,
                reason,
                safePoint.Point,
                BmrSafetyEscapeAcceptanceRadius,
                MovementCandidatePriority.Defensive,
                1f,
                0f,
                0f,
                0.95f));
        }
    }

    private void AddLineOfSightRecoveryCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!config.ManageTargetUptime ||
            context.BossModEncounterActive ||
            context.Target == null ||
            !context.LineOfSight.Checked ||
            !context.LineOfSight.Blocked ||
            context.LineOfSight.CombatClear ||
            this.ShouldSuppressLineOfSightRecovery(context))
        {
            return;
        }

        var target = context.Target;
        if (target.IsDead || target.CurrentHp == 0)
        {
            return;
        }

        var currentSurfaceDistance = Geometry.DistanceToHitbox(
            context.PlayerPosition,
            context.PlayerHitboxRadius,
            target.Position,
            target.HitboxRadius);

        var pathToTarget = vnavmesh.GetPathResult(context.PlayerPosition, target.Position);
        if (pathToTarget is { Status: VNavmeshPathStatus.Reachable, FirstWaypoint: { } firstWaypoint } &&
            Distance2D(context.PlayerPosition, firstWaypoint) is >= 1f and <= LineOfSightRecoveryMaxAnchorDistance)
        {
            candidates.Add(new(
                LineOfSightRecoverySource,
                $"following reachable route to line of sight; {context.LineOfSight.Reason}",
                new Vector3(firstWaypoint.X, context.PlayerPosition.Y, firstWaypoint.Z),
                LineOfSightRecoveryAcceptanceRadius,
                MovementCandidatePriority.PathRecovery,
                0.85f,
                0.65f,
                0f,
                0.95f));
        }

        var toTarget = new Vector2(target.Position.X - context.PlayerPosition.X, target.Position.Z - context.PlayerPosition.Z);
        if (toTarget.LengthSquared() <= 0.01f)
        {
            return;
        }

        toTarget = Vector2.Normalize(toTarget);
        foreach (var direction in this.EnumerateLineOfSightRecoveryDirections(context, toTarget))
        {
            foreach (var distance in LineOfSightRecoveryDistances)
            {
                var dest2 = new Vector2(context.PlayerPosition.X, context.PlayerPosition.Z) + direction * distance;
                var dest = new Vector3(dest2.X, context.PlayerPosition.Y, dest2.Y);
                if (!this.TryBuildCandidateLineOfSight(dest, target, out var candidateLine) ||
                    candidateLine.Blocked)
                {
                    continue;
                }

                var landingSurfaceDistance = Geometry.DistanceToHitbox(
                    dest,
                    context.PlayerHitboxRadius,
                    target.Position,
                    target.HitboxRadius);
                var progress = currentSurfaceDistance - landingSurfaceDistance;
                candidates.Add(new(
                    LineOfSightRecoverySource,
                    $"restores line of sight; {context.LineOfSight.Reason}",
                    dest,
                    LineOfSightRecoveryAcceptanceRadius,
                    MovementCandidatePriority.PathRecovery,
                    1f,
                    Math.Clamp((progress + 3f) / 8f, 0.55f, 0.9f),
                    0f,
                    1f));
            }
        }
    }

    private void AddObstacleRecoveryCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates)
    {
        if (!config.ManageTargetUptime ||
            context.BossModEncounterActive ||
            context.Target == null ||
            !this.ShouldCheckBmrNavigationLine(context))
        {
            return;
        }

        var target = context.Target;
        if (target.IsDead || target.CurrentHp == 0)
        {
            return;
        }

        var currentSurfaceDistance = Geometry.DistanceToHitbox(
            context.PlayerPosition,
            context.PlayerHitboxRadius,
            target.Position,
            target.HitboxRadius);
        var engagementRange = MathF.Max(1.5f, context.EngagementRange);
        if (currentSurfaceDistance <= engagementRange)
        {
            return;
        }

        if (!bossModSafety.TryCheckNavigationLine(context.PlayerPosition, target.Position, out var targetLine) ||
            targetLine.Clear ||
            !targetLine.BlockedDistance.HasValue ||
            targetLine.BlockedDistance.Value > ObstacleRecoveryTriggerDistance)
        {
            return;
        }

        var toTarget = new Vector2(target.Position.X - context.PlayerPosition.X, target.Position.Z - context.PlayerPosition.Z);
        if (toTarget.LengthSquared() <= 0.01f)
        {
            return;
        }

        toTarget = Vector2.Normalize(toTarget);

        if (bossModSafety.TryGetNearestNavigationBlocker(
                context.PlayerPosition,
                ObstacleRecoveryBoundarySearchRadius,
                includeAvoidBuffer: true,
                out var blocker) &&
            blocker is { Found: true, Point: { } blockerPoint, Distance: { } blockerDistance })
        {
            this.AddObstacleBoundaryRecoveryCandidates(context, target, toTarget, blockerPoint, blockerDistance, currentSurfaceDistance, candidates);
            return;
        }

        foreach (var direction in EnumerateObstacleRecoveryDirections(toTarget))
        {
            foreach (var distance in ObstacleRecoveryDistances)
            {
                var dest2 = new Vector2(context.PlayerPosition.X, context.PlayerPosition.Z) + direction * distance;
                var dest = new Vector3(dest2.X, context.PlayerPosition.Y, dest2.Y);
                var landingSurfaceDistance = Geometry.DistanceToHitbox(dest, context.PlayerHitboxRadius, target.Position, target.HitboxRadius);
                var progress = currentSurfaceDistance - landingSurfaceDistance;
                if (progress < -1.5f)
                {
                    continue;
                }

                candidates.Add(new(
                    ObstacleRecoverySource,
                    string.Create(CultureInfo.InvariantCulture, $"routing around blocked BMR line at {targetLine.BlockedDistance.Value:0.0}y"),
                    dest,
                    ObstacleRecoveryAcceptanceRadius,
                    MovementCandidatePriority.PathRecovery,
                    0.7f,
                    Math.Clamp((progress + 2f) / 6f, 0.45f, 0.8f),
                    0f,
                    0.8f));
            }
        }
    }

    private bool ShouldSuppressLineOfSightRecovery(MovementPlannerContext context)
    {
        return context.BmrForbiddenZones > 0 ||
               context.BmrMoveRequested ||
               context.BmrMoveImminent ||
               context.BmrForcedMovement is { } forced && forced.LengthSquared() > 0.01f;
    }

    private bool TryBuildCandidateLineOfSight(
        Vector3 from,
        IBattleChara target,
        [NotNullWhen(true)] out MovementLineOfSightDiagnostics? diagnostics)
    {
        diagnostics = this.BuildLineOfSightDiagnostics(from, target);
        return diagnostics.Checked;
    }

    private void AddObstacleBoundaryRecoveryCandidates(
        MovementPlannerContext context,
        IBattleChara target,
        Vector2 toTarget,
        Vector3 blockerPoint,
        float blockerDistance,
        float currentSurfaceDistance,
        ICollection<MovementCandidate> candidates)
    {
        var away = new Vector2(context.PlayerPosition.X - blockerPoint.X, context.PlayerPosition.Z - blockerPoint.Z);
        if (away.LengthSquared() <= 0.01f)
        {
            away = -toTarget;
        }

        away = Vector2.Normalize(away);
        foreach (var direction in EnumerateObstacleBoundaryRecoveryDirections(away, toTarget))
        {
            foreach (var distance in ObstacleBoundaryRecoveryDistances)
            {
                var dest2 = new Vector2(context.PlayerPosition.X, context.PlayerPosition.Z) + direction * distance;
                var dest = new Vector3(dest2.X, context.PlayerPosition.Y, dest2.Y);
                var landingSurfaceDistance = Geometry.DistanceToHitbox(dest, context.PlayerHitboxRadius, target.Position, target.HitboxRadius);
                var progress = currentSurfaceDistance - landingSurfaceDistance;
                if (progress < -2f)
                {
                    continue;
                }

                candidates.Add(new(
                    ObstacleRecoverySource,
                    string.Create(CultureInfo.InvariantCulture, $"stepping away from {blockerDistance:0.0}y BMR boundary"),
                    dest,
                    ObstacleRecoveryAcceptanceRadius,
                    MovementCandidatePriority.PathRecovery,
                    0.8f,
                    Math.Clamp((progress + 2f) / 6f, 0.35f, 0.75f),
                    0f,
                    0.9f));
            }
        }
    }

    private IEnumerable<Vector2> EnumerateHumanLikeTargetDirections(IBattleChara target, Vector2 fromTargetToPlayer)
    {
        yield return fromTargetToPlayer;

        var targetForward = new Vector2(MathF.Sin(target.Rotation), MathF.Cos(target.Rotation));
        var targetBack = -targetForward;
        var targetLeft = new Vector2(-targetForward.Y, targetForward.X);
        var targetRight = -targetLeft;
        yield return targetBack;
        yield return Vector2.Normalize(targetBack + targetLeft);
        yield return Vector2.Normalize(targetBack + targetRight);
        yield return targetLeft;
        yield return targetRight;
    }

    private static IEnumerable<Vector2> EnumerateObstacleRecoveryDirections(Vector2 toTarget)
    {
        var left = new Vector2(-toTarget.Y, toTarget.X);
        var right = -left;
        yield return left;
        yield return right;
        yield return Vector2.Normalize((toTarget * 0.55f) + left);
        yield return Vector2.Normalize((toTarget * 0.55f) + right);
        yield return Vector2.Normalize((-toTarget * 0.35f) + left);
        yield return Vector2.Normalize((-toTarget * 0.35f) + right);
    }

    private IEnumerable<Vector2> EnumerateLineOfSightRecoveryDirections(MovementPlannerContext context, Vector2 toTarget)
    {
        var left = new Vector2(-toTarget.Y, toTarget.X);
        var right = -left;

        yield return left;
        yield return right;
        yield return Vector2.Normalize((toTarget * 0.45f) + left);
        yield return Vector2.Normalize((toTarget * 0.45f) + right);
        yield return Vector2.Normalize((-toTarget * 0.25f) + left);
        yield return Vector2.Normalize((-toTarget * 0.25f) + right);

        if (context.LineOfSight.BlockedPoint is not { } blockedPoint)
        {
            yield break;
        }

        var awayFromBlocker = new Vector2(context.PlayerPosition.X - blockedPoint.X, context.PlayerPosition.Z - blockedPoint.Z);
        if (awayFromBlocker.LengthSquared() <= 0.01f)
        {
            yield break;
        }

        awayFromBlocker = Vector2.Normalize(awayFromBlocker);
        var blockerTangent = new Vector2(-awayFromBlocker.Y, awayFromBlocker.X);
        yield return Vector2.Normalize((blockerTangent * 0.85f) + (toTarget * 0.25f));
        yield return Vector2.Normalize((-blockerTangent * 0.85f) + (toTarget * 0.25f));
    }

    private static IEnumerable<Vector2> EnumerateObstacleBoundaryRecoveryDirections(Vector2 away, Vector2 toTarget)
    {
        var tangent = new Vector2(-away.Y, away.X);
        var forwardBlend = Vector2.Dot(away, toTarget) > -0.35f
            ? Vector2.Normalize(away + (toTarget * 0.35f))
            : away;

        yield return forwardBlend;
        yield return away;
        yield return Vector2.Normalize((away * 0.85f) + (tangent * 0.25f));
        yield return Vector2.Normalize((away * 0.85f) - (tangent * 0.25f));
    }

    private float TargetFrontPenalty(IBattleChara target, Vector3 destination)
    {
        var toDestination = new Vector2(destination.X - target.Position.X, destination.Z - target.Position.Z);
        if (toDestination.LengthSquared() <= 0.01f)
        {
            return 0.35f;
        }

        toDestination = Vector2.Normalize(toDestination);
        var targetForward = new Vector2(MathF.Sin(target.Rotation), MathF.Cos(target.Rotation));
        return Vector2.Dot(toDestination, targetForward) > 0.5f ? 0.35f : 0f;
    }

    private bool TryBuildContext(object hints, DateTime now, [NotNullWhen(true)] out MovementPlannerContext? context, out string reason)
    {
        context = null;
        if (!this.EnsureResolved(hints.GetType(), out reason))
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "LocalPlayerUnavailable";
            return false;
        }

        var members = this.resolvedMembers!;
        var forcedMovement = ReadVector3(members.ForcedMovementField.GetValue(hints));
        var goalZones = CountCollection(members.GoalZonesField.GetValue(hints));
        var forbiddenZones = CountCollection(members.ForbiddenZonesField.GetValue(hints));
        var mapCenter = this.ReadWPosAsVector3(members.PathfindMapCenterField.GetValue(hints), player.Position.Y);
        _ = BossModPathfindBoundsSnapshot.TryCreate(
            hints,
            members.PathfindMapCenterField,
            members.PathfindMapBoundsField,
            members.WPosXField,
            members.WPosZField,
            members.WDirConstructor,
            members.BoundsContainsMethod,
            out var pathfindBounds);
        var target = services.TargetManager.Target as IBattleChara;
        var lineOfSightDiagnostics = this.BuildLineOfSightDiagnostics(player.Position, target);
        context = new MovementPlannerContext
        {
            Hints = hints,
            NowUtc = now,
            Player = player,
            PlayerPosition = player.Position,
            PlayerRotation = player.Rotation,
            PlayerHitboxRadius = player.HitboxRadius,
            Target = target,
            EngagementRange = jobRangeProvider.EngagementRange,
            PackAoeRange = jobRangeProvider.PackAoeRange,
            AutomatedMovementSuppressed = automatedMovementSuppressed(),
            BmrMoveRequested = this.bmrMoveRequested,
            BmrMoveImminent = this.bmrMoveImminent,
            BossModEncounterActive = this.bossModEncounterActive,
            BmrGoalZones = goalZones,
            BmrForbiddenZones = forbiddenZones,
            BmrForcedMovement = forcedMovement,
            PathfindMapCenter = mapCenter,
            LineOfSight = lineOfSightDiagnostics,
            IsInsidePathfindMap = destination => pathfindBounds?.Contains(destination) ?? false
        };
        reason = string.Empty;
        return true;
    }

    private MovementLineOfSightDiagnostics BuildLineOfSightDiagnostics(Vector3 from, IBattleChara? target)
    {
        if (target == null)
        {
            return MovementLineOfSightDiagnostics.NotChecked("no target");
        }

        if (target.IsDead || target.CurrentHp == 0)
        {
            return MovementLineOfSightDiagnostics.NotChecked("target dead");
        }

        var combatChecked = lineOfSight.TryHasLineOfSight(from, target.Position, out var combatClear, out var combatReason);
        var navigationChecked = bossModSafety.TryCheckNavigationLine(from, target.Position, out var navigationLine);
        var navigationClear = !navigationChecked || navigationLine.Clear;
        var blocked = (combatChecked && !combatClear) || (navigationChecked && !navigationLine.Clear);
        var reason = BuildLineOfSightReason(combatChecked, combatClear, combatReason, navigationChecked, navigationLine);
        return new(
            combatChecked || navigationChecked,
            blocked,
            !combatChecked || combatClear,
            navigationClear,
            reason,
            navigationChecked ? navigationLine.BlockedPoint : null,
            navigationChecked ? navigationLine.BlockedDistance : null);
    }

    private static string BuildLineOfSightReason(
        bool combatChecked,
        bool combatClear,
        string combatReason,
        bool navigationChecked,
        BossModNavigationLineCheck navigationLine)
    {
        var combat = combatChecked
            ? combatClear ? "combat clear" : "combat blocked"
            : $"combat unknown:{combatReason}";
        var navigation = navigationChecked
            ? navigationLine.Clear ? "navigation clear" : $"navigation blocked:{navigationLine.Reason}"
            : $"navigation unknown:{navigationLine.Reason}";
        return $"{combat}; {navigation}";
    }

    private bool EnsureResolved(Type hintsType, out string reason)
    {
        if (this.resolvedHintsType == hintsType && this.resolvedMembers != null)
        {
            reason = string.Empty;
            return true;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var wposType = hintsType.Assembly.GetType("BossMod.WPos");
        var wdirType = hintsType.Assembly.GetType("BossMod.WDir");
        var xField = wposType?.GetField("X", Flags);
        var zField = wposType?.GetField("Z", Flags);
        var forcedMovement = hintsType.GetField("ForcedMovement", Flags);
        var goalZones = hintsType.GetField("GoalZones", Flags);
        var forbiddenZones = hintsType.GetField("ForbiddenZones", Flags);
        var pathfindMapCenter = hintsType.GetField("PathfindMapCenter", Flags);
        var pathfindMapBounds = hintsType.GetField("PathfindMapBounds", Flags);
        var pathfindMapObstacles = hintsType.GetField("PathfindMapObstacles", Flags);
        var wdirConstructor = wdirType?.GetConstructor([typeof(float), typeof(float)]);
        var boundsContains = wdirType == null
            ? null
            : pathfindMapBounds?.FieldType.GetMethods(Flags)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "Contains")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           (parameters[0].ParameterType == wdirType ||
                            parameters[0].ParameterType.IsByRef && parameters[0].ParameterType.GetElementType() == wdirType);
                });

        if (wposType == null ||
            wdirType == null ||
            xField == null ||
            zField == null ||
            forcedMovement == null ||
            goalZones == null ||
            forbiddenZones == null ||
            pathfindMapCenter == null ||
            pathfindMapBounds == null ||
            pathfindMapObstacles == null ||
            wdirConstructor == null ||
            boundsContains == null)
        {
            reason = $"BmrPlannerReflectionUnavailable:{FormatMissing(
                (wposType == null, "BossMod.WPos"),
                (wdirType == null, "BossMod.WDir"),
                (xField == null, "BossMod.WPos.X"),
                (zField == null, "BossMod.WPos.Z"),
                (forcedMovement == null, "AIHints.ForcedMovement"),
                (goalZones == null, "AIHints.GoalZones"),
                (forbiddenZones == null, "AIHints.ForbiddenZones"),
                (pathfindMapCenter == null, "AIHints.PathfindMapCenter"),
                (pathfindMapBounds == null, "AIHints.PathfindMapBounds"),
                (pathfindMapObstacles == null, "AIHints.PathfindMapObstacles"),
                (wdirConstructor == null, "BossMod.WDir(float,float)"),
                (boundsContains == null, "ArenaBounds.Contains"))}";
            this.resolvedHintsType = null;
            this.resolvedMembers = null;
            return false;
        }

        this.resolvedHintsType = hintsType;
        this.resolvedMembers = new(
            wposType,
            xField,
            zField,
            forcedMovement,
            goalZones,
            forbiddenZones,
            pathfindMapCenter,
            pathfindMapBounds,
            pathfindMapObstacles,
            wdirConstructor,
            boundsContains);
        reason = string.Empty;
        return true;
    }

    private Vector3? ReadWPosAsVector3(object? value, float y)
    {
        var members = this.resolvedMembers;
        if (members == null || value == null)
        {
            return null;
        }

        var x = ReadFloat(members.WPosXField.GetValue(value));
        var z = ReadFloat(members.WPosZField.GetValue(value));
        return x.HasValue && z.HasValue ? new Vector3(x.Value, y, z.Value) : null;
    }

    private MovementPlannerDiagnostics BuildDiagnostics(
        DateTime now,
        string switchReason,
        string suppressionReason,
        int generatedCount,
        int acceptedCount,
        IReadOnlyDictionary<string, int> rejectedByReason,
        IReadOnlyList<MovementCandidateScore> topCandidates,
        MovementPlannerContext? context,
        MovementCandidateScore? chosenScore,
        MovementPlannerQueryBudget? queryBudget = null)
    {
        var holdRemaining = this.currentIntent == null
            ? 0d
            : Math.Max(0d, (this.currentIntent.HoldUntilUtc - now).TotalMilliseconds);
        var vnavmeshProbe = chosenScore ??
                            topCandidates.FirstOrDefault(score => score.PathStatus != "None") ??
                            topCandidates.FirstOrDefault();
        var vnavmeshDestination = vnavmeshProbe == null
            ? null
            : vnavmesh.GetPointDiagnostics(vnavmeshProbe.Destination);
        var routeMemory = this.BuildRouteMemoryDiagnostics(topCandidates, chosenScore, queryBudget);
        return new(
            this.currentIntent?.IntentId.ToString(CultureInfo.InvariantCulture) ?? "<none>",
            this.currentIntent?.Source ?? "<none>",
            this.currentIntent?.Destination,
            this.currentIntent?.AcceptanceRadius,
            holdRemaining,
            switchReason,
            suppressionReason,
            generatedCount,
            acceptedCount,
            rejectedByReason,
            topCandidates,
            chosenScore?.ScoreBreakdown ?? "<none>",
            chosenScore?.PathStatus ?? "None",
            chosenScore?.PathDistance,
            chosenScore?.DirectDistance,
            chosenScore?.ExtraPathDistance,
            chosenScore?.PathDetourRatio,
            chosenScore?.PathWaypointCount,
            chosenScore?.PathCacheAgeMilliseconds,
            chosenScore?.FirstWaypoint,
            chosenScore?.FirstWaypointDistance,
            chosenScore?.FirstWaypointYawDelta,
            vnavmesh.GetRuntimeDiagnostics(),
            vnavmeshProbe?.Source ?? "<none>",
            vnavmeshDestination,
            context?.LineOfSight ?? MovementLineOfSightDiagnostics.NotChecked("not evaluated"),
            context?.BmrForcedMovement,
            context?.BmrForbiddenZones ?? 0,
            context?.BmrMoveRequested ?? this.bmrMoveRequested,
            context?.BmrMoveImminent ?? this.bmrMoveImminent,
            routeMemory);
    }

    private TrashRouteMemoryDiagnostics BuildRouteMemoryDiagnostics(
        IReadOnlyList<MovementCandidateScore> topCandidates,
        MovementCandidateScore? chosenScore,
        MovementPlannerQueryBudget? queryBudget)
    {
        var routeMemory = this.trashRouteMemory.Diagnostics;
        var routeProbe = chosenScore?.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal) == true
            ? chosenScore
            : topCandidates.FirstOrDefault(candidate => candidate.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal));
        if (routeProbe != null)
        {
            routeMemory = routeMemory with
            {
                NextWaypoint = routeProbe.FirstWaypoint,
                VnavStatus = routeProbe.PathStatus,
                WaypointCount = routeProbe.PathWaypointCount ?? routeMemory.WaypointCount,
                InvalidationReason = routeProbe.Accepted
                    ? routeMemory.InvalidationReason
                    : routeProbe.RejectionReason
            };
        }

        return routeMemory with
        {
            Reason = routeProbe?.Reason ?? routeMemory.Reason,
            LocalDestination = routeProbe?.Destination ?? routeMemory.LocalDestination,
            QueryBudgetUsed = queryBudget?.TotalUsed ?? routeMemory.QueryBudgetUsed,
            QueryBudgetLimit = queryBudget?.TotalLimit ?? routeMemory.QueryBudgetLimit
        };
    }

    private void ClearIntent(string reason)
    {
        if (this.currentIntent != null)
        {
            services.Log.Verbose($"Movement planner cleared intent {this.currentIntent.IntentId}: {reason}");
        }

        this.currentIntent = null;
        this.ResetProgressTracking();
    }

    private void UpdateCurrentIntentProgress(MovementPlannerContext context)
    {
        if (this.currentIntent == null)
        {
            this.ResetProgressTracking();
            return;
        }

        var distanceToDestination = Distance2D(context.PlayerPosition, this.currentIntent.Destination);
        if (distanceToDestination <= this.currentIntent.AcceptanceRadius + StuckDestinationDistanceBuffer)
        {
            this.ResetProgressTracking();
            return;
        }

        if (this.progressIntentId != this.currentIntent.IntentId || this.progressLastPosition == null)
        {
            this.progressIntentId = this.currentIntent.IntentId;
            this.progressLastPosition = context.PlayerPosition;
            this.progressLastMovedUtc = context.NowUtc;
            return;
        }

        if (Distance2D(context.PlayerPosition, this.progressLastPosition.Value) > StuckProgressThreshold)
        {
            this.progressLastPosition = context.PlayerPosition;
            this.progressLastMovedUtc = context.NowUtc;
            return;
        }

        if (context.NowUtc - this.progressLastMovedUtc < StuckIntentDuration)
        {
            return;
        }

        this.stuckSuppressedSource = this.currentIntent.Source;
        this.stuckSuppressedDestination = this.currentIntent.Destination;
        this.stuckSuppressedUntilUtc = context.NowUtc.Add(StuckDestinationCooldown);
        this.ClearIntent("stuck_zero_momentum");
    }

    private bool IsRecentlyStuckDestination(DateTime now, MovementCandidate candidate)
    {
        return now < this.stuckSuppressedUntilUtc &&
               this.stuckSuppressedDestination.HasValue &&
               string.Equals(candidate.Source, this.stuckSuppressedSource, StringComparison.Ordinal) &&
               Distance2D(candidate.Destination, this.stuckSuppressedDestination.Value) <= DestinationSameThreshold;
    }

    private void ResetProgressTracking()
    {
        this.progressIntentId = 0;
        this.progressLastPosition = null;
        this.progressLastMovedUtc = DateTime.MinValue;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static float? DirectionYawDelta(float currentRotation, Vector3 from, Vector3 to)
    {
        var delta = to - from;
        delta.Y = 0f;
        if (delta.LengthSquared() <= 0.0001f)
        {
            return null;
        }

        var desired = MathF.Atan2(delta.X, delta.Z);
        return NormalizeRadians(desired - currentRotation);
    }

    private static float NormalizeRadians(float value)
    {
        while (value > MathF.PI)
        {
            value -= MathF.Tau;
        }

        while (value < -MathF.PI)
        {
            value += MathF.Tau;
        }

        return value;
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static int CountCollection(object? value)
    {
        return value is ICollection collection ? collection.Count : 0;
    }

    private static bool TryCreateObstacleMapSnapshot(
        object? value,
        Vector3 mapCenter,
        [NotNullWhen(true)] out ObstacleMapSnapshot? obstacleMap,
        out string reason)
    {
        obstacleMap = null;
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (value == null)
        {
            reason = "BMR obstacle region unavailable";
            return false;
        }

        var bitmap = ReadMember(value, "Bitmap", Flags);
        if (bitmap == null)
        {
            reason = "BMR obstacle bitmap unavailable";
            return false;
        }

        var rect = ReadMember(value, "Rect", Flags);
        if (rect == null)
        {
            reason = "BMR obstacle rect unavailable";
            return false;
        }

        var width = ReadInt(ReadMember(bitmap, "Width", Flags)).GetValueOrDefault();
        var height = ReadInt(ReadMember(bitmap, "Height", Flags)).GetValueOrDefault();
        var bytesPerRow = ReadInt(ReadMember(bitmap, "BytesPerRow", Flags)).GetValueOrDefault();
        var resolution = ReadInt(ReadMember(bitmap, "Resolution", Flags)).GetValueOrDefault();
        var pixels = ReadMember(bitmap, "Pixels", Flags) as byte[];
        var left = ReadInt(ReadMember(rect, "Left", Flags)).GetValueOrDefault();
        var top = ReadInt(ReadMember(rect, "Top", Flags)).GetValueOrDefault();
        var right = ReadInt(ReadMember(rect, "Right", Flags)).GetValueOrDefault();
        var bottom = ReadInt(ReadMember(rect, "Bottom", Flags)).GetValueOrDefault();

        if (width <= 0 || height <= 0 || bytesPerRow <= 0 || resolution <= 0 || pixels == null)
        {
            reason = "BMR obstacle bitmap incomplete";
            return false;
        }

        if (pixels.Length < bytesPerRow * height)
        {
            reason = "BMR obstacle bitmap pixels incomplete";
            return false;
        }

        if (right <= left || bottom <= top)
        {
            reason = "BMR obstacle rect empty";
            return false;
        }

        obstacleMap = new(
            mapCenter,
            width,
            height,
            bytesPerRow,
            resolution,
            pixels,
            left,
            top,
            right,
            bottom);
        reason = "ok";
        return true;
    }

    private static object? ReadMember(object instance, string name, BindingFlags flags)
    {
        var type = instance.GetType();
        return type.GetField(name, flags)?.GetValue(instance) ??
               type.GetProperty(name, flags)?.GetValue(instance);
    }

    private static Vector3? ReadVector3(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is Vector3 vector)
        {
            return vector;
        }

        var type = value.GetType();
        var x = ReadFloat(type.GetField("X", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value));
        var y = ReadFloat(type.GetField("Y", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value));
        var z = ReadFloat(type.GetField("Z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value));
        return x.HasValue && y.HasValue && z.HasValue ? new Vector3(x.Value, y.Value, z.Value) : null;
    }

    private static float? ReadFloat(object? value)
    {
        return value switch
        {
            float f when float.IsFinite(f) => f,
            double d when double.IsFinite(d) => (float)d,
            int i => i,
            uint u => u,
            _ => null
        };
    }

    private static int? ReadInt(object? value)
    {
        return value switch
        {
            int i => i,
            uint u when u <= int.MaxValue => (int)u,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            _ => null
        };
    }

    private static string FormatMissing(params (bool Missing, string Name)[] members)
    {
        var missing = new List<string>();
        foreach (var member in members)
        {
            if (member.Missing)
            {
                missing.Add(member.Name);
            }
        }

        return string.Join(",", missing);
    }

    private static string BuildNoCandidateReason(int generatedCount, IReadOnlyDictionary<string, int> rejectedByReason)
    {
        if (generatedCount <= 0 || rejectedByReason.Count == 0)
        {
            return "no_candidate";
        }

        return string.Create(CultureInfo.InvariantCulture, $"all_rejected:{TopRejectedReason(rejectedByReason)}");
    }

    private static string TopRejectedReason(IReadOnlyDictionary<string, int> rejectedByReason)
    {
        return rejectedByReason
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First()
            .Key;
    }

    private sealed class MovementPlannerQueryBudget
    {
        private readonly bool enabled;
        private readonly int routeLimit;
        private readonly int recoveryLimit;
        private int routeUsed;
        private int recoveryUsed;

        private MovementPlannerQueryBudget(bool enabled, int routeLimit, int recoveryLimit)
        {
            this.enabled = enabled;
            this.routeLimit = routeLimit;
            this.recoveryLimit = recoveryLimit;
        }

        public int TotalUsed => this.enabled ? this.routeUsed + this.recoveryUsed : 0;
        public int TotalLimit => this.enabled ? this.routeLimit + this.recoveryLimit : 0;

        public static MovementPlannerQueryBudget Unlimited()
        {
            return new(false, 0, 0);
        }

        public static MovementPlannerQueryBudget ForRouteMemory(bool routeMemoryActive)
        {
            return routeMemoryActive
                ? new(true, 3, 2)
                : Unlimited();
        }

        public bool TryConsume(MovementCandidate candidate, out string reason)
        {
            if (!this.enabled)
            {
                reason = string.Empty;
                return true;
            }

            if (candidate.Source.Equals(TrashRouteMemory.CandidateSource, StringComparison.Ordinal))
            {
                if (this.routeUsed < this.routeLimit)
                {
                    ++this.routeUsed;
                    reason = string.Empty;
                    return true;
                }

                reason = "RouteBudgetExceeded";
                return false;
            }

            if (IsPathRecoverySource(candidate.Source))
            {
                if (this.recoveryUsed < this.recoveryLimit)
                {
                    ++this.recoveryUsed;
                    reason = string.Empty;
                    return true;
                }

                reason = "RouteRecoveryBudgetExceeded";
                return false;
            }

            if (candidate.Source.Equals("AoE pack", StringComparison.Ordinal) ||
                candidate.Source.Equals("Pack engagement", StringComparison.Ordinal) ||
                candidate.Source.Equals("Tank pull lead", StringComparison.Ordinal) ||
                candidate.Source.Equals(BmrSafetyEscapeSource, StringComparison.Ordinal))
            {
                reason = string.Empty;
                return true;
            }

            reason = "RouteBudgetSuppressed";
            return false;
        }
    }

    private sealed record ObstacleMapSnapshot(
        Vector3 MapCenter,
        int Width,
        int Height,
        int BytesPerRow,
        int Resolution,
        byte[] Pixels,
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public bool HasLineOfSight(float fromX, float fromZ, float toX, float toZ)
        {
            if (!this.TryWorldToBitmapCell(fromX, fromZ, out var x0, out var y0) ||
                !this.TryWorldToBitmapCell(toX, toZ, out var x1, out var y1))
            {
                return true;
            }

            var dx = Math.Abs(x1 - x0);
            var sx = x0 < x1 ? 1 : -1;
            var dy = -Math.Abs(y1 - y0);
            var sy = y0 < y1 ? 1 : -1;
            var err = dx + dy;
            var x = x0;
            var y = y0;

            while (true)
            {
                if (this.IsBlocked(x, y))
                {
                    return false;
                }

                if (x == x1 && y == y1)
                {
                    return true;
                }

                var e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }

        private bool TryWorldToBitmapCell(float worldX, float worldZ, out int x, out int y)
        {
            var centerCellX = (this.Left + this.Right) * 0.5f;
            var centerCellY = (this.Top + this.Bottom) * 0.5f;
            var invResolution = this.Resolution / 1024f;
            x = (int)MathF.Round(centerCellX + ((worldX - this.MapCenter.X) * invResolution));
            y = (int)MathF.Round(centerCellY + ((worldZ - this.MapCenter.Z) * invResolution));
            return (uint)x < (uint)this.Width && (uint)y < (uint)this.Height;
        }

        private bool IsBlocked(int x, int y)
        {
            if ((uint)x >= (uint)this.Width || (uint)y >= (uint)this.Height)
            {
                return false;
            }

            var index = (y * this.BytesPerRow) + (x >> 3);
            if ((uint)index >= (uint)this.Pixels.Length)
            {
                return false;
            }

            var mask = 0x80 >> (x & 7);
            return (this.Pixels[index] & mask) != 0;
        }
    }

    private sealed class LineOfSightReengageGoal(ObstacleMapSnapshot obstacleMap, Vector3 target, float targetRange)
    {
        private static readonly MethodInfo ScoreMethod = typeof(LineOfSightReengageGoal).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly float targetRangeSq = targetRange * targetRange;

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreMethod,
                Expression.Convert(Expression.Field(parameter, xField), typeof(float)),
                Expression.Convert(Expression.Field(parameter, zField), typeof(float)));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        private float ScoreFromWPos(float x, float z)
        {
            if (!obstacleMap.HasLineOfSight(x, z, target.X, target.Z))
            {
                return LineOfSightReengageBlockedScore;
            }

            var dx = x - target.X;
            var dz = z - target.Z;
            var distanceSq = (dx * dx) + (dz * dz);
            return distanceSq <= this.targetRangeSq
                ? LineOfSightReengageVisibleInRangeScore
                : LineOfSightReengageVisibleOutOfRangeScore;
        }
    }

    private sealed record ResolvedHintMembers(
        Type WPosType,
        FieldInfo WPosXField,
        FieldInfo WPosZField,
        FieldInfo ForcedMovementField,
        FieldInfo GoalZonesField,
        FieldInfo ForbiddenZonesField,
        FieldInfo PathfindMapCenterField,
        FieldInfo PathfindMapBoundsField,
        FieldInfo PathfindMapObstaclesField,
        ConstructorInfo WDirConstructor,
        MethodInfo BoundsContainsMethod);

    private sealed class FlatTopGoal(Vector3 destination, float acceptanceRadius)
    {
        private static readonly MethodInfo ScoreMethod = typeof(FlatTopGoal).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;

        public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
        {
            var parameter = Expression.Parameter(wposType, "p");
            var call = Expression.Call(
                Expression.Constant(this),
                ScoreMethod,
                Expression.Convert(Expression.Field(parameter, xField), typeof(float)),
                Expression.Convert(Expression.Field(parameter, zField), typeof(float)));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, call, parameter).Compile();
        }

        private float ScoreFromWPos(float x, float z)
        {
            var dx = x - destination.X;
            var dz = z - destination.Z;
            var distSq = (dx * dx) + (dz * dz);
            var radiusSq = MathF.Max(0.25f, acceptanceRadius * acceptanceRadius);
            return distSq <= radiusSq ? GoalZoneScorePolicy.StrongPreference : 0f;
        }
    }
}
