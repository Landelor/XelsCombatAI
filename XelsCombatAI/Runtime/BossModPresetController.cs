using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Runtime;

internal sealed class BossModPresetController(
    Configuration config,
    DalamudServices services,
    BossModIpc bossMod,
    BossModReflectionSafety bossModSafety,
    TargetUptimePlanner targetUptimePlanner,
    PositionalsController positionalsController,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RedMageMeleeComboController redMageMeleeComboController,
    BlackMageLeyLinesPositioningController blackMageLeyLinesPositioningController,
    PictomancerStarryMusePositioningController pictomancerStarryMusePositioningController,
    FacingController facingController,
    Func<BossModMechanicPressure> mechanicPressure,
    Func<BossModMovementDiagnostics> bossModMovementDiagnostics)
{
    private static readonly TimeSpan UptimeWalkSuppressionLinger = TimeSpan.FromMilliseconds(750);
    private const float MaximumSuppressedUptimeRange = 8f;
    private const float UptimeRangeSlack = 0.5f;
    private const int BossRingSampleCount = 24;

    public Positional LastPositional { get; private set; } = Positional.Any;
    public float LastTargetUptimeRange { get; private set; } = -1f;
    public string LastTargetUptimeRangeSource { get; private set; } = "none";
    public string LastTargetUptimeRangeReason { get; private set; } = "not checked";
    public bool? LastMovement { get; private set; }
    public string? LastMovementRangeStrategy { get; private set; }
    public string? LastForbiddenZoneCushion { get; private set; }
    public bool? LastLeylinesBetweenTheLines { get; private set; }
    public bool? LastLeylinesRetrace { get; private set; }
    public bool? LastLeylinesGoal { get; private set; }
    public bool InitializedPreset { get; private set; }
    private bool bossModPositionalNeutral;
    private DateTime suppressUptimeWalkUntil = DateTime.MinValue;
    private ulong suppressedUptimeTargetId;
    private string lastUptimeWalkSuppressionReason = "not suppressed";

    public bool Initialize()
    {
        try
        {
            if (!bossMod.EnsurePreset())
            {
                return false;
            }

            if (!bossMod.SetActive(BossModIpc.DefaultPresetName))
            {
                return false;
            }

            if (bossMod.SetPositional(BossModIpc.DefaultPresetName, Positional.Any))
            {
                this.LastPositional = Positional.Any;
                this.bossModPositionalNeutral = true;
            }

            this.InitializedPreset = true;
            return true;
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not initialize BossMod preset yet.");
            return false;
        }
    }

    public void Deactivate()
    {
        if (!bossMod.IsAvailable())
        {
            this.MarkUninitialized();
            return;
        }

        var presetName = BossModIpc.DefaultPresetName;
        try
        {
            this.WriteNeutralStrategies(presetName);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not write neutral BossMod strategies during deactivation.");
        }

        try
        {
            if (bossMod.GetActive() == presetName)
            {
                bossMod.ClearActive();
            }
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not clear active BossMod preset.");
        }

        try
        {
            bossMod.ClearTransientPresetStrategies(presetName);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not clear BossMod transient preset strategies.");
        }
    }

    public bool IsActivePreset()
    {
        try
        {
            return bossMod.IsAvailable() &&
                   bossMod.GetActive() == BossModIpc.DefaultPresetName;
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not check active BossMod preset.");
            return false;
        }
    }

    public void ApplyStrategies(bool suppressAutomatedMovement)
    {
        try
        {
            var targetUptimeRange = targetUptimePlanner.CalculateTargetUptimeRange();
            var plannedTargetUptimeRange = targetUptimeRange;
            this.LastTargetUptimeRangeSource = targetUptimePlanner.LastTargetUptimeRangeSource;
            this.LastTargetUptimeRangeReason = targetUptimePlanner.LastTargetUptimeRangeReason;
            var suppressTargetUptimeRange = this.ShouldSuppressTargetUptimeRange(suppressAutomatedMovement, plannedTargetUptimeRange, out var targetUptimeSuppressionReason);
            if (suppressTargetUptimeRange)
            {
                targetUptimeRange = Configuration.InternalDisabledUptimeRange;
                this.LastTargetUptimeRangeSource = "local";
                this.LastTargetUptimeRangeReason = $"{this.LastTargetUptimeRangeReason}; {targetUptimeSuppressionReason}";
            }

            var movementDiagnostics = bossModMovementDiagnostics();
            var suppressUptimeWalk = this.ShouldSuppressUptimeWalk(targetUptimeRange, movementDiagnostics, out var uptimeSuppressionReason);
            if (suppressUptimeWalk)
            {
                targetUptimeRange = Configuration.InternalDisabledUptimeRange;
                this.LastTargetUptimeRangeSource = "local";
                this.LastTargetUptimeRangeReason = $"{this.LastTargetUptimeRangeReason}; {uptimeSuppressionReason}";
            }

            var suppressSafeLowPriorityMovement = this.ShouldSuppressSafeLowPriorityMovementForActiveCast(suppressAutomatedMovement, plannedTargetUptimeRange, out var lowPriorityMovementSuppressionReason);
            if (suppressSafeLowPriorityMovement)
            {
                this.LastTargetUptimeRangeSource = "local";
                this.LastTargetUptimeRangeReason = $"{this.LastTargetUptimeRangeReason}; {lowPriorityMovementSuppressionReason}";
            }

            this.SetTargetUptimeRange(targetUptimeRange);

            this.SetForbiddenZoneCushion(config.ManageForbiddenZoneDistance && !suppressSafeLowPriorityMovement
                ? MapForbiddenZoneCushion(config.PreferredForbiddenZoneDistance)
                : "None");

            this.SetMovementRangeStrategy(config.ManageMovement && !suppressTargetUptimeRange && !suppressUptimeWalk && !suppressSafeLowPriorityMovement
                ? MapCombatStyle(config.CombatStyle)
                : "Any");

            if (config.ManagePositionals)
            {
                positionalsController.Apply();
            }
            else
            {
                this.SetPositional(Positional.Any);
            }

            this.SetMovement(config.ManageMovement && !suppressAutomatedMovement && !suppressSafeLowPriorityMovement);

            this.SetLeylines(false, false, false);

            this.SetGapClosers(suppressAutomatedMovement);
            this.SetMovementForPendingDashTurn(suppressAutomatedMovement);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not update BossMod strategies yet.");
            this.InitializedPreset = false;
        }
    }

    private bool ShouldSuppressTargetUptimeRange(bool suppressAutomatedMovement, float targetUptimeRange, out string reason)
    {
        reason = string.Empty;
        if (suppressAutomatedMovement)
        {
            reason = "target uptime range held during manual movement";
            return true;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || !CasterMovementPolicy.HasActiveCastTime(player))
        {
            return false;
        }

        var slidecastWindow = CasterMovementPolicy.IsCasterSlidecastWindow(player);
        if (slidecastWindow || this.HasSafeOutOfRangeCorrection(player, targetUptimeRange))
        {
            return false;
        }

        reason = slidecastWindow
            ? "target uptime range held during slidecast"
            : "target uptime range held during active cast";
        return true;
    }

    private bool ShouldSuppressSafeLowPriorityMovementForActiveCast(bool suppressAutomatedMovement, float targetUptimeRange, out string reason)
    {
        reason = string.Empty;
        if (suppressAutomatedMovement || !config.ManageMovement)
        {
            return false;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null ||
            !CasterMovementPolicy.HasActiveCastTime(player) ||
            CasterMovementPolicy.IsCasterSlidecastWindow(player))
        {
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentPositionSafe, out _))
        {
            return false;
        }

        return ShouldSuppressSafeLowPriorityMovementForActiveCast(
            activeNonSlidecastCast: true,
            currentPositionSafe,
            safeOutOfRangeCorrectionAvailable: this.HasSafeOutOfRangeCorrection(player, targetUptimeRange),
            out reason);
    }

    internal static bool ShouldSuppressSafeLowPriorityMovementForActiveCast(
        bool activeNonSlidecastCast,
        bool currentPositionSafe,
        bool safeOutOfRangeCorrectionAvailable,
        out string reason)
    {
        reason = string.Empty;
        if (!activeNonSlidecastCast || !currentPositionSafe || safeOutOfRangeCorrectionAvailable)
        {
            return false;
        }

        reason = "low priority movement held during active cast while current position is safe";
        return true;
    }

    private bool HasSafeOutOfRangeCorrection(IBattleChara player, float targetUptimeRange)
    {
        if (targetUptimeRange <= 0f ||
            services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            return false;
        }

        if (!IsOutsideUptimeRange(
                targetUptimeRange,
                player.Position,
                player.HitboxRadius,
                target.Position,
                target.HitboxRadius))
        {
            return false;
        }

        return this.TryFindSafeBossRingPoint(player, target, targetUptimeRange, out _, out var lineCheck, out _) &&
               (lineCheck == null || lineCheck.Clear);
    }

    internal static bool IsOutsideUptimeRange(
        float targetUptimeRange,
        Vector3 playerPosition,
        float playerRadius,
        Vector3 targetPosition,
        float targetRadius)
    {
        if (targetUptimeRange <= 0f)
        {
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(playerPosition, playerRadius, targetPosition, targetRadius);
        return distanceToHitbox > targetUptimeRange + UptimeRangeSlack;
    }

    public void ResetCache()
    {
        this.InitializedPreset = false;
        this.LastPositional = Positional.Any;
        this.LastTargetUptimeRange = -1f;
        this.LastTargetUptimeRangeSource = "none";
        this.LastTargetUptimeRangeReason = "reset";
        this.LastMovement = null;
        this.LastMovementRangeStrategy = null;
        this.LastForbiddenZoneCushion = null;
        this.LastLeylinesBetweenTheLines = null;
        this.LastLeylinesRetrace = null;
        this.LastLeylinesGoal = null;
        this.bossModPositionalNeutral = false;
        this.suppressUptimeWalkUntil = DateTime.MinValue;
        this.suppressedUptimeTargetId = 0;
        this.lastUptimeWalkSuppressionReason = "reset";
        positionalsController.Reset();
        gapCloserController.Reset();
        escapeGapCloserController.Reset();
        redMageMeleeComboController.Reset();
        blackMageLeyLinesPositioningController.Reset();
        bossModSafety.Reset();
    }

    public void MarkUninitialized()
    {
        this.InitializedPreset = false;
    }

    public void SetPositional(Positional positional)
    {
        if (positional != Positional.Any)
        {
            this.LastPositional = positional;
            return;
        }

        if (positional == this.LastPositional && this.bossModPositionalNeutral)
        {
            return;
        }

        if (bossMod.SetPositional(BossModIpc.DefaultPresetName, Positional.Any))
        {
            this.LastPositional = Positional.Any;
            this.bossModPositionalNeutral = true;
        }
    }

    private void SetTargetUptimeRange(float range)
    {
        if (Math.Abs(this.LastTargetUptimeRange - range) <= 0.01f)
        {
            return;
        }

        if (bossMod.SetRange(BossModIpc.DefaultPresetName, range))
        {
            this.LastTargetUptimeRange = range;
        }
    }

    private void SetMovement(bool enabled)
    {
        if (this.LastMovement == enabled)
        {
            return;
        }

        if (bossMod.SetMovement(BossModIpc.DefaultPresetName, enabled))
        {
            this.LastMovement = enabled;
        }
    }

    private void SetForbiddenZoneCushion(string cushion)
    {
        if (this.LastForbiddenZoneCushion == cushion)
        {
            return;
        }

        if (bossMod.SetForbiddenZoneCushion(BossModIpc.DefaultPresetName, cushion))
        {
            this.LastForbiddenZoneCushion = cushion;
        }
    }

    private void SetMovementRangeStrategy(string strategy)
    {
        if (this.LastMovementRangeStrategy == strategy)
        {
            return;
        }

        if (bossMod.SetMovementRangeStrategy(BossModIpc.DefaultPresetName, strategy))
        {
            this.LastMovementRangeStrategy = strategy;
        }
    }

    private bool ShouldSuppressUptimeWalk(float targetUptimeRange, BossModMovementDiagnostics movement, out string reason)
    {
        reason = string.Empty;
        var now = DateTime.UtcNow;
        var player = services.ObjectTable.LocalPlayer;
        if (!config.ManageMovement ||
            targetUptimeRange <= 0f ||
            targetUptimeRange > MaximumSuppressedUptimeRange ||
            player == null ||
            player.IsDead ||
            player.CurrentHp == 0 ||
            services.TargetManager.Target is not IBattleChara target ||
            target.IsDead ||
            target.CurrentHp == 0 ||
            !targetUptimePlanner.CurrentTargetHasBossModule())
        {
            this.ClearUptimeWalkSuppression();
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (distanceToHitbox <= targetUptimeRange + UptimeRangeSlack)
        {
            this.ClearUptimeWalkSuppression();
            return false;
        }

        if (!HasMechanicReengageBlocker(movement.HintDetails))
        {
            this.ClearUptimeWalkSuppression();
            return false;
        }

        if (this.suppressedUptimeTargetId == target.GameObjectId &&
            now < this.suppressUptimeWalkUntil)
        {
            reason = this.lastUptimeWalkSuppressionReason;
            return true;
        }

        if (HasClearImmediateWaypoint(player.Position, movement))
        {
            this.ClearUptimeWalkSuppression();
            return false;
        }

        if (!this.TryFindSafeBossRingPoint(player, target, targetUptimeRange, out var candidate, out var lineCheck, out var candidateReason))
        {
            reason = candidateReason;
            this.suppressedUptimeTargetId = target.GameObjectId;
            this.suppressUptimeWalkUntil = now.Add(UptimeWalkSuppressionLinger);
            this.lastUptimeWalkSuppressionReason = $"uptime walk held: {candidateReason}";
            reason = this.lastUptimeWalkSuppressionReason;
            return true;
        }

        if (lineCheck == null || lineCheck.Clear)
        {
            this.ClearUptimeWalkSuppression();
            return false;
        }

        reason = $"uptime walk held: {lineCheck.Reason} blocks boss re-engage; safe dash may still be used if available";
        this.suppressedUptimeTargetId = target.GameObjectId;
        this.suppressUptimeWalkUntil = now.Add(UptimeWalkSuppressionLinger);
        this.lastUptimeWalkSuppressionReason = reason;
        return true;
    }

    internal static bool HasMechanicReengageBlocker(BossModHintDiagnostics hints)
    {
        return hints.ForbiddenZones.GetValueOrDefault() > 0 ||
               hints.TemporaryObstacles.GetValueOrDefault() > 0 ||
               hints.ForbiddenDirections.GetValueOrDefault() > 0 ||
               (hints.ForcedMovement is { } forcedMovement && XzLengthSquared(forcedMovement) > 0.01f) ||
               HasActiveSpecialMode(hints.ImminentSpecialMode);
    }

    private static bool HasActiveSpecialMode(string? mode)
    {
        return !string.IsNullOrWhiteSpace(mode) &&
               !string.Equals(mode, "<none>", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasClearImmediateWaypoint(Vector3 playerPosition, BossModMovementDiagnostics movement)
    {
        if (!movement.NavigationNextWaypointPosition.HasValue)
        {
            return false;
        }

        var waypoint = ToPlayerPlane(playerPosition, movement.NavigationNextWaypointPosition.Value);
        return bossModSafety.TryCheckNavigationLine(playerPosition, waypoint, out var waypointLine) &&
               waypointLine.Clear;
    }

    private bool TryFindSafeBossRingPoint(IBattleChara player, IBattleChara target, float targetUptimeRange, out Vector3 point, out BossModNavigationLineCheck? lineCheck, out string reason)
    {
        var ringRadius = target.HitboxRadius + player.HitboxRadius + MathF.Max(CombatConstants.MeleeActionRange, targetUptimeRange);
        Vector3? nearestBlockedPoint = null;
        BossModNavigationLineCheck? nearestBlockedLine = null;
        reason = string.Empty;
        foreach (var candidate in EnumerateBossRingCandidates(player.Position, target.Position, ringRadius))
        {
            if (!bossModSafety.TryIsPositionSafe(candidate, out var safe, out var candidateReason))
            {
                reason = candidateReason;
                continue;
            }

            if (!safe)
            {
                reason = candidateReason;
                continue;
            }

            if (!bossModSafety.TryCheckNavigationLine(player.Position, candidate, out var candidateLine) ||
                candidateLine.Clear)
            {
                point = candidate;
                lineCheck = candidateLine;
                reason = candidateLine.Reason;
                return true;
            }

            nearestBlockedPoint ??= candidate;
            nearestBlockedLine ??= candidateLine;
            reason = candidateLine.Reason;
        }

        if (nearestBlockedPoint.HasValue)
        {
            point = nearestBlockedPoint.Value;
            lineCheck = nearestBlockedLine;
            reason = nearestBlockedLine?.Reason ?? "boss re-engage path blocked";
            return true;
        }

        point = default;
        lineCheck = null;
        reason = "no safe boss re-engage point found";
        return false;
    }

    internal static IEnumerable<Vector3> EnumerateBossRingCandidates(Vector3 playerPosition, Vector3 targetPosition, float ringRadius)
    {
        var toPlayer = playerPosition - targetPosition;
        toPlayer.Y = 0f;
        var baseAngle = 0f;
        if (toPlayer.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toPlayer);
            baseAngle = MathF.Atan2(direction.Z, direction.X);
            yield return new Vector3(targetPosition.X + direction.X * ringRadius, playerPosition.Y, targetPosition.Z + direction.Z * ringRadius);
        }
        else
        {
            yield return RingPoint(targetPosition, playerPosition.Y, ringRadius, baseAngle);
        }

        var angleStep = MathF.Tau / BossRingSampleCount;
        for (var step = 1; step < BossRingSampleCount / 2; step++)
        {
            yield return RingPoint(targetPosition, playerPosition.Y, ringRadius, baseAngle + (angleStep * step));
            yield return RingPoint(targetPosition, playerPosition.Y, ringRadius, baseAngle - (angleStep * step));
        }

        yield return RingPoint(targetPosition, playerPosition.Y, ringRadius, baseAngle + (angleStep * (BossRingSampleCount / 2)));
    }

    private static Vector3 RingPoint(Vector3 targetPosition, float y, float ringRadius, float angle)
        => new(
            targetPosition.X + MathF.Cos(angle) * ringRadius,
            y,
            targetPosition.Z + MathF.Sin(angle) * ringRadius);

    private static Vector3 ToPlayerPlane(Vector3 playerPosition, Vector2 position)
        => new(position.X, playerPosition.Y, position.Y);

    private static float XzLengthSquared(Vector3 vector)
        => (vector.X * vector.X) + (vector.Z * vector.Z);

    private void ClearUptimeWalkSuppression()
    {
        this.suppressUptimeWalkUntil = DateTime.MinValue;
        this.suppressedUptimeTargetId = 0;
        this.lastUptimeWalkSuppressionReason = "not suppressed";
    }

    private static string MapCombatStyle(CombatStyle style)
    {
        return style switch
        {
            CombatStyle.Greed => "GreedAutomatic",
            CombatStyle.GreedGCD => "GreedGCDExplicit",
            CombatStyle.GreedLastMoment => "GreedLastMomentExplicit",
            _ => "Any"
        };
    }

    private static string MapForbiddenZoneCushion(float distance)
    {
        return distance switch
        {
            <= 0.25f => "None",
            < 1.0f => "Small",
            <= 2.25f => "Medium",
            _ => "Large"
        };
    }


    private void SetLeylines(bool useBetweenTheLines, bool useRetrace, bool returnToLeylines)
    {
        if (this.LastLeylinesBetweenTheLines != useBetweenTheLines &&
            bossMod.SetLeylinesBetweenTheLines(BossModIpc.DefaultPresetName, useBetweenTheLines))
        {
            this.LastLeylinesBetweenTheLines = useBetweenTheLines;
        }

        if (this.LastLeylinesRetrace != useRetrace &&
            bossMod.SetLeylinesRetrace(BossModIpc.DefaultPresetName, useRetrace))
        {
            this.LastLeylinesRetrace = useRetrace;
        }

        if (this.LastLeylinesGoal != returnToLeylines &&
            bossMod.SetLeylinesGoal(BossModIpc.DefaultPresetName, returnToLeylines))
        {
            this.LastLeylinesGoal = returnToLeylines;
        }
    }

    private void SetGapClosers(bool suppressAutomatedMovement)
    {
        if (suppressAutomatedMovement)
        {
            if (GapCloserDecisionPolicy.ShouldRunSafetyGapCloserDuringManualSuppression(
                    suppressAutomatedMovement,
                    config.UseGapCloser))
            {
                escapeGapCloserController.TryUseEscapeGapCloser();
            }

            return;
        }

        if (!config.UseGapCloser)
        {
            if (blackMageLeyLinesPositioningController.TryUseLeyLinesAction())
            {
                return;
            }

            if (this.ShouldHoldOptionalDashForMechanicPressure())
            {
                return;
            }

            if (redMageMeleeComboController.TryUseComboJump())
            {
                return;
            }

            return;
        }

        if (escapeGapCloserController.TryUseEscapeGapCloser())
        {
            return;
        }

        if (blackMageLeyLinesPositioningController.TryUseLeyLinesAction())
        {
            return;
        }

        if (this.ShouldHoldOptionalDashForMechanicPressure())
        {
            return;
        }

        if (pictomancerStarryMusePositioningController.TryUseStarryMuseSmudge())
        {
            return;
        }

        if (redMageMeleeComboController.TryUseComboJump())
        {
            return;
        }

        gapCloserController.TryUseReengageGapCloser();
    }

    private void SetMovementForPendingDashTurn(bool suppressAutomatedMovement)
    {
        if (!config.ManageMovement || suppressAutomatedMovement)
        {
            return;
        }

        if (facingController.ShouldPauseBossModMovementForDirectionalDash(DateTime.UtcNow, out _))
        {
            this.SetMovement(false);
        }
    }

    private bool ShouldHoldOptionalDashForMechanicPressure()
    {
        var pressure = mechanicPressure();
        return !pressure.KnockbackRecoveryActive &&
               GapCloserDecisionPolicy.ShouldBlockAllOptionalDashesForPressure(pressure, out _);
    }

    private void WriteNeutralStrategies(string presetName)
    {
        bossMod.SetMovement(presetName, false);
        bossMod.SetMovementRangeStrategy(presetName, "Any");
        bossMod.SetRange(presetName, Configuration.InternalDisabledUptimeRange);
        bossMod.SetForbiddenZoneCushion(presetName, "None");
        bossMod.SetPositional(presetName, Positional.Any);
        bossMod.SetLeylinesBetweenTheLines(presetName, false);
        bossMod.SetLeylinesRetrace(presetName, false);
        bossMod.SetLeylinesGoal(presetName, false);
    }
}
