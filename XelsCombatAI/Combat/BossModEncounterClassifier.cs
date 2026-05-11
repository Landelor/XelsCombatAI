using System;

namespace XelsCombatAI.Combat;

internal static class BossModEncounterClassifier
{
    public static bool IsEncounterActive(object? activeBossModule, object? activeZoneModule)
    {
        return activeZoneModule != null || IsBossEncounterModule(activeBossModule?.GetType().Name);
    }

    public static bool IsBossEncounterModule(string? moduleName)
    {
        return !string.IsNullOrWhiteSpace(moduleName) &&
               !moduleName.Equals("<none>", StringComparison.Ordinal) &&
               !IsDungeonTrashModule(moduleName);
    }

    public static bool IsDungeonTrashModule(string? moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        var simpleName = ShortTypeName(moduleName);
        if (simpleName.Length < 3 || simpleName[0] != 'D')
        {
            return false;
        }

        var index = 1;
        while (index < simpleName.Length && char.IsDigit(simpleName[index]))
        {
            index++;
        }

        return index > 1 &&
               index < simpleName.Length &&
               simpleName[index - 1] == '0';
    }

    private static string ShortTypeName(string moduleName)
    {
        var separator = moduleName.LastIndexOf('.');
        return separator >= 0 && separator < moduleName.Length - 1
            ? moduleName[(separator + 1)..]
            : moduleName;
    }
}
