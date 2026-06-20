using System;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Game;

internal static class CasterMovementPolicy
{
    private const float SlidecastWindowSeconds = 0.5f;
    private const float ConservativeSlidecastWindowSeconds = 0.35f;
    private const float MinimumCastTimeForSlidecastSeconds = 1f;
    private const float CasterGcdReadyHoldSeconds = 0.75f;
    private const float MaximumActionAheadHoldSeconds = 1f;
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float MovementArrivalBufferSeconds = 0.2f;
    private const float FallbackActionAheadSeconds = 0.35f;
    private const float MaxMagicRangedSlidecastMoveDistance = EstimatedCombatMoveSpeed * ConservativeSlidecastWindowSeconds;

    public static bool ShouldSuppressAdvisoryMovement(IBattleChara? player)
    {
        if (player == null || !player.IsCasting)
        {
            return false;
        }

        return !IsCasterSlidecastWindow(player);
    }

    public static bool ShouldSuppressAdvisoryMovementForGcd(uint classJobId, float gcdRemaining, float gcdElapsed, float gcdTotal, float gcdActionAhead)
    {
        if (!IsCasterLike(classJobId))
        {
            return false;
        }

        if (!float.IsFinite(gcdRemaining) ||
            !float.IsFinite(gcdElapsed) ||
            !float.IsFinite(gcdTotal) ||
            gcdRemaining < 0f ||
            gcdElapsed < 0f ||
            gcdTotal < MinimumCastTimeForSlidecastSeconds)
        {
            return false;
        }

        var actionAheadHold = float.IsFinite(gcdActionAhead) && gcdActionAhead > 0f
            ? Math.Clamp(gcdActionAhead + 0.2f, CasterGcdReadyHoldSeconds, MaximumActionAheadHoldSeconds)
            : CasterGcdReadyHoldSeconds;
        return gcdRemaining <= actionAheadHold;
    }

    public static bool ShouldSuppressAdvisoryMovementForGcd(
        uint classJobId,
        float gcdRemaining,
        float gcdElapsed,
        float gcdTotal,
        float gcdActionAhead,
        float? moveDistance)
    {
        if (!IsCasterLike(classJobId) ||
            !HasReliableGcdTiming(gcdRemaining, gcdElapsed, gcdTotal))
        {
            return false;
        }

        if (!moveDistance.HasValue || !float.IsFinite(moveDistance.Value) || moveDistance.Value < 0f)
        {
            return ShouldSuppressAdvisoryMovementForGcd(classJobId, gcdRemaining, gcdElapsed, gcdTotal, gcdActionAhead);
        }

        var requiredSeconds = EstimateMovementSeconds(moveDistance.Value);
        var budgetSeconds = CalculateMovementBudgetSeconds(gcdRemaining, gcdActionAhead);
        return requiredSeconds > budgetSeconds;
    }

    public static bool ShouldSuppressAdvisoryMovementForActiveCast(
        uint classJobId,
        bool playerCasting,
        bool slidecastWindow,
        float? moveDistance)
    {
        if (!playerCasting)
        {
            return false;
        }

        if (JobRoles.GetRangeRole(classJobId) is not RangeRole.MagicRanged)
        {
            return true;
        }

        if (!slidecastWindow)
        {
            return true;
        }

        return !moveDistance.HasValue ||
               !float.IsFinite(moveDistance.Value) ||
               moveDistance.Value < 0f ||
               moveDistance.Value > MaxMagicRangedSlidecastMoveDistance;
    }

    public static bool IsCasterSlidecastWindow(IBattleChara player)
    {
        if (!IsCasterLike(player))
        {
            return false;
        }

        var totalCastTime = player.TotalCastTime;
        var currentCastTime = player.CurrentCastTime;
        if (!float.IsFinite(totalCastTime) ||
            !float.IsFinite(currentCastTime) ||
            totalCastTime < MinimumCastTimeForSlidecastSeconds)
        {
            return false;
        }

        var remaining = totalCastTime - currentCastTime;
        return remaining >= 0f && remaining <= SlidecastWindowSeconds;
    }

    private static bool IsCasterLike(IBattleChara? player)
        => JobRoles.GetRangeRole(player) is RangeRole.MagicRanged or RangeRole.Healer;

    private static bool IsCasterLike(uint classJobId)
        => JobRoles.GetRangeRole(classJobId) is RangeRole.MagicRanged or RangeRole.Healer;

    private static bool HasReliableGcdTiming(float gcdRemaining, float gcdElapsed, float gcdTotal)
    {
        return float.IsFinite(gcdRemaining) &&
               float.IsFinite(gcdElapsed) &&
               float.IsFinite(gcdTotal) &&
               gcdRemaining >= 0f &&
               gcdElapsed >= 0f &&
               gcdTotal >= MinimumCastTimeForSlidecastSeconds;
    }

    private static float CalculateMovementBudgetSeconds(float gcdRemaining, float gcdActionAhead)
    {
        var actionAhead = float.IsFinite(gcdActionAhead) && gcdActionAhead >= 0f
            ? gcdActionAhead
            : FallbackActionAheadSeconds;
        return MathF.Max(0f, gcdRemaining - actionAhead);
    }

    private static float EstimateMovementSeconds(float moveDistance)
        => moveDistance / EstimatedCombatMoveSpeed + MovementArrivalBufferSeconds;
}
