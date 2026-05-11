using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;

namespace XelsCombatAI.Integrations;

internal static class ReflectionObjectSearch
{
    public static object? FindLoadedPlugin(IDalamudPluginInterface pluginInterface, string typeFullName, int maxDepth, params string[] pluginNames)
    {
        foreach (var plugin in EnumerateLoadedPlugins(pluginInterface, pluginNames))
        {
            var found = FindObject(plugin, typeFullName, maxDepth);
            if (found != null)
            {
                return found;
            }
        }

        return FindObject(pluginInterface, typeFullName, maxDepth);
    }

    public static bool HasLoadedPlugin(IDalamudPluginInterface pluginInterface, params string[] pluginNames)
    {
        foreach (var _ in EnumerateLoadedPlugins(pluginInterface, pluginNames))
        {
            return true;
        }

        return false;
    }

    public static IEnumerable<object> EnumerateLoadedPlugins(IDalamudPluginInterface pluginInterface, params string[] pluginNames)
    {
        IEnumerable<object>? plugins;
        try
        {
            plugins = pluginInterface.InstalledPlugins?.Cast<object>().ToArray();
        }
        catch
        {
            yield break;
        }

        if (plugins == null)
        {
            yield break;
        }

        foreach (var plugin in plugins)
        {
            bool loaded;
            try
            {
                loaded = (bool)(plugin.GetType().GetProperty("IsLoaded")?.GetValue(plugin) ?? false);
            }
            catch
            {
                continue;
            }

            if (!loaded || !MatchesPlugin(plugin, pluginNames))
            {
                continue;
            }

            yield return plugin;
        }
    }

    private static bool MatchesPlugin(object plugin, string[] pluginNames)
    {
        string? internalName;
        string? name;
        try
        {
            var type = plugin.GetType();
            internalName = type.GetProperty("InternalName")?.GetValue(plugin)?.ToString();
            name = type.GetProperty("Name")?.GetValue(plugin)?.ToString();
        }
        catch
        {
            return false;
        }

        foreach (var pluginName in pluginNames)
        {
            if (string.Equals(internalName, pluginName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, pluginName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
