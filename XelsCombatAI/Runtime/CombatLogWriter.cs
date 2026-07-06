using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Runtime;

internal sealed class CombatLogWriter(string rootDirectory, string configDirectory, IPluginLog log)
{
    private const int MaxFightLogs = 100;

    public string? WriteFight(CombatHistory history, Configuration config, string reason)
    {
        if (!history.HasFrames)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(rootDirectory);
            var historyPath = this.CreateFightFilePath(history, reason);
            var settingsSnapshot = CombatLogSettingsSnapshot.Capture(config, configDirectory, log);
            File.WriteAllText(historyPath, history.BuildJsonLines(config, settingsSnapshot), Encoding.UTF8);

            log.Information(string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote XCAI combat history: {historyPath} ({history.FrameCount} frames, {history.DurationSeconds:0.0}s, reason={reason})."));
            this.PruneOldLogs();
            return historyPath;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Could not write XCAI combat history.");
            return null;
        }
    }

    private string CreateFightFilePath(CombatHistory history, string reason)
    {
        var started = history.CombatStartUtc == DateTime.MinValue ? DateTime.UtcNow : history.CombatStartUtc;
        var last = history.LastFrame;
        var name = string.Join(
            "_",
            started.ToString("yyyyMMdd-HHmmss'Z'"),
            $"job{history.PlayerClassJobId}",
            $"territory{history.TerritoryType}",
            $"cfcid{history.ContentFinderConditionId}",
            $"lasttarget{last?.TargetBaseId ?? 0}",
            Sanitize(reason));

        var path = Path.Combine(rootDirectory, $"{name}.jsonl");
        if (!File.Exists(path))
        {
            return path;
        }

        for (var i = 1; i < 1000; ++i)
        {
            var candidate = Path.Combine(rootDirectory, $"{name}-{i}.jsonl");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(rootDirectory, $"{name}-{Guid.NewGuid():N}.jsonl");
    }

    private void PruneOldLogs()
    {
        var root = new DirectoryInfo(rootDirectory);
        if (!root.Exists)
        {
            return;
        }

        foreach (var entry in root.GetFileSystemInfos("*.jsonl")
                     .Concat(root.GetDirectories())
                     .OrderByDescending(entry => entry.CreationTimeUtc)
                     .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
                     .Skip(MaxFightLogs))
        {
            try
            {
                if (entry is DirectoryInfo directory)
                {
                    directory.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception ex)
            {
                log.Verbose($"Could not prune old combat log '{entry.FullName}': {ex.Message}");
            }
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        var sanitized = builder.ToString().Trim('-');
        return sanitized.Length == 0 ? "fight" : sanitized;
    }
}
