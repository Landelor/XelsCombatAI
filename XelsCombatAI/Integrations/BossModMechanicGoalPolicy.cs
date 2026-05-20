using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Integrations;

internal static class BossModMechanicGoalPolicy
{
    private static readonly TimeSpan MechanicWhisperCloserStability = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MechanicWhisperConfidentRedirectStability = TimeSpan.FromMilliseconds(450);
    private const float MechanicWhisperAlignedDistance = 4f;
    private const float MechanicWhisperCloserDistanceGain = 6f;
    private const float MechanicEscapeMarginMinimumMoveDistance = 0.75f;
    private const float MechanicEscapeMarginMaximumMoveDistance = 8f;

    public static bool ShouldAllowMechanicWhisperCandidate(
        Vector2 candidate,
        Vector2? bossModDestination,
        Vector2? playerPosition,
        TimeSpan stableFor,
        MechanicWhisperConfidence confidence)
    {
        return EvaluateMechanicWhisperCandidate(candidate, bossModDestination, playerPosition, stableFor, confidence).Allowed;
    }

    public static bool ShouldIsolateMechanicSafetyGoals(
        int forbiddenZones,
        int temporaryObstacles,
        int forbiddenDirections,
        string? imminentSpecialMode,
        bool forcedMovementActive)
    {
        return forcedMovementActive ||
               forbiddenZones > 0 ||
               temporaryObstacles > 0 ||
               forbiddenDirections > 0 ||
               HasActiveSpecialMode(imminentSpecialMode);
    }

    public static BossModGoalContribution[] SelectMechanicSafetyGoalContributions(IReadOnlyList<BossModGoalContribution> contributions)
    {
        if (contributions.Count == 0)
        {
            return [];
        }

        var highestPriority = contributions.Max(c => c.Priority);
        return contributions
            .Where(c => c.Priority == highestPriority)
            .ToArray();
    }

    public static bool TryResolveMechanicEscapeMarginCandidate(
        Vector2 playerPosition,
        Vector3? desiredMovement,
        bool forbiddenZonesActive,
        bool forcedMovementActive,
        bool moveRequested,
        bool moveImminent,
        out Vector2 candidate)
    {
        candidate = default;
        if (!forbiddenZonesActive ||
            forcedMovementActive ||
            (!moveRequested && !moveImminent) ||
            !desiredMovement.HasValue)
        {
            return false;
        }

        var move = new Vector2(desiredMovement.Value.X, desiredMovement.Value.Z);
        var distance = move.Length();
        if (distance < MechanicEscapeMarginMinimumMoveDistance)
        {
            return false;
        }

        if (distance > MechanicEscapeMarginMaximumMoveDistance)
        {
            move *= MechanicEscapeMarginMaximumMoveDistance / distance;
        }

        candidate = playerPosition + move;
        return true;
    }

    private static bool HasActiveSpecialMode(string? mode)
    {
        return !string.IsNullOrWhiteSpace(mode) &&
               !string.Equals(mode, "<none>", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase);
    }

    public static MechanicWhisperDecision EvaluateMechanicWhisperCandidate(
        Vector2 candidate,
        Vector2? bossModDestination,
        Vector2? playerPosition,
        TimeSpan stableFor,
        MechanicWhisperConfidence confidence)
    {
        if (bossModDestination.HasValue &&
            Vector2.Distance(candidate, bossModDestination.Value) <= MechanicWhisperAlignedDistance)
        {
            return new MechanicWhisperDecision(true, "aligned", stableFor);
        }

        if (bossModDestination.HasValue && playerPosition.HasValue)
        {
            var bossModDistance = Vector2.Distance(playerPosition.Value, bossModDestination.Value);
            var candidateDistance = Vector2.Distance(playerPosition.Value, candidate);
            if (candidateDistance + MechanicWhisperCloserDistanceGain <= bossModDistance)
            {
                return stableFor >= MechanicWhisperCloserStability
                    ? new MechanicWhisperDecision(true, "shorter", stableFor)
                    : new MechanicWhisperDecision(false, "shorter-stabilizing", stableFor);
            }
        }

        if (confidence == MechanicWhisperConfidence.Confident)
        {
            return stableFor >= MechanicWhisperConfidentRedirectStability
                ? new MechanicWhisperDecision(true, "confident-redirect", stableFor)
                : new MechanicWhisperDecision(false, "confident-redirect-stabilizing", stableFor);
        }

        return new MechanicWhisperDecision(false, "routine-not-redirecting", stableFor);
    }
}

internal readonly record struct MechanicWhisperDecision(bool Allowed, string Reason, TimeSpan StableFor);
