using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Runtime;

internal sealed record CombatLogSettingsSnapshot(
    DateTime CapturedAtUtc,
    string ConfigRoot,
    IReadOnlyList<CombatLogPluginSettingsSnapshot> Plugins)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true
    };

    static CombatLogSettingsSnapshot()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static CombatLogSettingsSnapshot Capture(Configuration config, string configDirectory, IPluginLog log)
        => Capture(config, configDirectory, (ex, message) => log.Verbose(ex, message));

    internal static CombatLogSettingsSnapshot Capture(Configuration config, string configDirectory, Action<Exception, string>? logError)
    {
        var configRoot = ResolveConfigRoot(configDirectory);
        var plugins = new List<CombatLogPluginSettingsSnapshot>
        {
            CaptureXcai(config, configRoot, logError),
            CaptureFileBackedPlugin("BossModReborn", configRoot, ["BossModReborn.json"], ["BossModReborn"], logError),
            CaptureFileBackedPlugin("RotationSolver", configRoot, ["RotationSolver.json", "RotationSolverReborn.json"], ["RotationSolver", "RotationSolverReborn"], logError)
        };

        return new CombatLogSettingsSnapshot(
            DateTime.UtcNow,
            string.IsNullOrWhiteSpace(configRoot) ? "<unknown>" : Path.GetFileName(configRoot),
            plugins);
    }

    private static CombatLogPluginSettingsSnapshot CaptureXcai(Configuration config, string configRoot, Action<Exception, string>? logError)
    {
        var files = new List<CombatLogSettingsFileSnapshot>();
        var configFile = Path.Combine(configRoot, "XelsCombatAI.json");
        if (File.Exists(configFile))
        {
            files.Add(ReadJsonFile(configRoot, configFile, logError));
        }

        return new CombatLogPluginSettingsSnapshot(
            "XelsCombatAI",
            "captured",
            CombatLogPrivacy.RedactSettings(JsonSerializer.SerializeToElement(config, JsonOptions)),
            files);
    }

    private static CombatLogPluginSettingsSnapshot CaptureFileBackedPlugin(
        string pluginName,
        string configRoot,
        string[] rootFiles,
        string[] directories,
        Action<Exception, string>? logError)
    {
        var files = new List<CombatLogSettingsFileSnapshot>();
        foreach (var file in rootFiles.Select(fileName => Path.Combine(configRoot, fileName)))
        {
            if (File.Exists(file))
            {
                files.Add(ReadJsonFile(configRoot, file, logError));
            }
        }

        foreach (var directory in directories.Select(directoryName => Path.Combine(configRoot, directoryName)))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                files.AddRange(EnumerateSettingsJsonFiles(directory)
                    .Select(file => ReadJsonFile(configRoot, file, logError)));
            }
            catch (Exception ex)
            {
                logError?.Invoke(ex, $"Could not enumerate settings directory '{RelativePath(configRoot, directory)}' for run-review log.");
                files.Add(new CombatLogSettingsFileSnapshot(
                    RelativePath(configRoot, directory),
                    "error",
                    SizeBytes: null,
                    LastWriteUtc: null,
                    Settings: null,
                    ex.Message));
            }
        }

        var status = files.Count > 0 ? "captured" : "not found";
        return new CombatLogPluginSettingsSnapshot(pluginName, status, RuntimeConfig: null, files);
    }

    private static IEnumerable<string> EnumerateSettingsJsonFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Where(IsSettingsJsonFile)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSettingsJsonFile(string file)
    {
        foreach (var segment in file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("replays", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("combat-logs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Images", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static CombatLogSettingsFileSnapshot ReadJsonFile(string configRoot, string file, Action<Exception, string>? logError)
    {
        var relativePath = RelativePath(configRoot, file);
        try
        {
            var info = new FileInfo(file);
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return new CombatLogSettingsFileSnapshot(
                relativePath,
                "captured",
                info.Length,
                info.LastWriteTimeUtc,
                CombatLogPrivacy.RedactSettings(document.RootElement),
                Error: null);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex, $"Could not capture settings file '{relativePath}' for run-review log.");
            var info = File.Exists(file) ? new FileInfo(file) : null;
            return new CombatLogSettingsFileSnapshot(
                relativePath,
                "error",
                info?.Length,
                info?.LastWriteTimeUtc,
                Settings: null,
                ex.Message);
        }
    }

    private static string ResolveConfigRoot(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return string.Empty;
        }

        var directory = new DirectoryInfo(configDirectory);
        return directory.Parent?.FullName ?? directory.FullName;
    }

    private static string RelativePath(string root, string file)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFileName(file);
        }

        try
        {
            return Path.GetRelativePath(root, file);
        }
        catch
        {
            return Path.GetFileName(file);
        }
    }
}

internal sealed record CombatLogPluginSettingsSnapshot(
    string Plugin,
    string Status,
    JsonElement? RuntimeConfig,
    IReadOnlyList<CombatLogSettingsFileSnapshot> Files);

internal sealed record CombatLogSettingsFileSnapshot(
    string RelativePath,
    string Status,
    long? SizeBytes,
    DateTime? LastWriteUtc,
    JsonElement? Settings,
    string? Error);
