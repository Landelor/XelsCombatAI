using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class EscapeGapCloserController(Configuration config, DalamudServices services, BossModReflectionSafety bossModSafety, GapCloserController gapCloserController)
{
    private DateTime nextEscapeGapCloserAttempt = DateTime.MinValue;
    private DateTime escapeDangerDetectedAt = DateTime.MinValue;
    private string lastEscapeGapCloserSafety = "not checked";

    public string LastEscapeGapCloserSafety => this.lastEscapeGapCloserSafety;

    public void Reset()
    {
        this.nextEscapeGapCloserAttempt = DateTime.MinValue;
        this.escapeDangerDetectedAt = DateTime.MinValue;
        this.lastEscapeGapCloserSafety = "not checked";
    }

    public unsafe bool TryUseEscapeGapCloser()
    {
        if (DateTime.UtcNow < this.nextEscapeGapCloserAttempt)
        {
            return false;
        }

        this.nextEscapeGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.lastEscapeGapCloserSafety = "missing player";
            return false;
        }

        if (player.IsCasting || ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastEscapeGapCloserSafety = "player busy";
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        if (config.CombatStyle == CombatStyle.Greed && classJobId == 25 && this.HasActiveCircleOfPower())
        {
            this.lastEscapeGapCloserSafety = "disabled in Greed mode while in Ley Lines";
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out var currentReason))
        {
            this.lastEscapeGapCloserSafety = currentReason;
            return false;
        }

        if (currentSafe)
        {
            this.escapeDangerDetectedAt = DateTime.MinValue;
            this.lastEscapeGapCloserSafety = "current position safe";
            return false;
        }

        var now = DateTime.UtcNow;
        if (this.escapeDangerDetectedAt == DateTime.MinValue)
        {
            this.escapeDangerDetectedAt = now;
        }

        if ((now - this.escapeDangerDetectedAt).TotalMilliseconds > CombatConstants.EscapeGapCloserDangerWindowMilliseconds)
        {
            this.lastEscapeGapCloserSafety = "already walking to safety";
            return false;
        }

        if (!bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeMovementDestination, out var intentReason))
        {
            this.lastEscapeGapCloserSafety = intentReason;
            return false;
        }

        if (Geometry.Distance2D(player.Position, safeMovementDestination) < CombatConstants.EscapeGapCloserMinimumSafetyDistance)
        {
            this.lastEscapeGapCloserSafety = "safe movement under 4y";
            return false;
        }

        return classJobId switch
        {
            2 or 20 when config.EscapeGapCloserMNK => this.TryUseFriendlyEscapeGapCloser(ActionUse.MonkThunderclapActionId, CombatConstants.GapCloserMaxRange, safeMovementDestination),
            25 when config.EscapeGapCloserBLM => this.TryUseFriendlyEscapeGapCloser(ActionUse.BlackMageAetherialManipulationActionId, 25f, safeMovementDestination),
            29 or 30 when config.EscapeGapCloserNIN => this.TryUseLocationEscapeGapCloser(ActionUse.NinjaShukuchiActionId, CombatConstants.GapCloserMaxRange, "Shukuchi", safeMovementDestination),
            36 when config.EscapeGapCloserBLU => this.TryUseLocationEscapeGapCloser(ActionUse.BlueMageLoomActionId, 15f, "Loom", safeMovementDestination),
            39 when config.EscapeGapCloserRPR => gapCloserController.TryUseReaperRegress(ref this.lastEscapeGapCloserSafety, safeMovementDestination: safeMovementDestination) || this.TryUseForwardEscapeGapCloser(ActionUse.ReaperHellsIngressActionId, safeMovementDestination),
            40 when config.EscapeGapCloserSGE => this.TryUseFriendlyEscapeGapCloser(ActionUse.SageIcarusActionId, 25f, safeMovementDestination),
            41 when config.EscapeGapCloserVPR => this.TryUseFriendlyEscapeGapCloser(ActionUse.ViperSlitherActionId, CombatConstants.GapCloserMaxRange, safeMovementDestination),
            38 when config.EscapeGapCloserDNC => this.TryUseForwardEscapeGapCloser(ActionUse.DancerEnAvantActionId, safeMovementDestination),
            42 when config.EscapeGapCloserPCT => this.TryUseForwardEscapeGapCloser(ActionUse.PictomancerSmudgeActionId, safeMovementDestination),
            _ => false
        };
    }

    public static bool TryValidateEscapeDestination(DalamudServices services, BossModReflectionSafety bossModSafety, Vector3 playerPosition, Vector3 destination, Vector3 safeMovementDestination, out string reason)
    {
        _ = services;
        if (!IsUsefulEscapeDestination(playerPosition, destination, safeMovementDestination, out reason))
        {
            return false;
        }

        if (!bossModSafety.TryIsPositionSafe(destination, out var destinationSafe, out var destinationReason))
        {
            reason = destinationReason;
            return false;
        }

        if (!destinationSafe)
        {
            reason = "escape destination dangerous";
            return false;
        }

        if (!bossModSafety.TryIsDashSafe(playerPosition, destination, out reason))
        {
            return false;
        }

        return true;
    }

    private unsafe bool TryUseFriendlyEscapeGapCloser(uint actionId, float maxRange, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        foreach (var ally in this.EnumerateFriendlyEscapeTargets(player, maxRange))
        {
            if (!TryValidateEscapeDestination(services, bossModSafety, player.Position, ally.Position, safeMovementDestination, out var reason))
            {
                this.lastEscapeGapCloserSafety = reason;
                continue;
            }

            var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, ally.GameObjectId);
            if (used)
            {
                this.lastEscapeGapCloserSafety = $"used {actionId} on ally";
                return true;
            }

            this.lastEscapeGapCloserSafety = $"failed to use {actionId} on ally";
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = "no safe ally found";
        }

        return false;
    }

    private unsafe bool TryUseLocationEscapeGapCloser(uint actionId, float maxRange, string actionName, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        foreach (var candidate in this.EnumerateEscapeLocationCandidates(player.Position, maxRange))
        {
            if (!TryValidateEscapeDestination(services, bossModSafety, player.Position, candidate, safeMovementDestination, out var reason))
            {
                this.lastEscapeGapCloserSafety = reason;
                continue;
            }

            var location = candidate;
            var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, actionId, player.GameObjectId, &location);
            this.lastEscapeGapCloserSafety = used ? $"used escape {actionName}" : $"failed to use escape {actionName}";
            return used;
        }

        if (string.IsNullOrEmpty(this.lastEscapeGapCloserSafety) || this.lastEscapeGapCloserSafety == "current position safe")
        {
            this.lastEscapeGapCloserSafety = $"no safe {actionName} escape destination";
        }

        return false;
    }

    private unsafe bool TryUseForwardEscapeGapCloser(uint actionId, Vector3 safeMovementDestination)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastEscapeGapCloserSafety = "action unavailable";
            return false;
        }

        var destination = player.Position + Geometry.RotationToDirection(player.Rotation) * CombatConstants.FixedForwardGapCloserRange;
        if (!TryValidateEscapeDestination(services, bossModSafety, player.Position, destination, safeMovementDestination, out var reason))
        {
            this.lastEscapeGapCloserSafety = reason;
            return false;
        }

        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        this.lastEscapeGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private static bool IsUsefulEscapeDestination(Vector3 playerPosition, Vector3 destination, Vector3 safeMovementDestination, out string reason)
    {
        if (Geometry.Distance2D(playerPosition, destination) < CombatConstants.EscapeGapCloserMinimumSafetyDistance)
        {
            reason = "escape destination under 4y";
            return false;
        }

        if (Geometry.Distance2D(destination, safeMovementDestination) >= Geometry.Distance2D(playerPosition, safeMovementDestination))
        {
            reason = "escape destination not toward safety";
            return false;
        }

        reason = "useful escape destination";
        return true;
    }

    private IEnumerable<IBattleChara> EnumerateFriendlyEscapeTargets(IBattleChara player, float maxRange)
    {
        return services.ObjectTable
            .OfType<IBattleChara>()
            .Where(ally =>
                ally.ObjectKind == ObjectKind.Pc &&
                ally.GameObjectId != player.GameObjectId &&
                ally.GameObjectId != 0 &&
                !ally.IsDead &&
                ally.CurrentHp > 0 &&
                Vector3.Distance(player.Position, ally.Position) <= maxRange)
            .OrderByDescending(ally => Vector3.Distance(player.Position, ally.Position));
    }

    private IEnumerable<Vector3> EnumerateEscapeLocationCandidates(Vector3 playerPosition, float maxRange)
    {
        foreach (var radius in CombatConstants.EscapeLocationRadii)
        {
            if (radius > maxRange)
            {
                continue;
            }

            for (var i = 0; i < 16; i++)
            {
                var angle = i * (MathF.Tau / 16f);
                yield return new Vector3(
                    playerPosition.X + MathF.Cos(angle) * radius,
                    playerPosition.Y,
                    playerPosition.Z + MathF.Sin(angle) * radius);
            }
        }
    }

    private bool HasActiveCircleOfPower()
    {
        return services.ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == ActionUse.CircleOfPowerStatusId && status.RemainingTime > 0) == true;
    }
}
