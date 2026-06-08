using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal static class PositionalTrueNorthPolicy
{
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float ArrivalBufferSeconds = 0.2f;
    private const float FallbackActionAheadSeconds = 0.35f;

    public static bool ShouldWalkInsteadOfTrueNorth(Positional requiredPositional, RsrGcdActionTimingSnapshot action, float moveDistance, out string reason)
    {
        reason = string.Empty;
        if (!PositionalDashPolicy.IsActive(requiredPositional))
        {
            return false;
        }

        if (!TryGetActionPositional(action, out var actionPositional))
        {
            reason = $"RSR next GCD {action.ActionName} is not a known positional";
            return false;
        }

        if (actionPositional != requiredPositional)
        {
            reason = $"Avarice {requiredPositional} does not match RSR next GCD {action.ActionName} {actionPositional}";
            return false;
        }

        if (!AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = "RSR GCD timing unavailable";
            return false;
        }

        var requiredSeconds = EstimateMovementSeconds(moveDistance);
        var budgetSeconds = CalculateMovementBudgetSeconds(action.GcdRemaining, action.GcdActionAhead);
        if (requiredSeconds > budgetSeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"positional move too late for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking to {requiredPositional} for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
        return true;
    }

    public static bool TryEstimateWalkDistance(
        Vector3 playerPosition,
        float playerHitboxRadius,
        Vector3 targetPosition,
        float targetHitboxRadius,
        float targetRotation,
        Positional requiredPositional,
        out float distance)
    {
        distance = 0f;
        if (!PositionalDashPolicy.IsActive(requiredPositional))
        {
            return false;
        }

        var ringRadius = targetHitboxRadius + playerHitboxRadius + CombatConstants.MeleeActionRange;
        var candidates = PositionalDashPolicy.EnumeratePreferredLandings(
            playerPosition,
            targetPosition,
            targetRotation,
            ringRadius,
            requiredPositional);
        var nearest = candidates
            .Select(candidate => Geometry.Distance2D(playerPosition, candidate))
            .DefaultIfEmpty(float.PositiveInfinity)
            .Min();
        if (!float.IsFinite(nearest))
        {
            return false;
        }

        distance = nearest;
        return true;
    }

    internal static bool TryGetActionPositional(RsrGcdActionTimingSnapshot action, out Positional positional)
    {
        return TryGetActionPositional(action.AdjustedActionId, out positional) ||
               TryGetActionPositional(action.ActionId, out positional);
    }

    internal static float CalculateMovementBudgetSeconds(float gcdRemaining, float gcdActionAhead)
    {
        var actionAhead = float.IsFinite(gcdActionAhead) && gcdActionAhead >= 0f
            ? gcdActionAhead
            : FallbackActionAheadSeconds;
        return MathF.Max(0f, gcdRemaining - actionAhead);
    }

    internal static float EstimateMovementSeconds(float moveDistance)
        => moveDistance / EstimatedCombatMoveSpeed + ArrivalBufferSeconds;

    private static bool TryGetActionPositional(uint actionId, out Positional positional)
    {
        positional = actionId switch
        {
            56 or 3554 or 3563 or 7482 or 24382 or 36947 or 36970 or 34610 or 34611 or 34621 => Positional.Flank,
            66 or 88 or 2255 or 2258 or 3556 or 7481 or 24383 or 25772 or 36971 or 34612 or 34613 or 34622 => Positional.Rear,
            _ => Positional.Any
        };
        return positional != Positional.Any;
    }
}
