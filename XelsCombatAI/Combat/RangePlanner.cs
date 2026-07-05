using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using XelsCombatAI.Game;

namespace XelsCombatAI.Combat;

internal sealed class TargetUptimePlanner(
    DalamudServices services,
    BossModIpc bossMod,
    JobRangeProvider jobRangeProvider,
    RotationSolverActionReflection rotationSolverActions)
{
    private const float DancerDanceUptimeRange = 15f;

    public Func<float?> TargetUptimeRangeOverride { get; set; } = () => null;
    public string LastTargetUptimeRangeSource { get; private set; } = "none";
    public string LastTargetUptimeRangeReason { get; private set; } = "not checked";

    public float CalculateTargetUptimeRange()
    {
        var overrideRange = this.TargetUptimeRangeOverride();
        if (overrideRange.HasValue)
        {
            this.LastTargetUptimeRangeSource = "local override";
            this.LastTargetUptimeRangeReason = $"controller override range {overrideRange.Value:0.0}y";
            return overrideRange.Value;
        }

        if (!this.HasUsableHostileTarget())
        {
            this.LastTargetUptimeRangeSource = "local";
            this.LastTargetUptimeRangeReason = "no usable hostile target";
            return Configuration.InternalDisabledUptimeRange;
        }

        var player = services.ObjectTable.LocalPlayer;
        var classJobId = player?.ClassJob.RowId ?? 0;
        var rangeRole = JobRoles.GetRangeRole(classJobId);
        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var action, out _) &&
            (!action.IsFriendly || IsDancerDanceRangeAction(classJobId, action.ActionId, action.AdjustedActionId)))
        {
            if (player != null &&
                services.TargetManager.Target is IBattleChara target &&
                IsMeleeRangedCastAction(classJobId, action.ActionId, action.AdjustedActionId))
            {
                var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
                var timing = new RsrGcdActionTimingSnapshot(
                    action.ActionId,
                    action.AdjustedActionId,
                    action.ActionName,
                    action.Source,
                    action.PrimaryTargetId,
                    action.GcdRemaining,
                    action.GcdElapsed,
                    action.GcdTotal,
                    action.GcdActionAhead);
                if (ShouldUseMeleeRangedCastFallback(distanceToHitbox, jobRangeProvider.EngagementRange, timing, out var fallbackReason))
                {
                    var fallbackRange = ResolveActionRange(action.Range, jobRangeProvider.EngagementRange);
                    this.LastTargetUptimeRangeSource = action.Source;
                    this.LastTargetUptimeRangeReason = $"next GCD {action.ActionName} action range {action.Range:0.0}y -> ranged fallback {fallbackRange:0.0}y; {fallbackReason}";
                    return fallbackRange;
                }
            }

            var range = ResolveTargetUptimeRange(rangeRole, jobRangeProvider.EngagementRange, action.Range, classJobId, action.ActionId, action.AdjustedActionId);
            this.LastTargetUptimeRangeSource = action.Source;
            this.LastTargetUptimeRangeReason = $"next GCD {action.ActionName} action range {action.Range:0.0}y -> uptime range {range:0.0}y";
            return range;
        }

        this.LastTargetUptimeRangeSource = "local";
        this.LastTargetUptimeRangeReason = $"job engagement range {jobRangeProvider.EngagementRange:0.0}y";
        return jobRangeProvider.EngagementRange;
    }

    internal static float ResolveTargetUptimeRange(RangeRole rangeRole, float jobEngagementRange, float actionRange, uint classJobId = 0, uint actionId = 0, uint adjustedActionId = 0)
    {
        if (IsDancerDanceRangeAction(classJobId, actionId, adjustedActionId))
        {
            return DancerDanceUptimeRange;
        }

        var usableActionRange = ResolveActionRange(actionRange, jobEngagementRange);

        return rangeRole == RangeRole.Melee
            ? MathF.Min(jobEngagementRange, usableActionRange)
            : usableActionRange;
    }

    internal static bool ShouldUseMeleeRangedCastFallback(float distanceToHitbox, float engagementRange, RsrGcdActionTimingSnapshot action, out string reason)
        => !GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(distanceToHitbox, engagementRange, action, out reason);

    private static float ResolveActionRange(float actionRange, float jobEngagementRange)
    {
        return actionRange > 0f
            ? Math.Clamp(actionRange, CombatConstants.MeleeActionRange, Configuration.InternalDisabledUptimeRange)
            : jobEngagementRange;
    }

    private static bool IsMeleeRangedCastAction(uint classJobId, uint actionId, uint adjustedActionId)
    {
        if (classJobId != 39)
        {
            return false;
        }

        return actionId == ActionUse.ReaperHarpeActionId ||
               adjustedActionId == ActionUse.ReaperHarpeActionId;
    }

    private static bool IsDancerDanceRangeAction(uint classJobId, uint actionId, uint adjustedActionId)
    {
        if (classJobId != 38)
        {
            return false;
        }

        return IsDancerDanceRangeAction(actionId) ||
               IsDancerDanceRangeAction(adjustedActionId);
    }

    private static bool IsDancerDanceRangeAction(uint actionId)
    {
        return actionId is
            15997 or // Standard Step
            15998 or // Technical Step
            15999 or // Emboite
            16000 or // Entrechat
            16001 or // Jete
            16002 or // Pirouette
            16003 or // Standard Finish
            16004 or // Technical Finish
            16191 or // Single Standard Finish
            16192 or // Double Standard Finish
            16193 or // Single Technical Finish
            16194 or // Double Technical Finish
            16195 or // Triple Technical Finish
            16196 or // Quadruple Technical Finish
            25790 or // Tillana
            33215 or // Single Technical Finish II
            33216 or // Double Technical Finish II
            33217 or // Triple Technical Finish II
            33218 or // Quadruple Technical Finish II
            36984;   // Finishing Move
    }

    public bool CurrentTargetHasBossModule()
    {
        var dataId = services.TargetManager.Target?.BaseId ?? 0;
        if (dataId == 0)
        {
            return false;
        }

        try
        {
            return bossMod.HasModuleByDataId(dataId);
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not query BossMod module state yet.");
            return false;
        }
    }

    private bool HasUsableHostileTarget()
    {
        return services.TargetManager.Target is IBattleNpc target &&
               target.BattleNpcKind == BattleNpcSubKind.Combatant &&
               target.GameObjectId != 0 &&
               target.StatusFlags.HasFlag(StatusFlags.Hostile) &&
               !target.IsDead &&
               target.CurrentHp > 0;
    }
}
