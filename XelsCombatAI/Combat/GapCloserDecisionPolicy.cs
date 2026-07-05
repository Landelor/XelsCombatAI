using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const float FriendlyAnchorMinimumTargetGain = 3f;
    private const float FriendlyAnchorMinimumTargetDirectionDot = 0.55f;
    private const float FriendlyAnchorMaximumSafeDestinationLoss = 2f;
    private const float FriendlyAnchorClusterOutlierTolerance = 8f;
    private const float FriendlyAnchorKnockbackRecoveryRangeSlack = 0.5f;
    private const float FriendlyEscapeMinimumSafetyProgress = 5f;
    private const float FriendlyEscapeConservativeMinimumSafetyProgress = 8f;
    private const float FriendlyEscapeRelayMeleeSlack = 0.5f;
    private const float CurrentGcdLandingRangeSlack = 0.25f;
    private const float SpareChargeStrongRangeGain = 4f;
    private const float SpareChargeStrongSafetyOrPathGain = 3f;
    private const float MeleeStackRecoveryClusterRadius = 2.75f;
    private const float MeleeStackRecoveryRangeSlack = 0.5f;
    private const int MeleeStackRecoveryMinimumAllies = 2;
    private const float HostileRelayMinimumTargetGain = 4f;
    private const float HostileRelayMinimumDirectionDot = 0.45f;
    private const float KnockbackRecoveryMinimumDirectionDot = 0.35f;
    private const float BossCenterLandingSurfaceDistance = 0.75f;

    public static float EstimateWalkSeconds(float moveDistance)
        => PositionalTrueNorthPolicy.EstimateMovementSeconds(MathF.Max(0f, moveDistance));

    public static bool CanWalkToRangeBeforeGcd(float distanceToHitbox, float engagementRange, RsrGcdActionTimingSnapshot action, out string reason)
    {
        reason = string.Empty;
        if (distanceToHitbox <= engagementRange)
        {
            reason = $"already within {engagementRange:0.0}y engagement range";
            return true;
        }

        if (!AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            reason = "RSR GCD timing unavailable";
            return false;
        }

        var moveDistance = MathF.Max(0f, distanceToHitbox - engagementRange);
        var requiredSeconds = EstimateWalkSeconds(moveDistance);
        var budgetSeconds = PositionalTrueNorthPolicy.CalculateMovementBudgetSeconds(action.GcdRemaining, action.GcdActionAhead);
        if (requiredSeconds <= budgetSeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"walking can reach melee for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
            return true;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking misses melee for {action.ActionName} ({moveDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
        return false;
    }

    public static bool CanWalkToPositionalBeforeGcd(Positional requiredPositional, RsrGcdActionTimingSnapshot action, float moveDistance, out string reason)
    {
        return PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(requiredPositional, action, moveDistance, out reason);
    }

    public static bool ShouldUseReengageDashForCurrentGcd(
        float landingDistanceToHitbox,
        float engagementRange,
        RsrGcdActionTimingSnapshot? action,
        uint currentCharges,
        bool currentPositionUnsafe,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        var effectiveEngagementRange = MathF.Max(0f, engagementRange);
        var landingRange = effectiveEngagementRange + CurrentGcdLandingRangeSlack;
        if (landingDistanceToHitbox <= landingRange)
        {
            reason = $"landing restores useful range: {landingDistanceToHitbox:0.0}y / {effectiveEngagementRange:0.0}y";
            return true;
        }

        var timingReason = "RSR GCD timing unavailable";
        if (action != null &&
            AoeRepositionPolicy.HasReliableGcdTiming(action.GcdRemaining, action.GcdElapsed, action.GcdTotal))
        {
            var remainingWalkDistance = MathF.Max(0f, landingDistanceToHitbox - effectiveEngagementRange);
            var requiredSeconds = EstimateWalkSeconds(remainingWalkDistance);
            var budgetSeconds = PositionalTrueNorthPolicy.CalculateMovementBudgetSeconds(action.GcdRemaining, action.GcdActionAhead);
            if (requiredSeconds <= budgetSeconds)
            {
                reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"landing plus walk reaches range for {action.ActionName} ({remainingWalkDistance:0.0}y needs {requiredSeconds:0.0}s, {budgetSeconds:0.0}s before RSR action window)");
                return true;
            }

            timingReason = string.Create(
                CultureInfo.InvariantCulture,
                $"landing still misses {action.ActionName} ({landingDistanceToHitbox:0.0}y from target, {requiredSeconds:0.0}s walk, {budgetSeconds:0.0}s before RSR action window)");
        }

        if (currentPositionUnsafe &&
            (safetyGain >= SpareChargeStrongSafetyOrPathGain || pathGain >= SpareChargeStrongSafetyOrPathGain))
        {
            reason = $"safety dash allowed: {timingReason}; safety gain {safetyGain:0.0}y, path gain {pathGain:0.0}y";
            return true;
        }

        if (currentCharges >= 2 &&
            (uptimeGain >= SpareChargeStrongRangeGain ||
             safetyGain >= SpareChargeStrongSafetyOrPathGain ||
             pathGain >= SpareChargeStrongSafetyOrPathGain))
        {
            reason = $"spare-charge dash allowed: {timingReason}; uptime gain {uptimeGain:0.0}y, safety gain {safetyGain:0.0}y, path gain {pathGain:0.0}y";
            return true;
        }

        reason = currentCharges <= 1
            ? $"last charge held: {timingReason}"
            : timingReason;
        return false;
    }

    public static bool CanWalkToBossModSafetyBeforeUrgency(float safeMovementDistance, BossModMovementDiagnostics movement, out string reason)
    {
        if (!TryGetBossModMovementUrgency(movement, out var urgencySeconds))
        {
            reason = "BMR movement timing unavailable";
            return false;
        }

        var requiredSeconds = EstimateWalkSeconds(safeMovementDistance);
        if (requiredSeconds <= urgencySeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"walking can reach BMR safety ({safeMovementDistance:0.0}y needs {requiredSeconds:0.0}s, {urgencySeconds:0.0}s available)");
            return true;
        }

        reason = string.Create(
            CultureInfo.InvariantCulture,
            $"walking misses BMR safety timing ({safeMovementDistance:0.0}y needs {requiredSeconds:0.0}s, {urgencySeconds:0.0}s available)");
        return false;
    }

    public static bool TryGetBossModMovementUrgency(BossModMovementDiagnostics movement, out float urgencySeconds)
    {
        urgencySeconds = float.MaxValue;
        var found = false;
        AddUrgency(movement.NavigationDetails.ForceMovementIn, ref urgencySeconds, ref found);
        AddUrgency(movement.NavigationDetails.LeewaySeconds, ref urgencySeconds, ref found);
        return found;
    }

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

    public static bool ShouldUseFriendlyAnchorDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        out string reason)
    {
        if (!ShouldUseFriendlyAnchorDash(moveDistance, safetyGain, uptimeGain, pathGain, out reason))
        {
            return false;
        }

        if (!TryEvaluateFriendlyAnchorTargetProgress(
                playerPosition,
                playerRadius,
                anchorPosition,
                targetPosition,
                targetRadius,
                out var targetGain,
                out var directionDot,
                out reason))
        {
            return false;
        }

        if (targetGain < FriendlyAnchorMinimumTargetGain)
        {
            reason = $"ally anchor low target progress: {targetGain:0.0}y";
            return false;
        }

        if (directionDot < FriendlyAnchorMinimumTargetDirectionDot)
        {
            reason = $"ally anchor sideways to target: {directionDot:0.00}";
            return false;
        }

        reason = $"ally anchor gains {targetGain:0.0}y toward target; direction {directionDot:0.00}";
        return true;
    }

    public static bool ShouldUseFriendlyEscapeAnchorDash(
        float moveDistance,
        float configuredMinimumMoveDistance,
        float pathGain,
        bool uptimeRelayAvailable,
        string uptimeRelayReason,
        bool conservativeSingleCharge,
        bool currentPositionUnsafe,
        float safeMovementDistance,
        BossModMovementDiagnostics movement,
        out string reason)
    {
        if (uptimeRelayAvailable)
        {
            reason = uptimeRelayReason;
            return true;
        }

        var minimumMoveDistance = MathF.Max(0f, configuredMinimumMoveDistance);
        if (moveDistance < minimumMoveDistance)
        {
            reason = $"ally anchor below configured dash distance: {moveDistance:0.0}y / {minimumMoveDistance:0.0}y";
            return false;
        }

        var minimumProgress = conservativeSingleCharge
            ? FriendlyEscapeConservativeMinimumSafetyProgress
            : FriendlyEscapeMinimumSafetyProgress;
        if (pathGain < minimumProgress)
        {
            reason = $"ally anchor low safety progress: {MathF.Max(0f, pathGain):0.0}y / {minimumProgress:0.0}y";
            return false;
        }

        if (conservativeSingleCharge &&
            currentPositionUnsafe &&
            CanWalkToBossModSafetyBeforeUrgency(safeMovementDistance, movement, out var walkReason))
        {
            reason = $"single-charge ally dash held: {walkReason}";
            return false;
        }

        reason = $"ally anchor saves {pathGain:0.0}y toward BMR safety";
        return true;
    }

    public static bool ShouldUseFriendlyEscapeUptimeRelay(
        uint classJobId,
        uint currentCharges,
        bool finalTargetMoving,
        string finalTargetMovementReason,
        float currentTargetDistance,
        float anchorTargetDistance,
        float targetDashRange,
        out string reason)
    {
        if (JobRoles.GetRangeRole(classJobId) != RangeRole.Melee &&
            !JobRoles.IsTankJob(classJobId))
        {
            reason = "ally relay requires a melee or tank job";
            return false;
        }

        if (currentCharges < 2)
        {
            reason = $"ally relay needs spare dash charge: {currentCharges}";
            return false;
        }

        if (finalTargetMoving)
        {
            reason = $"ally relay held: {finalTargetMovementReason}";
            return false;
        }

        var meleeRange = CombatConstants.MeleeActionRange + FriendlyEscapeRelayMeleeSlack;
        if (anchorTargetDistance <= meleeRange &&
            currentTargetDistance > meleeRange)
        {
            reason = $"ally relay reaches melee: target {anchorTargetDistance:0.0}y, charges={currentCharges}";
            return true;
        }

        var effectiveDashRange = MathF.Max(0f, targetDashRange);
        if (currentTargetDistance <= effectiveDashRange)
        {
            reason = "target already in dash range";
            return false;
        }

        if (anchorTargetDistance > effectiveDashRange)
        {
            reason = $"ally relay outside target dash range: {anchorTargetDistance:0.0}y / {effectiveDashRange:0.0}y";
            return false;
        }

        reason = $"ally relay puts target in dash range: {anchorTargetDistance:0.0}y / {effectiveDashRange:0.0}y, charges={currentCharges}";
        return true;
    }

    public static bool ShouldUseStableFriendlyAnchorDash(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        Vector3? safeDestination,
        IReadOnlyList<Vector3> partyPositions,
        BossModMechanicPressure pressure,
        bool currentPositionUnsafe,
        float moveDistance,
        float safetyGain,
        float uptimeGain,
        float pathGain,
        float engagementRange,
        bool anchorAntiKnockbackActive,
        out string reason)
    {
        if (pressure.SharedDamageSoon && !currentPositionUnsafe && safetyGain <= 0.1f)
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return false;
        }

        if (!ShouldUseFriendlyAnchorDash(
                playerPosition,
                playerRadius,
                anchorPosition,
                targetPosition,
                targetRadius,
                moveDistance,
                safetyGain,
                uptimeGain,
                pathGain,
                out reason))
        {
            return false;
        }

        if (pressure.KnockbackRecoveryActive && !anchorAntiKnockbackActive)
        {
            var anchorTargetDistance = Geometry.DistanceToHitbox(anchorPosition, playerRadius, targetPosition, targetRadius);
            if (anchorTargetDistance > engagementRange + FriendlyAnchorKnockbackRecoveryRangeSlack)
            {
                reason = $"ally anchor held during knockback recovery: {anchorTargetDistance:0.0}y from target";
                return false;
            }
        }

        if (safeDestination.HasValue)
        {
            var playerSafeDistance = Geometry.Distance2D(playerPosition, safeDestination.Value);
            var anchorSafeDistance = Geometry.Distance2D(anchorPosition, safeDestination.Value);
            if (anchorSafeDistance > playerSafeDistance + FriendlyAnchorMaximumSafeDestinationLoss)
            {
                reason = $"ally anchor moves away from BMR safety: {anchorSafeDistance:0.0}y vs {playerSafeDistance:0.0}y";
                return false;
            }
        }

        if (TryGetPartyClusterCenter(partyPositions, out var clusterCenter))
        {
            var playerClusterDistance = Geometry.Distance2D(playerPosition, clusterCenter);
            var anchorClusterDistance = Geometry.Distance2D(anchorPosition, clusterCenter);
            if (anchorClusterDistance > MathF.Max(playerClusterDistance, FriendlyAnchorClusterOutlierTolerance) + 2f)
            {
                reason = $"ally anchor is outside party stack: {anchorClusterDistance:0.0}y";
                return false;
            }
        }

        return true;
    }

    public static bool ShouldUseMeleeStackRecoveryAnchorDash(
        bool playerIsMeleeRangeRole,
        bool reengageWalkBlocked,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float playerRadius,
        float targetRadius,
        float engagementRange,
        IReadOnlyList<Vector3> partyPositions,
        BossModMechanicPressure pressure,
        float moveDistance,
        out string reason)
    {
        if (!playerIsMeleeRangeRole)
        {
            reason = "melee stack recovery requires a melee job";
            return false;
        }

        if (!reengageWalkBlocked)
        {
            reason = "melee stack recovery held: walking path not blocked";
            return false;
        }

        if (pressure.MovementLockSoon || pressure.FreezingSoon || pressure.MisdirectionActive)
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return false;
        }

        if (moveDistance < FriendlyAnchorMinimumMoveDistance)
        {
            reason = $"melee stack recovery anchor too close: {moveDistance:0.0}y";
            return false;
        }

        var anchorTargetDistance = Geometry.DistanceToHitbox(anchorPosition, playerRadius, targetPosition, targetRadius);
        if (anchorTargetDistance > engagementRange + MeleeStackRecoveryRangeSlack)
        {
            reason = $"melee stack recovery anchor outside melee range: {anchorTargetDistance:0.0}y";
            return false;
        }

        if (!TryCountPartyStackNearAnchor(partyPositions, anchorPosition, out var stackedAllies) ||
            stackedAllies < MeleeStackRecoveryMinimumAllies)
        {
            reason = $"melee stack recovery needs party stack: {stackedAllies} allies";
            return false;
        }

        reason = $"melee stack recovery via {stackedAllies}-ally safe melee pocket";
        return true;
    }

    public static bool ShouldHoldReengageForMechanicPressure(
        BossModMechanicPressure pressure,
        bool walkingWouldMissUsefulGcd,
        bool safetyDash,
        bool strongStyleGainRequired,
        out string reason)
    {
        reason = string.Empty;
        if (safetyDash || pressure.KnockbackRecoveryActive)
        {
            return false;
        }

        if (pressure.MovementLockSoon || pressure.FreezingSoon || pressure.MisdirectionActive)
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return true;
        }

        if (pressure.DowntimeSoon && !IsVulnerabilityStarting(pressure))
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return true;
        }

        if ((pressure.KnockbackSoon || pressure.RaidwideOrDamageSoon) &&
            !walkingWouldMissUsefulGcd &&
            !strongStyleGainRequired)
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return true;
        }

        return false;
    }

    public static bool ShouldUsePostDowntimeReengage(
        bool targetAttackable,
        bool walkingWouldMissUsefulGcd,
        BossModMechanicPressure pressure,
        out string reason)
    {
        if (!IsDowntimeActiveOrEnding(pressure) && !IsVulnerabilityStarting(pressure))
        {
            reason = string.Empty;
            return true;
        }

        if (!targetAttackable)
        {
            reason = "downtime re-entry held: target not attackable";
            return false;
        }

        if (!walkingWouldMissUsefulGcd)
        {
            reason = "downtime re-entry held: walking reaches next offensive GCD";
            return false;
        }

        reason = "downtime re-entry allowed for missed offensive GCD";
        return true;
    }

    public static bool ShouldHoldCasterImmobilityWindow(
        uint classJobId,
        bool playerCasting,
        bool stationaryBuffActive,
        bool currentPositionUnsafe,
        out string reason)
    {
        if (currentPositionUnsafe)
        {
            reason = string.Empty;
            return false;
        }

        if (playerCasting)
        {
            reason = "caster dash held while casting";
            return true;
        }

        if (!IsCasterLikeJob(classJobId))
        {
            reason = string.Empty;
            return false;
        }

        if (stationaryBuffActive)
        {
            reason = "caster dash held for stationary buff window";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public static bool ShouldAllowKnockbackRecoveryDashDirection(
        Vector3 playerPosition,
        Vector3 destination,
        Vector3 targetPosition,
        Vector3? safeDestination,
        bool onlyValidatedSafetyImprovement,
        out string reason)
    {
        if (TryDirectionDot(playerPosition, destination, targetPosition, out var targetDot) &&
            targetDot >= KnockbackRecoveryMinimumDirectionDot)
        {
            reason = $"knockback recovery returns toward target: {targetDot:0.00}";
            return true;
        }

        if (safeDestination.HasValue &&
            TryDirectionDot(playerPosition, destination, safeDestination.Value, out var safeDot) &&
            safeDot >= KnockbackRecoveryMinimumDirectionDot)
        {
            reason = $"knockback recovery returns toward BMR safety: {safeDot:0.00}";
            return true;
        }

        if (onlyValidatedSafetyImprovement)
        {
            reason = "knockback recovery allowed as validated safety movement";
            return true;
        }

        reason = $"knockback recovery sideways: {(TryDirectionDot(playerPosition, destination, targetPosition, out var dot) ? dot.ToString("0.00", CultureInfo.InvariantCulture) : "unknown")}";
        return false;
    }

    public static bool ShouldRejectNonTankFrontOrCenterLanding(
        bool playerIsTank,
        Vector3 destination,
        Vector3 targetPosition,
        float targetRotation,
        float playerRadius,
        float targetRadius,
        bool alternativeExists,
        out string reason)
    {
        reason = string.Empty;
        if (playerIsTank || !alternativeExists)
        {
            return false;
        }

        if (Geometry.DistanceToHitbox(destination, playerRadius, targetPosition, targetRadius) <= BossCenterLandingSurfaceDistance)
        {
            reason = "landing too close to boss center while alternatives exist";
            return true;
        }

        if (PositionalDashPolicy.IsSatisfied(Positional.Front, destination, targetPosition, targetRotation))
        {
            reason = "non-tank front landing rejected while side/rear alternative exists";
            return true;
        }

        return false;
    }

    public static bool ShouldUsePairedReturnDash(
        float distanceToHitbox,
        float engagementRange,
        RsrGcdActionTimingSnapshot? nextOffensiveGcd,
        bool currentPositionUnsafe,
        out string reason)
    {
        if (currentPositionUnsafe)
        {
            reason = "paired return allowed for safety recovery";
            return true;
        }

        if (distanceToHitbox <= engagementRange)
        {
            reason = "paired return held: already in useful range";
            return false;
        }

        if (nextOffensiveGcd != null &&
            CanWalkToRangeBeforeGcd(distanceToHitbox, engagementRange, nextOffensiveGcd, out var walkReason))
        {
            reason = $"paired return held: {walkReason}";
            return false;
        }

        reason = "paired return restores useful uptime";
        return true;
    }

    public static bool ShouldRunSafetyGapCloserDuringManualSuppression(
        bool suppressAutomatedMovement,
        bool gapClosersEnabled)
    {
        return suppressAutomatedMovement && gapClosersEnabled;
    }

    public static bool ShouldBlockAllOptionalDashesForPressure(BossModMechanicPressure pressure, out string reason)
    {
        if (pressure.MovementLockSoon || pressure.FreezingSoon || pressure.MisdirectionActive)
        {
            reason = pressure.FormatOptionalMovementHoldReason();
            return true;
        }

        reason = string.Empty;
        return false;
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

    public static bool ShouldConserveTrashPullGapCloser(
        float triggerDistance,
        float useThreshold,
        uint currentCharges,
        bool currentPositionUnsafe,
        bool walkingWouldMissUsefulGcd,
        out string reason)
    {
        reason = string.Empty;
        if (triggerDistance >= useThreshold)
        {
            return false;
        }

        if (currentPositionUnsafe)
        {
            return false;
        }

        if (currentCharges >= 2 && walkingWouldMissUsefulGcd)
        {
            return false;
        }

        reason = $"trash pull conserving gap closer: {triggerDistance:0.#}y / {useThreshold:0.#}y";
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

    private static bool TryEvaluateFriendlyAnchorTargetProgress(
        Vector3 playerPosition,
        float playerRadius,
        Vector3 anchorPosition,
        Vector3 targetPosition,
        float targetRadius,
        out float gain,
        out float directionDot,
        out string reason)
    {
        var currentDistance = Geometry.DistanceToHitbox(playerPosition, playerRadius, targetPosition, targetRadius);
        var anchorDistance = Geometry.DistanceToHitbox(anchorPosition, playerRadius, targetPosition, targetRadius);
        gain = currentDistance - anchorDistance;
        if (!TryDirectionDot(playerPosition, anchorPosition, targetPosition, out directionDot))
        {
            reason = "ally anchor target direction unknown";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetPartyClusterCenter(IReadOnlyList<Vector3> partyPositions, out Vector3 center)
    {
        center = default;
        if (partyPositions.Count < 2)
        {
            return false;
        }

        foreach (var position in partyPositions)
        {
            center += position;
        }

        center /= partyPositions.Count;
        return true;
    }

    private static bool TryCountPartyStackNearAnchor(IReadOnlyList<Vector3> partyPositions, Vector3 anchorPosition, out int stackedAllies)
    {
        stackedAllies = 0;
        if (partyPositions.Count == 0)
        {
            return false;
        }

        foreach (var position in partyPositions)
        {
            if (Geometry.Distance2D(position, anchorPosition) <= MeleeStackRecoveryClusterRadius)
            {
                stackedAllies++;
            }
        }

        return true;
    }

    private static bool IsDowntimeActiveOrEnding(BossModMechanicPressure pressure)
    {
        return IsFiniteTimer(pressure.BMRDowntimeEndIn) &&
               (!IsFiniteTimer(pressure.BMRDowntimeIn) || pressure.BMRDowntimeEndIn < pressure.BMRDowntimeIn);
    }

    private static bool IsVulnerabilityStarting(BossModMechanicPressure pressure)
    {
        return pressure.VulnerableSoon ||
               (IsFiniteTimer(pressure.BMRVulnerableIn) && pressure.BMRVulnerableIn <= BossModMechanicPressure.VulnerablePressureSeconds);
    }

    private static bool IsCasterLikeJob(uint classJobId)
    {
        return JobRoles.GetRangeRole(classJobId) is RangeRole.MagicRanged or RangeRole.Healer;
    }

    private static bool IsFiniteTimer(float value)
    {
        return float.IsFinite(value) && value < float.MaxValue / 2f;
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
            38 => CombatConstants.DancerEnAvantRange,
            24 or 39 or 42 => CombatConstants.FixedForwardGapCloserRange,
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

    private static void AddUrgency(float? value, ref float urgencySeconds, ref bool found)
    {
        if (!value.HasValue ||
            !float.IsFinite(value.Value) ||
            value.Value < 0f)
        {
            return;
        }

        urgencySeconds = MathF.Min(urgencySeconds, value.Value);
        found = true;
    }
}
