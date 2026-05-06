using System;
using System.Text;
using XelsCombatAI.Combat;
using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed class CombatHistory
{
    private const int MaxFrames = 1200;
    private static readonly TimeSpan RecordInterval = TimeSpan.FromMilliseconds(250);

    private readonly CombatHistoryFrame?[] frames = new CombatHistoryFrame?[MaxFrames];
    private int head;
    private int count;
    private DateTime combatStart = DateTime.MinValue;
    private DateTime lastRecordedAt = DateTime.MinValue;

    public void Reset()
    {
        this.head = 0;
        this.count = 0;
        this.combatStart = DateTime.MinValue;
        this.lastRecordedAt = DateTime.MinValue;
    }

    public void Record(RuntimeStatus status, AoePackPositioningStatus aoe)
    {
        var now = DateTime.UtcNow;
        if (now - this.lastRecordedAt < RecordInterval)
            return;

        if (this.combatStart == DateTime.MinValue)
            this.combatStart = now;

        this.lastRecordedAt = now;

        var frame = new CombatHistoryFrame(
            T: (float)(now - this.combatStart).TotalSeconds,
            InCombat: status.InCombat,
            IsDead: status.IsDead,
            PlayerClassJobId: status.PlayerClassJobId,
            TargetBaseId: status.TargetBaseId,
            Movement: status.LastMovement,
            AutomatedMovementSuppressed: status.AutomatedMovementSuppressed,
            MovementRangeStrategy: status.LastMovementRangeStrategy,
            ForbiddenZoneCushion: status.LastForbiddenZoneCushion,
            Range: status.LastRange,
            LastPositional: status.LastPositional,
            TrueNorthActive: status.TrueNorthActive,
            TrueNorthCharges: status.TrueNorthCharges,
            GapSafety: status.LastGapCloserSafety,
            EscapeSafety: status.LastEscapeGapCloserSafety,
            Reason: aoe.LastReason,
            Henched: aoe.RsrHenchedActive,
            Targets: aoe.PriorityTargetCount,
            CurrentHits: aoe.CurrentHits,
            BestHits: aoe.BestHits,
            Injected: aoe.Injected,
            ActionName: aoe.ActionName,
            Shape: aoe.Shape);

        var index = (this.head + this.count) % MaxFrames;
        this.frames[index] = frame;
        if (this.count < MaxFrames)
            this.count++;
        else
            this.head = (this.head + 1) % MaxFrames;
    }

    public string Build(Configuration config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Combat History ===");

        if (this.count == 0)
        {
            sb.AppendLine("No frames recorded.");
            return sb.ToString();
        }

        var first = this.frames[this.head]!;
        var last = this.frames[(this.head + this.count - 1) % MaxFrames]!;
        sb.AppendLine($"Start={this.combatStart:O}  Duration={last.T:0.0}s  Frames={this.count}");
        sb.AppendLine();
        sb.AppendLine("[Header]");
        sb.AppendLine($"Job={first.PlayerClassJobId}  AoECombatControl={config.AoePackPositioningAoeCombatControl}  ControlRsrTarget={config.AoePackPositioningControlRsrTarget}  MinExtraTargets={config.AoePackPositioningMinimumExtraTargets}  Threshold={config.AoEEnemyThreshold}  ManagePositionals={config.ManagePositionals}  ManageTrueNorth={config.ManageTrueNorth}  CombatStyle={config.CombatStyle}");
        sb.AppendLine();
        sb.AppendLine("[Frames]");

        CombatHistoryFrame? prev = null;
        for (var i = 0; i < this.count; i++)
        {
            var frame = this.frames[(this.head + i) % MaxFrames]!;
            sb.Append($"[T+{frame.T,6:0.00}]");

            // Always show state changes relevant to debugging
            AppendIfChanged(sb, "InCombat", frame.InCombat, prev?.InCombat);
            AppendIfChanged(sb, "Dead", frame.IsDead, prev?.IsDead);
            AppendIfChanged(sb, "Target", frame.TargetBaseId, prev?.TargetBaseId);
            AppendIfChanged(sb, "Move", frame.Movement, prev?.Movement);
            AppendIfChanged(sb, "Suppressed", frame.AutomatedMovementSuppressed, prev?.AutomatedMovementSuppressed);
            AppendIfChanged(sb, "Strategy", frame.MovementRangeStrategy, prev?.MovementRangeStrategy);
            AppendIfChanged(sb, "Cushion", frame.ForbiddenZoneCushion, prev?.ForbiddenZoneCushion);
            AppendIfChanged(sb, "Range", $"{frame.Range:0.0}", prev == null ? null : $"{prev.Range:0.0}");
            AppendIfChanged(sb, "Positional", frame.LastPositional, prev?.LastPositional);
            AppendIfChanged(sb, "TrueNorth", frame.TrueNorthActive, prev?.TrueNorthActive);
            AppendIfChanged(sb, "TNCharges", frame.TrueNorthCharges, prev?.TrueNorthCharges);
            AppendIfChanged(sb, "Gap", frame.GapSafety, prev?.GapSafety);
            AppendIfChanged(sb, "Escape", frame.EscapeSafety, prev?.EscapeSafety);

            // AoE pack fields — only print when relevant
            AppendIfChanged(sb, "AoE", frame.Reason, prev?.Reason);
            AppendIfChanged(sb, "Henched", frame.Henched, prev?.Henched);
            AppendIfChanged(sb, "Targets", frame.Targets, prev?.Targets);
            if (frame.CurrentHits != 0 || frame.BestHits != 0 || prev?.CurrentHits != 0 || prev?.BestHits != 0)
                AppendIfChanged(sb, "Hits", $"{frame.CurrentHits}/{frame.BestHits}", $"{prev?.CurrentHits}/{prev?.BestHits}");
            AppendIfChanged(sb, "Injected", frame.Injected, prev?.Injected);
            if (frame.ActionName != "<none>")
                AppendIfChanged(sb, "Action", $"{frame.ActionName}({frame.Shape})", prev == null ? null : $"{prev.ActionName}({prev.Shape})");

            sb.AppendLine();
            prev = frame;
        }

        return sb.ToString();
    }

    private static void AppendIfChanged<T>(StringBuilder sb, string label, T current, T? previous) where T : struct
    {
        if (previous == null || !current.Equals(previous.Value))
            sb.Append($"  {label}={current}");
    }

    private static void AppendIfChanged(StringBuilder sb, string label, bool? current, bool? previous)
    {
        if (current != previous)
            sb.Append($"  {label}={current}");
    }

    private static void AppendIfChanged(StringBuilder sb, string label, string? current, string? previous)
    {
        if (current != previous)
            sb.Append($"  {label}={current}");
    }
}
