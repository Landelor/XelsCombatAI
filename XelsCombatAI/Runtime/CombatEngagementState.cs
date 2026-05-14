using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.Runtime;

internal sealed record CombatEngagementState(
    bool LocalInCombat,
    bool PartyInCombat,
    bool NearbyHostileCombat,
    bool EffectiveInCombat,
    string Reason,
    string SuppressedReason);

internal static class CombatEngagementDetector
{
    private const float NearbyHostileCombatDistance = 45f;

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
        var nearbyHostileCombat = HasNearbyHostileCombat(services, player, partyIds);
        var effectiveInCombat = localInCombat || partyInCombat || nearbyHostileCombat;
        var reason = localInCombat
            ? "local"
            : partyInCombat
                ? "party"
                : nearbyHostileCombat
                    ? "nearby hostile"
                    : "none";

        var suppressedReason = services.Condition[ConditionFlag.Unconscious] || player.IsDead || player.CurrentHp == 0
            ? "player dead"
            : string.Empty;

        return new(localInCombat, partyInCombat, nearbyHostileCombat, effectiveInCombat, reason, suppressedReason);
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

    private static bool HasNearbyHostileCombat(DalamudServices services, IBattleChara player, IReadOnlySet<ulong> partyIds)
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

            if (VectorDistance2D(player.Position, npc.Position) <= NearbyHostileCombatDistance)
            {
                return true;
            }
        }

        return false;
    }

    private static float VectorDistance2D(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
