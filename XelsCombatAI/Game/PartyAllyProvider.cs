using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Game;

internal sealed record PartyAllySnapshot(
    IReadOnlyList<IBattleChara> Members,
    int PartyListMembers,
    int DutySupportMembers);

internal static class PartyAllyProvider
{
    public static IEnumerable<IBattleChara> EnumerateVisiblePartyAllies(DalamudServices services, IBattleChara player)
    {
        return GetVisiblePartyAllies(services, player).Members;
    }

    public static PartyAllySnapshot GetVisiblePartyAllies(DalamudServices services, IBattleChara player)
    {
        var seen = new HashSet<ulong> { player.GameObjectId };
        var members = new List<IBattleChara>();
        var partyListMembers = 0;
        var dutySupportMembers = 0;

        foreach (var member in services.PartyList)
        {
            if (member.GameObject is IBattleChara actor &&
                IsUsableAlly(actor) &&
                seen.Add(actor.GameObjectId))
            {
                members.Add(actor);
                partyListMembers++;
                if (IsDutySupportPartyMember(actor))
                {
                    dutySupportMembers++;
                }
            }
        }

        foreach (var actor in services.ObjectTable.OfType<IBattleChara>())
        {
            if (seen.Contains(actor.GameObjectId) ||
                !IsUsableAlly(actor) ||
                !IsDutySupportPartyMember(actor))
            {
                continue;
            }

            seen.Add(actor.GameObjectId);
            members.Add(actor);
            dutySupportMembers++;
        }

        return new PartyAllySnapshot(members, partyListMembers, dutySupportMembers);
    }

    public static IBattleChara? SelectBestTank(DalamudServices services, IBattleChara player)
    {
        IBattleChara? withStance = null;
        IBattleChara? fallback = null;
        var bestStanceDistSq = float.MaxValue;
        var bestMobCount = -1;
        var seen = new HashSet<ulong> { player.GameObjectId };

        foreach (var member in services.PartyList)
        {
            if (!JobRoles.IsTankJob(member.ClassJob.RowId) ||
                member.GameObject is not IBattleChara actor ||
                actor.IsDead || actor.CurrentHp == 0 ||
                !seen.Add(actor.GameObjectId))
                continue;
            ConsiderTankCandidate(services, actor, player, ref withStance, ref bestStanceDistSq, ref fallback, ref bestMobCount);
        }

        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (npc.BattleNpcKind != BattleNpcSubKind.NpcPartyMember ||
                !JobRoles.IsTankJob(npc.ClassJob.RowId) ||
                npc.IsDead || npc.CurrentHp == 0 ||
                !seen.Add(npc.GameObjectId))
                continue;
            ConsiderTankCandidate(services, npc, player, ref withStance, ref bestStanceDistSq, ref fallback, ref bestMobCount);
        }

        return withStance ?? fallback;
    }

    public static bool HasTankStance(IBattleChara actor) =>
        actor.StatusList.Any(s => s.StatusId is
            ActionUse.PaladinIronWillStatusId or
            ActionUse.WarriorDefianceStatusId or
            ActionUse.DarkKnightGritStatusId or
            ActionUse.GunbreakerRoyalGuardStatusId);

    public static int CountMobsTargeting(DalamudServices services, IBattleChara actor)
    {
        var count = 0;
        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (npc.BattleNpcKind == BattleNpcSubKind.Combatant &&
                npc.StatusFlags.HasFlag(StatusFlags.InCombat) &&
                npc.StatusFlags.HasFlag(StatusFlags.Hostile) &&
                !npc.IsDead && npc.CurrentHp > 0 &&
                npc.TargetObjectId == actor.GameObjectId)
                count++;
        }
        return count;
    }

    private static void ConsiderTankCandidate(DalamudServices services, IBattleChara actor, IBattleChara player,
        ref IBattleChara? withStance, ref float bestStanceDistSq, ref IBattleChara? fallback, ref int bestMobCount)
    {
        if (HasTankStance(actor))
        {
            var distSq = Vector3.DistanceSquared(actor.Position, player.Position);
            if (withStance == null || distSq < bestStanceDistSq)
            {
                withStance = actor;
                bestStanceDistSq = distSq;
            }
        }
        else
        {
            var mobCount = CountMobsTargeting(services, actor);
            if (fallback == null || mobCount > bestMobCount)
            {
                fallback = actor;
                bestMobCount = mobCount;
            }
        }
    }

    private static bool IsUsableAlly(IBattleChara actor)
    {
        return !actor.IsDead &&
               actor.CurrentHp > 0 &&
               actor.ObjectKind is ObjectKind.Pc or ObjectKind.BattleNpc;
    }

    private static bool IsDutySupportPartyMember(IBattleChara actor)
    {
        return actor.ObjectKind == ObjectKind.BattleNpc &&
               actor is IBattleNpc npc &&
               npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember;
    }
}
