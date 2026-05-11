using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Game;

internal sealed class CombatLineOfSightChecker(IPluginLog log)
{
    private const float EyeHeight = 2f;
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(5);
    private DateTime nextFailureLogUtc = DateTime.MinValue;

    public unsafe bool TryHasLineOfSight(Vector3 from, Vector3 to, out bool clear, out string reason)
    {
        clear = true;
        reason = "clear";

        try
        {
            var fromEye = new Vector3(from.X, from.Y + EyeHeight, from.Z);
            var toEye = new Vector3(to.X, to.Y + EyeHeight, to.Z);
            var offset = toEye - fromEye;
            var maxDistance = offset.Length();
            if (maxDistance < 0.01f)
            {
                reason = "same point";
                return true;
            }

            var direction = offset / maxDistance;
            clear = !BGCollisionModule.RaycastMaterialFilter(fromEye, direction, out _, maxDistance);
            reason = clear ? "game collision clear" : "game collision blocked";
            return true;
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if (now >= this.nextFailureLogUtc)
            {
                this.nextFailureLogUtc = now.Add(FailureLogInterval);
                log.Verbose(ex, "Could not query game collision line of sight.");
            }

            clear = true;
            reason = "game collision query failed";
            return false;
        }
    }
}
