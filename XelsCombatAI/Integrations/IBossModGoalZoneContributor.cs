using System;
using System.Collections.Generic;

namespace XelsCombatAI.Integrations;

internal enum BossModGoalPriority
{
    Convenience = 100,
    Uptime = 200,
    ImmediateAction = 300,
    DefensiveMechanic = 400
}

internal sealed record BossModGoalContribution(Delegate Goal, BossModGoalPriority Priority, string Label);

internal interface IBossModGoalZoneContributor
{
    void SetHookState(string state);

    void SetBossModMovementState(bool moveRequested, bool moveImminent)
    {
    }

    void TryInjectGoal(object hints, ICollection<BossModGoalContribution> contributions);

    void Reset();
}
