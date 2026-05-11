using Dalamud.Game.ClientState.Objects.Types;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XelsCombatAI.Game;

internal sealed unsafe class LocalPlayerFacingActuator
{
    public bool TrySetRotation(IGameObject player, float rotation, out string reason)
    {
        if (player.Address == nint.Zero)
        {
            reason = "player address unavailable";
            return false;
        }

        var gameObject = (GameObjectStruct*)player.Address;
        if (gameObject == null)
        {
            reason = "player object unavailable";
            return false;
        }

        gameObject->SetRotation(Geometry.NormalizeRadians(rotation));
        reason = "rotation set";
        return true;
    }
}
