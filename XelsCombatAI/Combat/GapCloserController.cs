using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class GapCloserController(Configuration config, DalamudServices services, BossModIpc bossMod, BossModReflectionSafety bossModSafety)
{
    private DateTime nextGapCloserAttempt = DateTime.MinValue;
    private string lastGapCloserSafety = "not checked";
    private Vector3? lastSafeLandingPosition;

    public string LastGapCloserSafety => this.lastGapCloserSafety;
    public Vector3? LastSafeLandingPosition => this.lastSafeLandingPosition;

    public void Reset()
    {
        this.nextGapCloserAttempt = DateTime.MinValue;
        this.lastGapCloserSafety = "not checked";
        this.lastSafeLandingPosition = null;
    }

    public unsafe bool TryUseReengageGapCloser()
    {
        if (DateTime.UtcNow < this.nextGapCloserAttempt)
        {
            return false;
        }

        this.nextGapCloserAttempt = DateTime.UtcNow.AddMilliseconds(250);

        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            this.lastGapCloserSafety = "missing player or target";
            return false;
        }

        if (target is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
        {
            this.lastGapCloserSafety = "target is not attackable";
            return false;
        }

        if (player.IsCasting)
        {
            this.lastGapCloserSafety = "player casting";
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            this.lastGapCloserSafety = "animation lock";
            return false;
        }

        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);

        if (distanceToHitbox > CombatConstants.MeleeActionRange && services.PartyList.Count >= 4)
        {
            var targetIsBoss = target is IBattleNpc bossCheck && bossMod.HasModuleByDataId(bossCheck.BaseId);
            if (!targetIsBoss)
            {
                var bestTrash = this.FindTrashGapCloserTarget(player);
                if (bestTrash != null)
                {
                    target = bestTrash;
                    distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
                }
            }
        }

        if (distanceToHitbox <= CombatConstants.MeleeActionRange || distanceToHitbox > CombatConstants.GapCloserMaxRange)
        {
            this.lastGapCloserSafety = "target not in gap closer range";
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (distanceToHitbox < config.MinimumReengageGapCloserDistance)
        {
            this.lastGapCloserSafety = $"target under {config.MinimumReengageGapCloserDistance:0}y";
            return false;
        }

        var classJobId = player.ClassJob.RowId;
        return classJobId switch
        {
            1 or 19 when config.GapCloserPLD => this.TryUseTargetGapCloser(ActionUse.PaladinInterveneActionId, distanceToHitbox, target),
            3 or 21 when config.GapCloserWAR => this.TryUseTargetGapCloser(ActionUse.WarriorOnslaughtActionId, distanceToHitbox, target),
            32 when config.GapCloserDRK => this.TryUseTargetGapCloser(ActionUse.DarkKnightShadowstrideActionId, distanceToHitbox, target),
            37 when config.GapCloserGNB => this.TryUseTargetGapCloser(ActionUse.GunbreakerTrajectoryActionId, distanceToHitbox, target),
            2 or 20 when config.GapCloserMNK => this.TryUseTargetGapCloser(ActionUse.MonkThunderclapActionId, distanceToHitbox, target),
            4 or 22 when config.GapCloserDRG => this.TryUseTargetGapCloser(ActionUse.DragoonWingedGlideActionId, distanceToHitbox, target),
            29 or 30 when config.GapCloserNIN => this.TryUseNinjaShukuchi(target),
            34 when config.GapCloserSAM => this.TryUseTargetGapCloser(ActionUse.SamuraiGyotenActionId, distanceToHitbox, target),
            38 when config.GapCloserDNC => this.TryUseForwardGapCloser(ActionUse.DancerEnAvantActionId, distanceToHitbox),
            39 when config.GapCloserRPR => this.TryUseReaperRegress(ref this.lastGapCloserSafety, distanceToHitbox) || this.TryUseForwardGapCloser(ActionUse.ReaperHellsIngressActionId, distanceToHitbox),
            41 when config.GapCloserVPR => this.TryUseTargetGapCloser(ActionUse.ViperSlitherActionId, distanceToHitbox, target),
            _ => false
        };
    }

    public unsafe bool TryUseReaperRegress(ref string lastSafety, float distanceToHitboxRequired = 0f, Vector3? safeMovementDestination = null)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.ReaperRegressActionId))
        {
            lastSafety = "Regress unavailable";
            return false;
        }

        var portal = this.TryFindReaperPortal(player);
        if (portal == null)
        {
            lastSafety = "Regress: no portal found";
            return false;
        }

        var portalPosition = portal.Position;

        if (safeMovementDestination.HasValue &&
            !EscapeGapCloserController.TryValidateEscapeDestination(services, bossModSafety, player.Position, portalPosition, safeMovementDestination.Value, config.MinimumEscapeGapCloserDistance, out var usefulReason))
        {
            lastSafety = $"Regress: {usefulReason}";
            return false;
        }

        if (distanceToHitboxRequired > 0f)
        {
            var target = services.TargetManager.Target;
            if (target == null)
            {
                lastSafety = "Regress: no target";
                return false;
            }

            var regressDistanceToHitbox = Geometry.DistanceToHitbox(portalPosition, player.HitboxRadius, target.Position, target.HitboxRadius);
            if (regressDistanceToHitbox >= distanceToHitboxRequired || regressDistanceToHitbox > CombatConstants.MeleeActionRange + 1f)
            {
                lastSafety = "Regress: portal would not re-engage";
                return false;
            }
        }

        if (!safeMovementDestination.HasValue &&
            !bossModSafety.TryIsDashSafe(player.Position, portalPosition, out var reason))
        {
            lastSafety = reason;
            return false;
        }

        var location = portalPosition;
        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.ReaperRegressActionId, player.GameObjectId, &location);
        lastSafety = used ? "used Regress" : "failed to use Regress";
        return used;
    }

    private unsafe bool TryUseTargetGapCloser(uint actionId, float distanceToHitbox, IGameObject target)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!Geometry.TryCalculateTargetDashDestination(player.Position, target.Position, distanceToHitbox, out var destination))
        {
            this.lastGapCloserSafety = "could not calculate dash destination";
            this.lastSafeLandingPosition = null;
            return false;
        }

        if (!bossModSafety.TryIsDashSafe(player.Position, destination, out var reason))
        {
            this.lastGapCloserSafety = reason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, target.GameObjectId);
        this.lastGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private unsafe bool TryUseForwardGapCloser(uint actionId, float distanceToHitbox)
    {
        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(actionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        var forward = Geometry.RotationToDirection(player.Rotation);
        var toTarget = target.Position - player.Position;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() <= 0.0001f)
        {
            this.lastGapCloserSafety = "could not calculate target direction";
            return false;
        }

        var targetDirection = Vector3.Normalize(toTarget);
        if (Vector3.Dot(forward, targetDirection) < 0.85f)
        {
            this.lastGapCloserSafety = "target not in front for fixed dash";
            return false;
        }

        var destination = player.Position + forward * CombatConstants.FixedForwardGapCloserRange;
        var destinationDistanceToHitbox = Geometry.DistanceToHitbox(destination, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (destinationDistanceToHitbox >= distanceToHitbox || destinationDistanceToHitbox > CombatConstants.MeleeActionRange + 1f)
        {
            this.lastGapCloserSafety = "fixed dash would not re-engage";
            return false;
        }

        if (!bossModSafety.TryIsDashSafe(player.Position, destination, out var reason))
        {
            this.lastGapCloserSafety = reason;
            this.lastSafeLandingPosition = null;
            return false;
        }

        this.lastSafeLandingPosition = destination;
        var used = ActionManager.Instance()->UseAction(ActionType.Action, actionId, player.GameObjectId);
        this.lastGapCloserSafety = used ? $"used {actionId}" : $"failed to use {actionId}";
        return used;
    }

    private unsafe bool TryUseNinjaShukuchi(IGameObject target)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        if (!ActionUse.CanUseAction(ActionUse.NinjaShukuchiActionId))
        {
            this.lastGapCloserSafety = "action unavailable";
            return false;
        }

        if (!this.TryFindSafeShukuchiDestination(player.Position, target.Position, target.HitboxRadius, out var destination))
        {
            return false;
        }

        var location = destination;
        var used = ActionManager.Instance()->UseActionLocation(ActionType.Action, ActionUse.NinjaShukuchiActionId, player.GameObjectId, &location);
        this.lastGapCloserSafety = used ? "used Shukuchi" : "failed to use Shukuchi";
        return used;
    }

    private bool TryFindSafeShukuchiDestination(Vector3 playerPosition, Vector3 targetPosition, float targetHitboxRadius, out Vector3 destination)
    {
        foreach (var candidate in this.EnumerateShukuchiCandidates(playerPosition, targetPosition, targetHitboxRadius))
        {
            if (Vector3.Distance(playerPosition, candidate) > CombatConstants.GapCloserMaxRange)
            {
                continue;
            }

            if (bossModSafety.TryIsDashSafe(playerPosition, candidate, out var reason))
            {
                destination = candidate;
                this.lastGapCloserSafety = "safe Shukuchi destination found";
                return true;
            }

            this.lastGapCloserSafety = reason;
        }

        destination = default;
        if (string.IsNullOrEmpty(this.lastGapCloserSafety) || this.lastGapCloserSafety == "safe Shukuchi destination found")
        {
            this.lastGapCloserSafety = "no safe Shukuchi destination";
        }

        return false;
    }

    private IEnumerable<Vector3> EnumerateShukuchiCandidates(Vector3 playerPosition, Vector3 targetPosition, float targetHitboxRadius)
    {
        var radius = targetHitboxRadius + CombatConstants.GapCloserDestinationMeleeRange;
        var toTarget = targetPosition - playerPosition;
        toTarget.Y = 0;
        if (toTarget.LengthSquared() > 0.0001f)
        {
            var direction = Vector3.Normalize(toTarget);
            yield return new Vector3(targetPosition.X - (direction.X * radius), playerPosition.Y, targetPosition.Z - (direction.Z * radius));
        }

        for (var i = 0; i < 16; i++)
        {
            var angle = i * (MathF.Tau / 16f);
            yield return new Vector3(
                targetPosition.X + MathF.Cos(angle) * radius,
                playerPosition.Y,
                targetPosition.Z + MathF.Sin(angle) * radius);
        }
    }

    private bool TryFindTankPosition(out Vector3 tankPosition)
    {
        foreach (var member in services.PartyList)
        {
            if (JobRoles.IsTankJob(member.ClassJob.RowId) && member.GameObject is { } go && !go.IsDead)
            {
                tankPosition = go.Position;
                return true;
            }
        }

        tankPosition = default;
        return false;
    }

    private IBattleNpc? FindTrashGapCloserTarget(IBattleChara player)
    {
        var hasTank = this.TryFindTankPosition(out var tankPos);

        IBattleNpc? best = null;
        var bestScore = float.MaxValue;

        foreach (var obj in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (obj.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (!obj.StatusFlags.HasFlag(StatusFlags.InCombat)) continue;
            var d = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, obj.Position, obj.HitboxRadius);
            if (d <= CombatConstants.MeleeActionRange || d > CombatConstants.GapCloserMaxRange) continue;

            var score = hasTank ? Vector3.Distance(obj.Position, tankPos) : d;
            if (score < bestScore) { best = obj; bestScore = score; }
        }

        return best;
    }

    private IGameObject? TryFindReaperPortal(IGameObject player)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == ActionUse.ReaperHellsgatePortalDataId && obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }
}
