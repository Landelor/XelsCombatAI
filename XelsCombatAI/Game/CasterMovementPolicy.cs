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
    private const float MaxSlidecastMoveDistance = EstimatedCombatMoveSpeed * ConservativeSlidecastWindowSeconds;

    public static bool ShouldSuppressAdvisoryMovement(IBattleChara? player)
    {
        if (!HasActiveCastTime(player))
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
        bool activeCastTime,
        bool slidecastWindow,
        float? moveDistance)
    {
        if (!activeCastTime)
        {
            return false;
        }

        if (!slidecastWindow)
        {
            return true;
        }

        return !moveDistance.HasValue ||
               !float.IsFinite(moveDistance.Value) ||
               moveDistance.Value < 0f ||
               moveDistance.Value > MaxSlidecastMoveDistance;
    }

    public static bool ShouldHoldAutomatedMovementForActiveCast(
        bool activeCastTime,
        bool slidecastWindow,
        bool safetyKnown,
        bool currentPositionSafe,
        out string reason)
    {
        reason = string.Empty;
        if (!activeCastTime || slidecastWindow)
        {
            return false;
        }

        if (!safetyKnown || !currentPositionSafe)
        {
            return false;
        }

        reason = "movement held during active cast while current position is safe";
        return true;
    }

    public static bool IsCasterSlidecastWindow(IBattleChara? player)
    {
        if (player == null)
        {
            return false;
        }

        return IsCasterSlidecastWindow(player.IsCasting, player.TotalCastTime, player.CurrentCastTime);
    }

    public static bool HasActiveCastTime(IBattleChara? player)
    {
        return player != null && HasActiveCastTime(player.IsCasting, player.TotalCastTime);
    }

    internal static bool HasActiveCastTime(bool playerCasting, float totalCastTime)
    {
        return playerCasting &&
               float.IsFinite(totalCastTime) &&
               totalCastTime > 0f;
    }

    internal static bool IsCasterSlidecastWindow(bool playerCasting, float totalCastTime, float currentCastTime)
    {
        if (!HasActiveCastTime(playerCasting, totalCastTime))
        {
            return false;
        }

        if (!float.IsFinite(currentCastTime) ||
            totalCastTime < MinimumCastTimeForSlidecastSeconds)
        {
            return false;
        }

        var remaining = totalCastTime - currentCastTime;
        return remaining >= 0f && remaining <= SlidecastWindowSeconds;
    }

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
