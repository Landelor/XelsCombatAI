using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Game;

internal enum RangeRole
{
    Melee,
    PhysicalRanged,
    Healer,
    MagicRanged
}

internal static class JobRoles
{
    public static bool IsTankJob(uint classJobId)
        => classJobId is 1 or 19 or 3 or 21 or 32 or 37;

    public static bool IsMeleeAoEHealer(uint classJobId)
        => classJobId is 6 or 24 or 28 or 40;

    public static RangeRole GetRangeRole(IBattleChara? player)
        => GetRangeRole(player?.ClassJob.RowId ?? 0);

    public static RangeRole GetRangeRole(uint classJobId)
    {
        return classJobId is
            5 or 23 or
            31 or
            38
                ? RangeRole.PhysicalRanged
                : classJobId is
                    6 or 24 or
                    28 or
                    33 or
                    40
                        ? RangeRole.Healer
                        : classJobId is
                            7 or 25 or
                            26 or 27 or
                            35 or
                            36 or
                            42
                                ? RangeRole.MagicRanged
                                : RangeRole.Melee;
    }
}
