using System;
using System.Linq;
using System.Numerics;
using ECommons.EzSharedDataManager;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Combat;

internal sealed class PositionalsController(
    Configuration config,
    DalamudServices services,
    RotationSolverIpc rotationSolver,
    Action<Positional> setPositional,
    Action updateDtr)
{
    private bool? trueNorthStrategy;

    public bool? RsrTrueNorthDisabled { get; private set; }

    public void Reset()
    {
        this.RsrTrueNorthDisabled = null;
        this.trueNorthStrategy = null;
    }

    public void Apply()
    {
        if (!config.ManagePositionals)
        {
            return;
        }

        if (config.ManageTrueNorth)
        {
            this.EnsureRsrTrueNorthDisabled();
            var positional = ReadAvaricePositional();
            if (positional == Positional.Any)
            {
                this.trueNorthStrategy = null;
                setPositional(Positional.Any);
            }
            else
            {
                if (this.trueNorthStrategy == null)
                    this.trueNorthStrategy = this.HasActiveTrueNorth() || this.GetTrueNorthCharges() > 0;
                if (this.IsCurrentPositionalCorrect(positional))
                {
                    setPositional(positional);
                }
                else if (this.trueNorthStrategy == true)
                {
                    this.TryUseTrueNorth(positional);
                    var pending = !this.HasActiveTrueNorth() && !this.IsOutsideMeleeRange();
                    setPositional(Positional.Any);
                    if (pending && this.IsOutsideMeleeRange())
                    {
                        return;
                    }
                }
                else
                {
                    setPositional(positional);
                }
            }
        }
        else
        {
            setPositional(this.HasTrueNorthCoverage() ? Positional.Any : ReadAvaricePositional());
        }
    }

    public void EnsureRsrTrueNorthDisabled()
    {
        if (this.RsrTrueNorthDisabled != null)
        {
            return;
        }

        try
        {
            if (!rotationSolver.DisableAutoTrueNorth())
            {
                this.RsrTrueNorthDisabled = false;
                updateDtr();
                return;
            }

            this.RsrTrueNorthDisabled = true;
            services.Log.Verbose("Disabled Rotation Solver Reborn Auto True North.");
        }
        catch (Exception ex)
        {
            this.RsrTrueNorthDisabled = false;
            services.Log.Verbose(ex, "Could not disable Rotation Solver Reborn Auto True North.");
            if (config.EchoStatusToChat)
            {
                services.ChatGui.Print("[Xel's Combat AI] Warning: Manage True North is enabled, but Rotation Solver Reborn Auto True North could not be disabled.");
            }
            updateDtr();
        }
    }

    public uint GetTrueNorthCharges()
    {
        try
        {
            return this.GetTrueNorthChargesUnsafe();
        }
        catch (Exception ex)
        {
            services.Log.Verbose(ex, "Could not read True North charges.");
            return 0;
        }
    }

    public bool HasActiveTrueNorth()
    {
        return services.ObjectTable.LocalPlayer?.StatusList.Any(status => status.StatusId == ActionUse.TrueNorthStatusId && status.RemainingTime > 0) == true;
    }

    private unsafe bool TryUseTrueNorth(Positional positional)
    {
        if (positional == Positional.Any)
        {
            return false;
        }

        if (JobRoles.GetRangeRole(services.ObjectTable.LocalPlayer) != RangeRole.Melee)
        {
            return false;
        }

        if (this.IsCurrentPositionalCorrect(positional))
        {
            return false;
        }

        if (this.HasActiveTrueNorth())
        {
            return false;
        }

        if (this.IsNinjaMudraWindow())
        {
            return false;
        }

        if (this.GetTrueNorthCharges() == 0)
        {
            return false;
        }

        if (this.IsOutsideMeleeRange())
        {
            return false;
        }

        if (rotationSolver.IsNoCasting(services.Log))
        {
            return false;
        }

        if (ActionManager.Instance()->AnimationLock > 0)
        {
            return false;
        }

        if (services.ObjectTable.LocalPlayer?.IsCasting == true)
        {
            return false;
        }

        if (ActionManager.Instance()->GetActionStatus(ActionType.Action, ActionUse.TrueNorthActionId) != 0)
        {
            return false;
        }

        ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.TrueNorthActionId);
        return true;
    }

    private static Positional ReadAvaricePositional()
    {
        if (!EzSharedData.TryGet<uint[]>(CombatConstants.AvaricePositionalStatusKey, out var status) || status.Length < 2)
        {
            return Positional.Any;
        }

        return status[1] switch
        {
            1 => Positional.Rear,
            2 => Positional.Flank,
            _ => Positional.Any
        };
    }

    private bool IsOutsideMeleeRange()
    {
        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        return Vector3.Distance(player.Position, target.Position) - player.HitboxRadius - target.HitboxRadius > CombatConstants.MeleeActionRange;
    }

    private bool IsCurrentPositionalCorrect(Positional positional)
    {
        if (positional == Positional.Any)
        {
            return true;
        }

        var player = services.ObjectTable.LocalPlayer;
        var target = services.TargetManager.Target;
        if (player == null || target == null)
        {
            return false;
        }

        var toPlayer = player.Position - target.Position;
        toPlayer.Y = 0;
        if (toPlayer.LengthSquared() <= 0.0001f)
        {
            return false;
        }

        var frontDot = Vector3.Dot(Geometry.RotationToDirection(target.Rotation), Vector3.Normalize(toPlayer));
        return positional switch
        {
            Positional.Flank => Math.Abs(frontDot) < CombatConstants.PositionalDotThreshold,
            Positional.Rear => frontDot < -CombatConstants.PositionalDotThreshold,
            Positional.Front => frontDot > CombatConstants.PositionalDotThreshold,
            _ => true
        };
    }

    private bool HasTrueNorthCoverage()
    {
        return this.HasActiveTrueNorth() || this.GetTrueNorthCharges() > 0;
    }

    private bool IsNinjaMudraWindow()
    {
        var player = services.ObjectTable.LocalPlayer;
        return player?.ClassJob.RowId is 29 or 30 &&
               player.StatusList.Any(status =>
                   status.RemainingTime > 0f &&
                   status.StatusId is ActionUse.NinjaMudraStatusId or ActionUse.NinjaTenChiJinStatusId or ActionUse.NinjaThreeMudraStatusId);
    }

    private unsafe uint GetTrueNorthChargesUnsafe()
    {
        return ActionManager.Instance()->GetCurrentCharges(ActionUse.TrueNorthActionId);
    }
}
