using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Game;

internal static class ActionUse
{
    public const uint TrueNorthActionId = 7546;
    public const uint TrueNorthStatusId = 1250;
    public const uint CircleOfPowerStatusId = 738;
    public const uint PassageOfArmsActionId = 7385;
    public const uint PassageOfArmsStatusId = 1175;
    public const uint PaladinIronWillStatusId = 79;
    public const uint WarriorDefianceStatusId = 91;
    public const uint DarkKnightGritStatusId = 743;
    public const uint GunbreakerRoyalGuardStatusId = 1833;
    public const uint PaladinInterveneActionId = 16461;
    public const uint WarriorOnslaughtActionId = 7386;
    public const uint DarkKnightShadowstrideActionId = 36926;
    public const uint GunbreakerTrajectoryActionId = 36934;
    public const uint MonkThunderclapActionId = 25762;
    public const uint DragoonWingedGlideActionId = 36951;
    public const uint NinjaShukuchiActionId = 2262;
    public const uint SamuraiGyotenActionId = 7492;
    public const uint ReaperHellsIngressActionId = 24401;
    public const uint ReaperRegressActionId = 24403;
    public const uint ReaperHellsgatePortalDataId = 0x4C3u;
    public const uint ViperSlitherActionId = 34646;
    public const uint WhiteMageAetherialShiftActionId = 37008;
    public const uint BlackMageAetherialManipulationActionId = 155;
    public const uint SageIcarusActionId = 24295;
    public const uint PictomancerSmudgeActionId = 34684;
    public const uint DancerEnAvantActionId = 16010;

    public static unsafe bool CanUseAction(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager->GetActionStatus(ActionType.Action, actionId) == 0 &&
               actionManager->GetCurrentCharges(actionId) > 0;
    }
}
