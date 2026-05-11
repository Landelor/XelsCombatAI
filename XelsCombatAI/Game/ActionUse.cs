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
    public const uint DragoonElusiveJumpActionId = 94;
    public const uint DragoonWingedGlideActionId = 36951;
    public const uint NinjaShukuchiActionId = 2262;
    public const uint NinjaMudraStatusId = 496;
    public const uint NinjaTenChiJinStatusId = 1186;
    public const uint NinjaThreeMudraStatusId = 1317;
    public const uint SamuraiGyotenActionId = 7492;
    public const uint SamuraiYatenActionId = 7493;
    public const uint ReaperHellsIngressActionId = 24401;
    public const uint ReaperHellsEgressActionId = 24402;
    public const uint ReaperRegressActionId = 24403;
    public const uint ReaperHellsgatePortalDataId = 0x4C3u;
    public const uint ViperSlitherActionId = 34646;
    public const uint WhiteMageAetherialShiftActionId = 37008;
    public const uint BlackMageAetherialManipulationActionId = 155;
    public const uint RedMageCorpsACorpsActionId = 7506;
    public const uint RedMageDisplacementActionId = 7515;
    public const uint RedMageEnchantedRiposteActionId = 7527;
    public const uint RedMageEnchantedZwerchhauActionId = 7528;
    public const uint RedMageEnchantedRedoublementActionId = 7529;
    public const uint RedMageEnchantedMoulinetActionId = 7530;
    public const uint RedMageEnchantedMoulinetDeuxActionId = 37002;
    public const uint RedMageEnchantedMoulinetTroisActionId = 37003;
    public const uint RedMageManaficationRiposteActionId = 45960;
    public const uint RedMageManaficationZwerchhauActionId = 45961;
    public const uint RedMageManaficationRedoublementActionId = 45962;
    public const uint RedMageManaficationStatusId = 1971;
    public const uint RedMageMagickedSwordplayStatusId = 3875;
    public const uint RedMageDualcastStatusId = 1249;
    public const uint RedMageAlternateDualcastStatusId = 1393;
    public const uint RedMageAccelerationStatusId = 1238;
    public const uint RedMageGrandImpactReadyStatusId = 3877;
    public const uint SwiftcastStatusId = 167;
    public const uint SageIcarusActionId = 24295;
    public const uint PictomancerSmudgeActionId = 34684;
    public const uint DancerEnAvantActionId = 16010;
    public const uint BardRepellingShotActionId = 112;

    public static unsafe bool CanUseAction(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager->GetActionStatus(ActionType.Action, actionId) == 0 &&
               actionManager->GetCurrentCharges(actionId) > 0;
    }

    public static unsafe uint GetCurrentCharges(uint actionId)
    {
        return ActionManager.Instance()->GetCurrentCharges(actionId);
    }

    public static unsafe bool HasAnimationLock()
    {
        return ActionManager.Instance()->AnimationLock > 0f;
    }
}
