using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal static class GapCloserDecisionPolicy
{
    private const float ConservativeTrashGapCloserRangeReserve = 2.5f;
    private const float ConservativeTrashMinimumConfidence = 0.55f;
    private const int ConservativeTrashMinimumTargets = 3;
    private const float TrashGapCloserAwayFromAnchorTolerance = 2f;
    private const float FriendlyAnchorMinimumMoveDistance = 6f;
    private const float FriendlyAnchorMinimumUptimeGain = 4f;
    private const float HostileRelayMinimumTargetGain = 4f;
    private const float HostileRelayMinimumDirectionDot = 0.45f;

    public static bool ShouldUseFriendlyAnchorDash(
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        reason = string.Empty;
        if (moveDistance < FriendlyAnchorMinimumMoveDistance)
        {
            reason = $"ally anchor too close: {moveDistance:0.0}y";
            return false;
        }

        if (safetyGain > 0.1f ||
            pathGain > 0.1f)
        {
            return true;
        }

        if (uptimeGain < FriendlyAnchorMinimumUptimeGain)
        {
            reason = $"ally anchor low uptime gain: {uptimeGain:0.0}y";
            return false;
        }

        return true;
    }

    public static bool ShouldAllowTrashPullGapCloserTarget(Vector3 playerPosition, Vector3 targetPosition, Vector3 anchorPosition, out string reason)
    {
        var playerAnchorDistance = Geometry.Distance2D(playerPosition, anchorPosition);
        var targetAnchorDistance = Geometry.Distance2D(targetPosition, anchorPosition);
        if (targetAnchorDistance > playerAnchorDistance + TrashGapCloserAwayFromAnchorTolerance)
        {
            reason = $"trash pull dash would move away from tank pack: target={targetAnchorDistance:0.#}y anchor, player={playerAnchorDistance:0.#}y";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool ShouldUseHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out string reason)
    {
        return TryEvaluateHostileRelayDash(
            playerPosition,
            playerRadius,
            landingPosition,
            intendedTargetPosition,
            intendedTargetRadius,
            out _,
            out _,
            out reason);
    }

    public static bool TryEvaluateHostileRelayDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 landingPosition,
        Vector3 intendedTargetPosition,
        float intendedTargetRadius,
        out float gain,
        out float directionDot,
        out string reason)
    {
        var currentDistance = Geometry.DistanceToHitbox(playerPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        var landingDistance = Geometry.DistanceToHitbox(landingPosition, playerRadius, intendedTargetPosition, intendedTargetRadius);
        gain = currentDistance - landingDistance;
        if (gain < HostileRelayMinimumTargetGain)
        {
            directionDot = 0f;
            reason = $"relay low target gain: {gain:0.0}y";
            return false;
        }

        if (!TryDirectionDot(playerPosition, landingPosition, intendedTargetPosition, out directionDot))
        {
            reason = "relay direction unknown";
            return false;
        }

        if (directionDot < HostileRelayMinimumDirectionDot)
        {
            reason = $"relay wrong direction: {directionDot:0.00}";
            return false;
        }

        reason = $"relay gains {gain:0.0}y; direction {directionDot:0.00}";
        return true;
    }

    public static bool CanUseHostileRelayGapCloser(uint classJobId)
    {
        return classJobId is
            1 or 19 or
            3 or 21 or
            32 or
            37 or
            2 or 20 or
            4 or 22 or
            34 or
            41 or
            35 or
            40;
    }

    public static float ResolveHostileRelayGapCloserRange(uint classJobId)
    {
        return classJobId == 40
            ? 25f
            : CombatConstants.GapCloserMaxRange;
    }

    public static bool IsConservativeTrashPullContext(TrashPullDiagnostics trash)
    {
        return (trash.Phase is TrashPullPhase.Gathering or TrashPullPhase.Stabilizing or TrashPullPhase.Burning) &&
               trash.Confidence >= ConservativeTrashMinimumConfidence &&
               trash.DominantTargetCount >= ConservativeTrashMinimumTargets;
    }

    public static Vector3 ResolveTrashPullGapCloserAnchor(TrashPullDiagnostics trash, IGameObject target)
    {
        if (trash.Phase == TrashPullPhase.Gathering && trash.ProjectedTankPosition.HasValue)
        {
            return trash.ProjectedTankPosition.Value;
        }

        return trash.PackCentroid ?? target.Position;
    }

    public static float ResolveConservativeTrashGapCloserRange(uint classJobId)
    {
        return classJobId switch
        {
            25 or 40 => 25f,
            24 or 38 or 39 or 42 => CombatConstants.FixedForwardGapCloserRange,
            _ => CombatConstants.GapCloserMaxRange
        };
    }

    public static float ResolveConservativeTrashGapCloserUseThreshold(uint classJobId)
    {
        return MathF.Max(0f, ResolveConservativeTrashGapCloserRange(classJobId) - ConservativeTrashGapCloserRangeReserve);
    }

    private static bool TryDirectionDot(Vector3 from, Vector3 to, Vector3 toward, out float dot)
    {
        var movement = to - from;
        var target = toward - from;
        movement.Y = 0f;
        target.Y = 0f;
        if (movement.LengthSquared() <= 0.0001f || target.LengthSquared() <= 0.0001f)
        {
            dot = 0f;
            return false;
        }

        dot = Vector3.Dot(Vector3.Normalize(movement), Vector3.Normalize(target));
        return true;
    }
}
