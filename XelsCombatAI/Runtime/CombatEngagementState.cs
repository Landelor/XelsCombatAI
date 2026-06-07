using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Runtime;

internal sealed record CombatEngagementState(
    bool LocalInCombat,
    bool PartyInCombat,
    bool PartyTargetedHostileCombat,
    bool EffectiveInCombat,
    string Reason,
    string SuppressedReason);

internal static class CombatEngagementDetector
{
    public static bool IsEffectivelyInCombat(DalamudServices services)
    {
        return Detect(services).EffectiveInCombat;
    }

    public static CombatEngagementState Detect(DalamudServices services)
    {
        var localInCombat = services.Condition[ConditionFlag.InCombat];
        if (services.ObjectTable.LocalPlayer is not { } player)
        {
            return new(localInCombat, false, false, false, localInCombat ? "local" : "none", "missing player");
        }

        var partyIds = BuildPartyIds(services, player, out var partyInCombat);
        var partyTargetedHostileCombat = HasPartyTargetedHostileCombat(services, partyIds);
        var effectiveInCombat = localInCombat || partyInCombat || partyTargetedHostileCombat;
        var reason = localInCombat
            ? "local"
            : partyInCombat
                ? "party"
                : partyTargetedHostileCombat
                    ? "party targeted"
                    : "none";

        var suppressedReason = services.Condition[ConditionFlag.Unconscious] || player.IsDead || player.CurrentHp == 0
            ? "player dead"
            : string.Empty;

        return new(localInCombat, partyInCombat, partyTargetedHostileCombat, effectiveInCombat, reason, suppressedReason);
    }

    private static HashSet<ulong> BuildPartyIds(DalamudServices services, IBattleChara player, out bool partyInCombat)
    {
        var ids = new HashSet<ulong> { player.GameObjectId };
        partyInCombat = false;

        foreach (var actor in PartyAllyProvider.GetVisiblePartyAllies(services, player).Members)
        {
            ids.Add(actor.GameObjectId);
            if (actor.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                partyInCombat = true;
            }
        }

        return ids;
    }

    private static bool HasPartyTargetedHostileCombat(DalamudServices services, IReadOnlySet<ulong> partyIds)
    {
        foreach (var npc in services.ObjectTable.OfType<IBattleNpc>())
        {
            if (npc.BattleNpcKind != BattleNpcSubKind.Combatant ||
                !npc.StatusFlags.HasFlag(StatusFlags.InCombat) ||
                !npc.StatusFlags.HasFlag(StatusFlags.Hostile) ||
                npc.IsDead ||
                npc.CurrentHp == 0)
            {
                continue;
            }

            if (partyIds.Contains(npc.TargetObjectId))
            {
                return true;
            }
        }

        return false;
    }
}
