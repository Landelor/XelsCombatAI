using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed record CombatHistoryFrame(
    float T,
    bool InCombat,
    bool IsDead,
    uint PlayerClassJobId,
    uint TargetBaseId,
    // Movement
    bool? Movement,
    bool AutomatedMovementSuppressed,
    string? MovementRangeStrategy,
    string? ForbiddenZoneCushion,
    float Range,
    // Positionals
    Positional LastPositional,
    bool TrueNorthActive,
    uint TrueNorthCharges,
    // Gap closers
    string GapSafety,
    string EscapeSafety,
    // AoE pack
    string Reason,
    bool Henched,
    int Targets,
    int CurrentHits,
    int BestHits,
    bool Injected,
    string ActionName,
    string Shape);
