using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Combat;

internal enum MovementCandidatePriority
{
    Comfort = 100,
    TargetRange = 200,
    PathRecovery = 250,
    ActiveAoe = 300,
    Defensive = 400
}

internal sealed record MovementCandidate(
    string Source,
    string Reason,
    Vector3 Destination,
    float AcceptanceRadius,
    MovementCandidatePriority Priority,
    float SourceScore,
    float TargetRangeScore = 0f,
    float AoeScore = 0f,
    float HumanScore = 0.5f);

internal sealed record MovementCandidateScore(
    string Source,
    string Reason,
    Vector3 Destination,
    float AcceptanceRadius,
    MovementCandidatePriority Priority,
    bool Accepted,
    string RejectionReason,
    float TotalScore,
    float BmrSafetyScore,
    float VnavScore,
    float TargetRangeScore,
    float AoeScore,
    float MovementCostScore,
    float TurnSmoothnessScore,
    float PreviousDestinationScore,
    float HumanScore,
    string PathStatus,
    float? PathDistance,
    float DirectDistance,
    float? ExtraPathDistance,
    float? PathDetourRatio,
    int? PathWaypointCount,
    double? PathCacheAgeMilliseconds,
    Vector3? FirstWaypoint,
    float? FirstWaypointDistance,
    float? FirstWaypointYawDelta,
    string ScoreBreakdown);

internal sealed record MovementIntent(
    long IntentId,
    string Source,
    Vector3 Destination,
    float AcceptanceRadius,
    MovementCandidateScore Score,
    DateTime CommittedAtUtc,
    DateTime HoldUntilUtc);

internal sealed record MovementLineOfSightDiagnostics(
    bool Checked,
    bool Blocked,
    bool CombatClear,
    bool NavigationClear,
    string Reason,
    Vector3? BlockedPoint,
    float? BlockedDistance)
{
    public static MovementLineOfSightDiagnostics NotChecked(string reason) => new(false, false, true, true, reason, null, null);
}

internal sealed record TrashRouteMemoryDiagnostics(
    bool Active,
    string State,
    string Source,
    string Reason,
    Vector3? RouteGoal,
    Vector3? LocalDestination,
    Vector3? NextWaypoint,
    int OffsetSide,
    float OffsetDistance,
    double RouteAgeMilliseconds,
    int WaypointIndex,
    int WaypointCount,
    string VnavStatus,
    int QueryBudgetUsed,
    int QueryBudgetLimit,
    string InvalidationReason,
    IReadOnlyList<Vector3> TankTrail)
{
    public static TrashRouteMemoryDiagnostics Empty { get; } = new(
        false,
        "inactive",
        "<none>",
        "not evaluated",
        null,
        null,
        null,
        0,
        0f,
        0d,
        0,
        0,
        "None",
        0,
        0,
        "<none>",
        []);
}

internal sealed record MovementPlannerDiagnostics(
    string IntentId,
    string ChosenSource,
    Vector3? Destination,
    float? AcceptanceRadius,
    double HoldRemainingMs,
    string SwitchReason,
    string SuppressionReason,
    int GeneratedCount,
    int AcceptedCount,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyList<MovementCandidateScore> TopCandidates,
    string ScoreBreakdown,
    string PathStatus,
    float? PathDistance,
    float? DirectDistance,
    float? ExtraPathDistance,
    float? PathDetourRatio,
    int? PathWaypointCount,
    double? PathCacheAgeMilliseconds,
    Vector3? FirstWaypoint,
    float? FirstWaypointDistance,
    float? FirstWaypointYawDelta,
    VNavmeshRuntimeDiagnostics Vnavmesh,
    string VnavmeshProbeSource,
    VNavmeshPointDiagnostics? VnavmeshDestination,
    MovementLineOfSightDiagnostics LineOfSight,
    Vector3? BmrForcedMovement,
    int BmrForbiddenZones,
    bool BmrMoveRequested,
    bool BmrMoveImminent,
    TrashRouteMemoryDiagnostics RouteMemory)
{
    public static MovementPlannerDiagnostics Empty { get; } = new(
        "<none>",
        "<none>",
        null,
        null,
        0d,
        "none",
        "not evaluated",
        0,
        0,
        new Dictionary<string, int>(),
        [],
        "<none>",
        "None",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        new VNavmeshRuntimeDiagnostics(false, null, null, null),
        "<none>",
        null,
        MovementLineOfSightDiagnostics.NotChecked("not evaluated"),
        null,
        0,
        false,
        false,
        TrashRouteMemoryDiagnostics.Empty);
}

internal sealed class MovementPlannerContext
{
    public required object Hints { get; init; }
    public required DateTime NowUtc { get; init; }
    public required IBattleChara Player { get; init; }
    public required Vector3 PlayerPosition { get; init; }
    public required float PlayerRotation { get; init; }
    public required float PlayerHitboxRadius { get; init; }
    public required IBattleChara? Target { get; init; }
    public required float EngagementRange { get; init; }
    public required float PackAoeRange { get; init; }
    public required bool AutomatedMovementSuppressed { get; init; }
    public required bool BmrMoveRequested { get; init; }
    public required bool BmrMoveImminent { get; init; }
    public required bool BossModEncounterActive { get; init; }
    public required int BmrGoalZones { get; init; }
    public required int BmrForbiddenZones { get; init; }
    public required Vector3? BmrForcedMovement { get; init; }
    public required Vector3? PathfindMapCenter { get; init; }
    public required MovementLineOfSightDiagnostics LineOfSight { get; init; }
    public required Func<Vector3, bool> IsInsidePathfindMap { get; init; }
}
