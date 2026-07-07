using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Runtime;

internal sealed class CombatLogWriter(string rootDirectory, string configDirectory, IPluginLog log)
{
    private const int MaxFightLogs = 100;
    private static readonly TimeSpan DisposeExportWait = TimeSpan.FromSeconds(2);
    private readonly object queueLock = new();
    private readonly Queue<CombatHistory.ExportSnapshot> pendingExports = [];
    private Task? worker;

    public void EnqueueFight(CombatHistory.ExportSnapshot export)
    {
        if (!export.HasFrames)
        {
            return;
        }

        lock (this.queueLock)
        {
            this.pendingExports.Enqueue(export);
            this.worker ??= Task.Run(this.ProcessQueue);
        }
    }

    public void WaitForPendingExports()
    {
        Task? activeWorker;
        lock (this.queueLock)
        {
            activeWorker = this.worker;
        }

        if (activeWorker == null)
        {
            return;
        }

        try
        {
            if (!activeWorker.Wait(DisposeExportWait))
            {
                log.Information("XCAI combat history export is still running in the background.");
            }
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Could not wait for XCAI combat history export.");
        }
    }

    private void ProcessQueue()
    {
        while (true)
        {
            CombatHistory.ExportSnapshot export;
            lock (this.queueLock)
            {
                if (this.pendingExports.Count == 0)
                {
                    this.worker = null;
                    return;
                }

                export = this.pendingExports.Dequeue();
            }

            this.WriteFight(export);
        }
    }

    private string? WriteFight(CombatHistory.ExportSnapshot export)
    {
        if (!export.HasFrames)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(rootDirectory);
            var historyPath = this.CreateFightFilePath(export);
            var settingsSnapshot = CombatLogSettingsSnapshot.Capture(export.XcaiRuntimeConfig, configDirectory, (ex, message) => log.Verbose(ex, message));
            WriteCompressedJsonLines(historyPath, writer => export.WriteJsonLines(writer, settingsSnapshot));

            log.Information(string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote XCAI combat history: {historyPath} ({export.FrameCount} frames, {export.DurationSeconds:0.0}s, reason={export.Reason})."));
            this.PruneOldLogs();
            return historyPath;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Could not write XCAI combat history.");
            return null;
        }
    }

    internal static void WriteCompressedJsonLines(string path, Action<TextWriter> write)
    {
        var tempPath = $"{path}.tmp";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                write(writer);
            }

            File.Move(tempPath, path, overwrite: false);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort cleanup; the original export failure is more useful to callers.
            }

            throw;
        }
    }

    private string CreateFightFilePath(CombatHistory.ExportSnapshot export)
    {
        var started = export.CombatStartUtc == DateTime.MinValue ? DateTime.UtcNow : export.CombatStartUtc;
        var name = string.Join(
            "_",
            started.ToString("yyyyMMdd-HHmmss'Z'"),
            $"job{export.PlayerClassJobId}",
            $"territory{export.TerritoryType}",
            $"cfcid{export.ContentFinderConditionId}",
            $"lasttarget{export.LastTargetBaseId}",
            Sanitize(export.Reason));

        var path = Path.Combine(rootDirectory, $"{name}.jsonl.gz");
        if (!File.Exists(path))
        {
            return path;
        }

        for (var i = 1; i < 1000; ++i)
        {
            var candidate = Path.Combine(rootDirectory, $"{name}-{i}.jsonl.gz");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(rootDirectory, $"{name}-{Guid.NewGuid():N}.jsonl.gz");
    }

    private void PruneOldLogs()
        => PruneOldLogs(rootDirectory, message => log.Verbose(message));

    internal static void PruneOldLogs(string directory, Action<string>? logFailure = null)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            return;
        }

        foreach (var entry in root.GetFileSystemInfos("*.jsonl")
                     .Concat(root.GetFileSystemInfos("*.jsonl.gz"))
                     .Concat(root.GetDirectories())
                     .OrderByDescending(entry => entry.CreationTimeUtc)
                     .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
                     .Skip(MaxFightLogs))
        {
            try
            {
                if (entry is DirectoryInfo oldDirectory)
                {
                    oldDirectory.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception ex)
            {
                logFailure?.Invoke($"Could not prune old combat log '{entry.FullName}': {ex.Message}");
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
