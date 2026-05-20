using System.Collections.Generic;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal sealed record AoePackPositioningStatus(
    string HookState,
    string LastReason,
    string RsrStatus,
    string RsrReflectionDiagnostics,
    uint ActionId,
    string ActionName,
    string Shape,
    int CurrentHits,
    int BestHits,
    bool Injected,
    bool RsrHenchedActive,
    StateCommandType RsrSnapshotMode,
    string RsrLastRestoreStatus,
    int PriorityTargetCount,
    Vector3? Candidate,
    Vector3? PrimaryTarget,
    bool CandidateInjected,
    TrashPullDiagnostics TrashPull);

internal sealed record AoePackOverlaySnapshot(
    uint ActionId,
    string ActionName,
    string Shape,
    Vector3 Candidate,
    Vector3 PrimaryTarget,
    float Radius,
    float HalfWidth,
    int CurrentHits,
    int BestHits,
    IReadOnlyList<AoePackOverlayTarget> Targets);

internal sealed record AoePackOverlayTarget(Vector3 Position, float Radius, bool Hit, bool InsideAvoidedHitbox = false, string? AvoidanceLabel = null);
