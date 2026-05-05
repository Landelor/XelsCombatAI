using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModReflectionSafety
{
    private const string BossModPluginTypeName = "BossMod.Plugin";
    private const string BossModWPosTypeName = "BossMod.WPos";
    private const string BossModHintsTypeName = "BossMod.AIHints";
    private const string BossModActionDefinitionsTypeName = "BossMod.ActionDefinitions";
    private const double DashLockSeconds = 0.8d;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private object? bossModPlugin;
    private FieldInfo? hintsField;
    private FieldInfo? imminentSpecialModeField;
    private FieldInfo? forcedMovementField;
    private ConstructorInfo? wposConstructor;
    private MethodInfo? isDashDangerousMethod;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private string status = "unresolved";

    public BossModReflectionSafety(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public string Status => this.status;

    public void Reset()
    {
        this.bossModPlugin = null;
        this.hintsField = null;
        this.imminentSpecialModeField = null;
        this.forcedMovementField = null;
        this.wposConstructor = null;
        this.isDashDangerousMethod = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.status = "unresolved";
    }

    public bool TryIsDashSafe(Vector3 from, Vector3 to, out string reason)
    {
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                this.ResetWithStatus("BMR hints unavailable");
                reason = this.status;
                return false;
            }

            if (this.HasImminentDashBlockingMode(hints))
            {
                reason = "BMR imminent movement lock";
                return false;
            }

            var fromWPos = this.CreateWPos(from);
            var toWPos = this.CreateWPos(to);
            var dangerous = (bool)(this.isDashDangerousMethod!.Invoke(null, [fromWPos, toWPos, hints]) ?? true);
            if (dangerous)
            {
                reason = "BMR reports dash destination dangerous";
                return false;
            }

            reason = "safe";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not query reflected BossMod dash safety.");
            this.ResetWithStatus("BMR reflection query failed");
            reason = this.status;
            return false;
        }
    }

    public bool TryIsPositionSafe(Vector3 position, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                this.ResetWithStatus("BMR hints unavailable");
                reason = this.status;
                return false;
            }

            var wpos = this.CreateWPos(position);
            safe = !(bool)(this.isDashDangerousMethod!.Invoke(null, [wpos, wpos, hints]) ?? true);
            reason = safe ? "safe" : "BMR reports current position dangerous";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not query reflected BossMod position safety.");
            this.ResetWithStatus("BMR reflection query failed");
            reason = this.status;
            return false;
        }
    }

    public bool TryGetSafeMovementIntent(Vector3 playerPosition, out Vector3 destination, out string reason)
    {
        destination = default;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        if (this.forcedMovementField == null)
        {
            reason = "BMR ForcedMovement field not found";
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                reason = "BMR hints unavailable";
                return false;
            }

            var rawValue = this.forcedMovementField.GetValue(hints);
            if (rawValue is not Vector3 movement)
            {
                reason = "BMR ForcedMovement not set";
                return false;
            }

            var xz = new Vector2(movement.X, movement.Z);
            if (xz.Length() < 0.5f)
            {
                reason = "BMR movement vector too small";
                return false;
            }

            var inferredDestination = playerPosition + new Vector3(xz.X, 0, xz.Y);
            if (!this.TryIsPositionSafe(inferredDestination, out var destSafe, out var destReason))
            {
                reason = destReason;
                return false;
            }

            if (!destSafe)
            {
                reason = "BMR inferred destination not safe";
                return false;
            }

            destination = inferredDestination;
            reason = "BMR movement intent confirmed safe";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose($"Could not query BMR movement intent: {ex.Message}");
            reason = "BMR movement intent query failed";
            return false;
        }
    }

    private bool EnsureResolved()
    {
        if (this.bossModPlugin != null &&
            this.hintsField != null &&
            this.wposConstructor != null &&
            this.isDashDangerousMethod != null)
        {
            return true;
        }

        if (DateTime.UtcNow < this.nextResolveAttempt)
        {
            return false;
        }

        this.nextResolveAttempt = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var plugin = this.FindBossModPlugin();
            if (plugin == null)
            {
                this.ResetWithStatus("BMR plugin instance not found");
                return false;
            }

            var assembly = plugin.GetType().Assembly;
            var hintsType = assembly.GetType(BossModHintsTypeName);
            var wposType = assembly.GetType(BossModWPosTypeName);
            var actionDefinitionsType = assembly.GetType(BossModActionDefinitionsTypeName);
            if (hintsType == null || wposType == null || actionDefinitionsType == null)
            {
                this.ResetWithStatus("BMR safety types not found");
                return false;
            }

            var hints = plugin.GetType().GetField("_hints", BindingFlags.Instance | BindingFlags.NonPublic);
            var wposCtor = wposType.GetConstructor([typeof(float), typeof(float)]);
            var isDashDangerous = actionDefinitionsType.GetMethod(
                "IsDashDangerous",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [wposType, wposType, hintsType],
                null);

            if (hints == null || wposCtor == null || isDashDangerous == null)
            {
                this.ResetWithStatus("BMR safety members not found");
                return false;
            }

            this.bossModPlugin = plugin;
            this.hintsField = hints;
            this.imminentSpecialModeField = hintsType.GetField("ImminentSpecialMode", BindingFlags.Instance | BindingFlags.Public);
            this.forcedMovementField = hintsType.GetField("ForcedMovement", BindingFlags.Instance | BindingFlags.Public);
            this.wposConstructor = wposCtor;
            this.isDashDangerousMethod = isDashDangerous;
            this.status = "available";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Could not resolve reflected BossMod safety integration.");
            this.ResetWithStatus("BMR reflection resolve failed");
            return false;
        }
    }

    private object? FindBossModPlugin()
    {
        if (this.pluginInterface.InstalledPlugins != null)
        {
            foreach (var plugin in this.pluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded)
                {
                    continue;
                }

                if (!string.Equals(plugin.InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.InternalName, "BossMod", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.Name, "BossMod Reborn", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(plugin.Name, "BossMod", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var found = FindObject(plugin, BossModPluginTypeName, maxDepth: 8);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return FindObject(this.pluginInterface, BossModPluginTypeName, maxDepth: 8);
    }

    private object CreateWPos(Vector3 position)
    {
        return this.wposConstructor!.Invoke([position.X, position.Z]);
    }

    private bool HasImminentDashBlockingMode(object hints)
    {
        if (this.imminentSpecialModeField == null)
        {
            return false;
        }

        var value = this.imminentSpecialModeField.GetValue(hints);
        if (value == null)
        {
            return false;
        }

        var type = value.GetType();
        var mode = GetTupleField(value, type, "mode", "Item1");
        var activation = GetTupleField(value, type, "activation", "Item2");
        if (mode == null || activation is not DateTime deadline)
        {
            return false;
        }

        var modeName = mode.ToString();
        return (string.Equals(modeName, "Pyretic", StringComparison.Ordinal) ||
                string.Equals(modeName, "NoMovement", StringComparison.Ordinal)) &&
               deadline <= DateTime.Now.AddSeconds(DashLockSeconds);
    }

    private void ResetWithStatus(string newStatus)
    {
        var oldStatus = this.status;
        this.bossModPlugin = null;
        this.hintsField = null;
        this.imminentSpecialModeField = null;
        this.forcedMovementField = null;
        this.wposConstructor = null;
        this.isDashDangerousMethod = null;
        this.status = newStatus;
        if (!string.Equals(oldStatus, newStatus, StringComparison.Ordinal))
        {
            this.log.Error($"BossMod reflected gap closer safety unavailable: {newStatus}");
        }
    }

    private static object? GetTupleField(object value, Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                return field.GetValue(value);
            }
        }

        return null;
    }

    private static object? FindObject(object root, string typeFullName, int maxDepth)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!visited.Add(value))
            {
                continue;
            }

            var type = value.GetType();
            if (type.FullName == typeFullName)
            {
                return value;
            }

            if (depth >= maxDepth || ShouldSkip(type))
            {
                continue;
            }

            foreach (var child in EnumerateChildren(value, type))
            {
                if (child != null)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateChildren(object value, Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(Flags))
        {
            if (field.IsStatic || ShouldSkip(field.FieldType))
            {
                continue;
            }

            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        foreach (var property in type.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead || ShouldSkip(property.PropertyType))
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var count = 0;
            foreach (var child in enumerable)
            {
                yield return child;
                if (++count >= 128)
                {
                    yield break;
                }
            }
        }
    }

    private static bool ShouldSkip(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(Type) ||
               typeof(Assembly).IsAssignableFrom(type) ||
               typeof(MemberInfo).IsAssignableFrom(type);
    }
}
