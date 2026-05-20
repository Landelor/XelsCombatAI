using System;
using System.Collections.Generic;
using System.Numerics;
using XelsCombatAI.Combat;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Runtime;

internal sealed record CombatHistoryFrame(
    DateTime TimestampUtc,
    float T,
    bool InCombat,
    bool IsDead,
    bool PluginEnabled,
    uint PlayerClassJobId,
    uint TerritoryType,
    uint ContentFinderConditionId,
    Vector3? PlayerPosition,
    float PlayerRotation,
    uint TargetBaseId,
    ulong TargetObjectId,
    Vector3? TargetPosition,
    float TargetRotation,
    float TargetRadius,
    // Movement
    bool? Movement,
    bool AutomatedMovementSuppressed,
    string? MovementRangeStrategy,
    string? SafetyBuffer,
    float TargetUptimeRange,
    // Positionals
    Positional LastPositional,
    bool TrueNorthActive,
    uint TrueNorthCharges,
    // Gap closers
    string GapSafety,
    string EscapeSafety,
    Vector3? EscapeLanding,
    MobilityDecisionDiagnostics MobilityDecision,
    // Healer coverage zone
    string HealerCoverageReason,
    bool HealerCoverageInjected,
    int HealerCoverageMembers,
    int HealerCoverageCoveredMembers,
    float HealerCoverageDist,
    // AoE pack
    string Reason,
    bool Henched,
    string RsrStatus,
    string RsrReflectionDiagnostics,
    string RotationSolverIpcDiagnostics,
    string RotationSolverRedMageMeleeDiagnostics,
    StateCommandType RsrSnapshotMode,
    string RsrLastRestore,
    int Targets,
    int CurrentHits,
    int BestHits,
    bool Injected,
    string ActionName,
    string Shape,
    Vector3? AoeCandidate,
    Vector3? AoePrimaryTarget,
    bool AoeCandidateInjected,
    // Survivability zone
    string SurvZoneReason,
    bool SurvZoneInjected,
    string SurvZoneName,
    float SurvZoneDistance,
    Vector3? SurvZoneCenter,
    Vector3? SurvZoneCaster,
    // Passage of arms
    string PassageReason,
    bool PassageInjected,
    float PassageDistance,
    bool PassageInCone,
    Vector3? PassagePaladin,
    Vector3? PassagePreferred,
    // Healer coverage
    Vector3? HealerCoverageCenter,
    string ArenaEdgeReason,
    // BossMod goal hook
    string GoalPriority,
    string GoalSources,
    string BossModActiveModule,
    string BossModActiveZoneModule,
    string BossModNavigationDestination,
    string BossModNavigationNextWaypoint,
    string BossModNavigationStats,
    string BossModControllerTarget,
    string BossModMovementOverride,
    string BossModHintSummary,
    BossModMovementDiagnostics BossModMovement,
    string ManualMovementInput,
    FacingStatus Facing,
    RedMageMeleeComboStatus RedMageMeleeCombo,
    TrashPullDiagnostics TrashPull,
    IReadOnlyList<CombatHistoryActorSnapshot> Actors);

internal sealed record CombatHistoryActorSnapshot(
    string Relation,
    ulong GameObjectId,
    uint EntityId,
    uint BaseId,
    string ObjectKind,
    byte SubKind,
    uint ClassJobId,
    byte Level,
    Vector3 Position,
    float Rotation,
    float Radius,
    bool IsTargetable,
    bool IsDead,
    bool InCombat,
    uint CurrentHp,
    uint MaxHp,
    ulong TargetObjectId,
    float DistanceToPlayer);
