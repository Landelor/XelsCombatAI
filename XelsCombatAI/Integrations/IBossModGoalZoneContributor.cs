using System;
using System.Collections.Generic;
using System.Numerics;

namespace XelsCombatAI.Integrations;

internal enum BossModGoalPriority
{
    Convenience = 100,
    Uptime = 200,
    ImmediateAction = 300,
    DefensiveMechanic = 400
}

internal enum BossModGoalScoreMode
{
    Advisory,
    Raw
}

internal enum MechanicWhisperConfidence
{
    Routine,
    Confident
}

internal sealed record BossModGoalContribution(
    Delegate Goal,
    BossModGoalPriority Priority,
    string Label,
    Vector2? Candidate = null,
    MechanicWhisperConfidence Confidence = MechanicWhisperConfidence.Routine,
    BossModGoalScoreMode ScoreMode = BossModGoalScoreMode.Advisory);

internal interface IBossModGoalZoneContributor
{
    void SetHookState(string state);

    void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
    }

    void SetBossModEncounterState(bool activeModule)
    {
    }

    void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions);

    void Reset();
}
