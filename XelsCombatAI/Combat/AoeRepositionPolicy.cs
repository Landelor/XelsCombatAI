using System;
using System.Globalization;

namespace XelsCombatAI.Combat;

internal static class AoeRepositionPolicy
{
    private const float EstimatedCombatMoveSpeed = 6f;
    private const float AoeRepositionArrivalBufferSeconds = 0.15f;
    private const float MarginalAoeGainMaxMoveDistance = 4.5f;
    private const float ModerateAoeGainMaxMoveDistance = 7f;
    private const float StrongAoeGainMaxMoveDistance = 10f;

    public static bool ShouldSkipMarginalAoeReposition(int currentHits, int bestHits, int targetCount, float moveDistance, out string reason)
    {
        reason = string.Empty;
        var gain = bestHits - currentHits;
        if (gain <= 0 || targetCount <= 1)
        {
            return false;
        }

        var goodEnoughHits = GoodEnoughTrashAoeHits(targetCount);
        if (currentHits < goodEnoughHits)
        {
            return false;
        }

        var maxMoveDistance = gain switch
        {
            <= 1 => MarginalAoeGainMaxMoveDistance,
            2 => ModerateAoeGainMaxMoveDistance,
            _ => StrongAoeGainMaxMoveDistance
        };
        if (moveDistance <= maxMoveDistance)
        {
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"already good AoE coverage ({currentHits}/{targetCount}); skipped {moveDistance:0.0}y move for +{gain} hit");
        return true;
    }

    public static bool ShouldSkipAoeRepositionForGcdTiming(float moveDistance, float gcdRemaining, float gcdTotal, out string reason)
    {
        reason = string.Empty;
        if (!HasReliableGcdTiming(gcdRemaining, 0f, gcdTotal))
        {
            return false;
        }

        var requiredSeconds = moveDistance / EstimatedCombatMoveSpeed + AoeRepositionArrivalBufferSeconds;
        if (gcdRemaining >= requiredSeconds)
        {
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"AoE move too late for next GCD ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {gcdRemaining:0.0}s left)");
        return true;
    }

    public static bool ShouldSkipProactiveAoeReposition(int currentHits, int bestHits, int targetCount, out string reason)
    {
        reason = string.Empty;
        var gain = bestHits - currentHits;
        if (gain <= 0 || targetCount <= 1)
        {
            return false;
        }

        var goodEnoughHits = GoodEnoughTrashAoeHits(targetCount);
        if (currentHits < goodEnoughHits || gain > 1)
        {
            return false;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"already good AoE prep coverage ({currentHits}/{targetCount}); skipped +{gain} hit");
        return true;
    }

    public static bool HasReliableGcdTiming(float gcdRemaining, float gcdElapsed, float gcdTotal)
    {
        return float.IsFinite(gcdRemaining) &&
               float.IsFinite(gcdElapsed) &&
               float.IsFinite(gcdTotal) &&
               gcdRemaining >= 0f &&
               gcdElapsed >= 0f &&
               gcdTotal > 0.5f;
    }

    private static int GoodEnoughTrashAoeHits(int targetCount)
    {
        if (targetCount <= 1)
        {
            return targetCount;
        }

        if (targetCount <= 3)
        {
            return targetCount;
        }

        if (targetCount <= 5)
        {
            return targetCount - 1;
        }

        return Math.Min(targetCount, Math.Max(4, (int)MathF.Ceiling(targetCount * 0.75f)));
    }
}
