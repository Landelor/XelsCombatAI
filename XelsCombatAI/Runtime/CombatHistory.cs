using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XelsCombatAI.Combat;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Runtime;

internal sealed class CombatHistory
{
    // One hour at the combat sample rate; downtime is sampled slower so full duties fit without dropping early context.
    private const int MaxFrames = 14400;
    private static readonly TimeSpan CombatRecordInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DowntimeRecordInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MobilityDecisionFreshness = TimeSpan.FromMilliseconds(750);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        IncludeFields = true
    };

    static CombatHistory()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly CombatHistoryFrame?[] frames = new CombatHistoryFrame?[MaxFrames];
    private int head;
    private int count;
    private DateTime combatStart = DateTime.MinValue;
    private DateTime lastRecordedAt = DateTime.MinValue;
    private StateCommandType lastSeenRsrSnapshotMode;
    private string logScope = "instance-run";

    public bool HasFrames => this.count > 0;
    public DateTime CombatStartUtc => this.combatStart;
    public CombatHistoryFrame? FirstFrame => this.count == 0 ? null : this.frames[this.head];
    public CombatHistoryFrame? LastFrame => this.count == 0 ? null : this.frames[(this.head + this.count - 1) % MaxFrames];

    public bool ShouldRecord(bool inCombat)
    {
        return this.ShouldRecord(DateTime.UtcNow, inCombat);
    }

    public void Reset(string logScope = "instance-run")
    {
        this.head = 0;
        this.count = 0;
        this.combatStart = DateTime.MinValue;
        this.lastRecordedAt = DateTime.MinValue;
        this.lastSeenRsrSnapshotMode = default;
        this.logScope = logScope;
    }

    public void Record(RuntimeStatus status, AoePackPositioningStatus aoe, IReadOnlyList<CombatHistoryActorSnapshot> actors)
    {
        var now = DateTime.UtcNow;
        if (!this.ShouldRecord(now, status.InCombat))
            return;

        if (this.combatStart == DateTime.MinValue)
            this.combatStart = now;

        this.lastRecordedAt = now;

        if (aoe.RsrHenchedActive)
            this.lastSeenRsrSnapshotMode = aoe.RsrSnapshotMode;

        var frame = new CombatHistoryFrame(
            TimestampUtc: now,
            T: (float)(now - this.combatStart).TotalSeconds,
            InCombat: status.InCombat,
            IsDead: status.IsDead,
            PlayerClassJobId: status.PlayerClassJobId,
            TerritoryType: status.TerritoryType,
            ContentFinderConditionId: status.ContentFinderConditionId,
            PlayerPosition: status.PlayerPosition,
            PlayerRotation: status.PlayerRotation,
            TargetBaseId: status.TargetBaseId,
            TargetObjectId: status.TargetObjectId,
            TargetPosition: status.TargetPosition,
            TargetRotation: status.TargetRotation,
            TargetRadius: status.TargetRadius,
            Movement: status.LastMovement,
            AutomatedMovementSuppressed: status.AutomatedMovementSuppressed,
            MovementRangeStrategy: status.LastMovementRangeStrategy,
            SafetyBuffer: status.LastForbiddenZoneCushion,
            TargetUptimeRange: status.LastTargetUptimeRange,
            LastPositional: status.LastPositional,
            TrueNorthActive: status.TrueNorthActive,
            TrueNorthCharges: status.TrueNorthCharges,
            GapSafety: status.LastGapCloserSafety,
            EscapeSafety: status.LastEscapeGapCloserSafety,
            EscapeLanding: status.LastEscapeLanding,
            MobilityDecision: FreshMobilityDecision(now, status.MobilityDecision),
            HealerCoverageReason: status.HealerCoveragePositioning.LastReason,
            HealerCoverageInjected: status.HealerCoveragePositioning.Injected,
            HealerCoverageMembers: status.HealerCoveragePositioning.PartyMembers,
            HealerCoverageDist: status.HealerCoveragePositioning.DistanceToCenter,
            Reason: aoe.LastReason,
            Henched: aoe.RsrHenchedActive,
            RsrStatus: aoe.RsrStatus,
            RsrReflectionDiagnostics: aoe.RsrReflectionDiagnostics,
            RsrSnapshotMode: aoe.RsrSnapshotMode,
            RsrLastRestore: aoe.RsrLastRestoreStatus,
            Targets: aoe.PriorityTargetCount,
            CurrentHits: aoe.CurrentHits,
            BestHits: aoe.BestHits,
            Injected: aoe.Injected,
            ActionName: aoe.ActionName,
            Shape: aoe.Shape,
            AoeCandidate: aoe.Candidate,
            AoePrimaryTarget: aoe.PrimaryTarget,
            AoeCandidateInjected: aoe.CandidateInjected,
            SurvZoneReason: status.SurvivabilityZonePositioning.LastReason,
            SurvZoneInjected: status.SurvivabilityZonePositioning.Injected,
            SurvZoneName: status.SurvivabilityZonePositioning.ZoneName,
            SurvZoneDistance: status.SurvivabilityZonePositioning.DistanceToCenter,
            SurvZoneCenter: status.SurvivabilityZonePositioning.ZoneCenter,
            SurvZoneCaster: status.SurvivabilityZonePositioning.CasterPosition,
            PassageReason: status.PassageOfArmsPositioning.LastReason,
            PassageInjected: status.PassageOfArmsPositioning.Injected,
            PassageDistance: status.PassageOfArmsPositioning.DistanceToPreferred,
            PassageInCone: status.PassageOfArmsPositioning.PlayerInCone,
            PassagePaladin: status.PassageOfArmsPositioning.PaladinPosition,
            PassagePreferred: status.PassageOfArmsPositioning.PreferredPosition,
            HealerCoverageCenter: status.HealerCoveragePositioning.Center,
            AggroReason: status.AggroSafety.LastReason,
            AggroInjected: status.AggroSafety.Injected,
            AggroSeconds: status.AggroSafety.AggroSeconds,
            ArenaEdgeReason: status.ArenaEdgeReason,
            GoalPriority: status.AoeGoalPriority,
            GoalSources: status.AoeGoalSources,
            BossModActiveModule: status.BossModMovement.ActiveModule,
            BossModActiveZoneModule: status.BossModMovement.ActiveZoneModule,
            BossModNavigationDestination: status.BossModMovement.NavigationDestination,
            BossModNavigationNextWaypoint: status.BossModMovement.NavigationNextWaypoint,
            BossModNavigationStats: status.BossModMovement.NavigationStats,
            BossModVnavmeshGuard: status.BossModMovement.VnavmeshGuard,
            BossModControllerTarget: status.BossModMovement.ControllerTarget,
            BossModMovementOverride: status.BossModMovement.MovementOverride,
            BossModHintSummary: status.BossModMovement.HintSummary,
            BossModMovement: status.BossModMovement,
            MovementPlanner: status.MovementPlanner,
            ManualMovementInput: status.ManualMovementInput,
            Facing: status.Facing,
            RedMageMeleeCombo: status.RedMageMeleeCombo,
            TrashPull: aoe.TrashPull,
            Actors: actors);

        var index = (this.head + this.count) % MaxFrames;
        this.frames[index] = frame;
        if (this.count < MaxFrames)
            this.count++;
        else
            this.head = (this.head + 1) % MaxFrames;
    }

    private bool ShouldRecord(DateTime now, bool inCombat)
    {
        var recordInterval = inCombat ? CombatRecordInterval : DowntimeRecordInterval;
        return now - this.lastRecordedAt >= recordInterval;
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
        sb.AppendLine($"Job={first.PlayerClassJobId}  TargetUptime={config.ManageTargetUptime}  PickAoeTarget={config.PickBetterAoeTarget}  KeepTrashTarget={config.KeepTrashTargetSelected}  LeadTrashPulls={config.LeadTrashPullsWithTank}  ManagePositionals={config.ManagePositionals}  ManageTrueNorth={config.ManageTrueNorth}  SocialTurning={config.ManageSocialTurning}  RedMageMelee={config.UseRedMageMeleeComboMovement}  CombatStyle={config.CombatStyle}  RsrSnapshot={this.lastSeenRsrSnapshotMode}");
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
            AppendIfChanged(sb, "PlayerPos", FormatVector(frame.PlayerPosition), prev == null ? null : FormatVector(prev.PlayerPosition));
            AppendIfChanged(sb, "PlayerRot", FormatFloat(frame.PlayerRotation), prev == null ? null : FormatFloat(prev.PlayerRotation));
            AppendIfChanged(sb, "Target", frame.TargetBaseId, prev?.TargetBaseId);
            AppendIfChanged(sb, "TargetObj", frame.TargetObjectId, prev?.TargetObjectId);
            AppendIfChanged(sb, "TargetPos", FormatVector(frame.TargetPosition), prev == null ? null : FormatVector(prev.TargetPosition));
            AppendIfChanged(sb, "TargetRot", FormatFloat(frame.TargetRotation), prev == null ? null : FormatFloat(prev.TargetRotation));
            AppendIfChanged(sb, "TargetRadius", FormatFloat(frame.TargetRadius), prev == null ? null : FormatFloat(prev.TargetRadius));
            AppendIfChanged(sb, "Move", frame.Movement, prev?.Movement);
            AppendIfChanged(sb, "Suppressed", frame.AutomatedMovementSuppressed, prev?.AutomatedMovementSuppressed);
            AppendIfChanged(sb, "ManualInput", frame.ManualMovementInput, prev?.ManualMovementInput);
            AppendIfChanged(sb, "Facing", FormatFacing(frame.Facing), prev == null ? null : FormatFacing(prev.Facing));
            AppendIfChanged(sb, "RedMageMelee", FormatRedMageMelee(frame.RedMageMeleeCombo), prev == null ? null : FormatRedMageMelee(prev.RedMageMeleeCombo));
            AppendIfChanged(sb, "Strategy", frame.MovementRangeStrategy, prev?.MovementRangeStrategy);
            AppendIfChanged(sb, "GoalSources", frame.GoalSources, prev?.GoalSources);
            AppendIfChanged(sb, "GoalState", frame.GoalPriority, prev?.GoalPriority);
            AppendIfChanged(sb, "BMRModule", frame.BossModActiveModule, prev?.BossModActiveModule);
            AppendIfChanged(sb, "BMRZoneModule", frame.BossModActiveZoneModule, prev?.BossModActiveZoneModule);
            AppendIfChanged(sb, "BMRDest", frame.BossModNavigationDestination, prev?.BossModNavigationDestination);
            AppendIfChanged(sb, "BMRNext", frame.BossModNavigationNextWaypoint, prev?.BossModNavigationNextWaypoint);
            AppendIfChanged(sb, "BMRNav", frame.BossModNavigationStats, prev?.BossModNavigationStats);
            AppendIfChanged(sb, "BMRVnavGuard", frame.BossModVnavmeshGuard, prev?.BossModVnavmeshGuard);
            AppendIfChanged(sb, "BMRPlannerSteer", frame.BossModMovement.PlannerSteer, prev?.BossModMovement.PlannerSteer);
            AppendIfChanged(sb, "BMRController", frame.BossModControllerTarget, prev?.BossModControllerTarget);
            AppendIfChanged(sb, "BMRMove", frame.BossModMovementOverride, prev?.BossModMovementOverride);
            AppendIfChanged(sb, "BMRHints", frame.BossModHintSummary, prev?.BossModHintSummary);
            AppendIfChanged(sb, "PlannerIntent", frame.MovementPlanner.IntentId, prev?.MovementPlanner.IntentId);
            AppendIfChanged(sb, "PlannerSource", frame.MovementPlanner.ChosenSource, prev?.MovementPlanner.ChosenSource);
            AppendIfChanged(sb, "PlannerDest", FormatVector(frame.MovementPlanner.Destination), prev == null ? null : FormatVector(prev.MovementPlanner.Destination));
            AppendIfChanged(sb, "PlannerSwitch", frame.MovementPlanner.SwitchReason, prev?.MovementPlanner.SwitchReason);
            AppendIfChanged(sb, "PlannerSuppress", frame.MovementPlanner.SuppressionReason, prev?.MovementPlanner.SuppressionReason);
            AppendIfChanged(sb, "PlannerCounts", $"{frame.MovementPlanner.AcceptedCount}/{frame.MovementPlanner.GeneratedCount}", prev == null ? null : $"{prev.MovementPlanner.AcceptedCount}/{prev.MovementPlanner.GeneratedCount}");
            AppendIfChanged(sb, "PlannerPath", frame.MovementPlanner.PathStatus, prev?.MovementPlanner.PathStatus);
            AppendIfChanged(sb, "PlannerScore", frame.MovementPlanner.ScoreBreakdown, prev?.MovementPlanner.ScoreBreakdown);
            AppendIfChanged(sb, "PlannerRejects", FormatPlannerRejects(frame.MovementPlanner.RejectedByReason), prev == null ? null : FormatPlannerRejects(prev.MovementPlanner.RejectedByReason));
            AppendIfChanged(sb, "BMRGeometry", FormatBmrGeometry(frame.MovementPlanner), prev == null ? null : FormatBmrGeometry(prev.MovementPlanner));
            AppendIfChanged(sb, "RouteMemory", FormatRouteMemory(frame.MovementPlanner.RouteMemory), prev == null ? null : FormatRouteMemory(prev.MovementPlanner.RouteMemory));
            AppendIfChanged(sb, "TargetLOS", FormatLineOfSight(frame.MovementPlanner.LineOfSight), prev == null ? null : FormatLineOfSight(prev.MovementPlanner.LineOfSight));
            AppendIfChanged(sb, "SafetyBuffer", frame.SafetyBuffer, prev?.SafetyBuffer);
            AppendIfChanged(sb, "TargetUptime", $"{frame.TargetUptimeRange:0.0}", prev == null ? null : $"{prev.TargetUptimeRange:0.0}");
            AppendIfChanged(sb, "Positional", frame.LastPositional, prev?.LastPositional);
            AppendIfChanged(sb, "TrueNorth", frame.TrueNorthActive, prev?.TrueNorthActive);
            AppendIfChanged(sb, "TNCharges", frame.TrueNorthCharges, prev?.TrueNorthCharges);
            AppendIfChanged(sb, "Gap", frame.GapSafety, prev?.GapSafety);
            AppendIfChanged(sb, "Escape", frame.EscapeSafety, prev?.EscapeSafety);
            AppendIfChanged(sb, "EscapeLanding", FormatVector(frame.EscapeLanding), prev == null ? null : FormatVector(prev.EscapeLanding));
            AppendIfChanged(sb, "Mobility", FormatMobility(frame.MobilityDecision), prev == null ? null : FormatMobility(prev.MobilityDecision));
            AppendIfChanged(sb, "HealerCoverage", frame.HealerCoverageReason, prev?.HealerCoverageReason);
            AppendIfChanged(sb, "HCInjected", frame.HealerCoverageInjected, prev?.HealerCoverageInjected);
            AppendIfChanged(sb, "HCMembers", frame.HealerCoverageMembers, prev?.HealerCoverageMembers);
            AppendIfChanged(sb, "HCDist", $"{frame.HealerCoverageDist:0.0}", prev == null ? null : $"{prev.HealerCoverageDist:0.0}");

            // AoE pack fields — only print when relevant
            AppendIfChanged(sb, "AoEPack", frame.Reason, prev?.Reason);
            AppendIfChanged(sb, "Henched", frame.Henched, prev?.Henched);
            AppendIfChanged(sb, "RsrStatus", frame.RsrStatus, prev?.RsrStatus);
            AppendIfChanged(sb, "RsrRestore", frame.RsrLastRestore, prev?.RsrLastRestore);
            AppendIfChanged(sb, "RsrSnapshot", frame.RsrSnapshotMode, prev?.RsrSnapshotMode);
            AppendIfChanged(sb, "RsrReflect", frame.RsrReflectionDiagnostics, prev?.RsrReflectionDiagnostics);
            AppendIfChanged(sb, "Targets", frame.Targets, prev?.Targets);
            if (frame.CurrentHits != 0 || frame.BestHits != 0 || prev?.CurrentHits != 0 || prev?.BestHits != 0)
                AppendIfChanged(sb, "Hits", $"{frame.CurrentHits}/{frame.BestHits}", $"{prev?.CurrentHits}/{prev?.BestHits}");
            AppendIfChanged(sb, "Injected", frame.Injected, prev?.Injected);
            if (frame.ActionName != "<none>")
                AppendIfChanged(sb, "Action", $"{frame.ActionName}({frame.Shape})", prev == null ? null : $"{prev.ActionName}({prev.Shape})");
            AppendIfChanged(sb, "AoeCandidate", FormatVector(frame.AoeCandidate), prev == null ? null : FormatVector(prev.AoeCandidate));
            AppendIfChanged(sb, "AoePrimary", FormatVector(frame.AoePrimaryTarget), prev == null ? null : FormatVector(prev.AoePrimaryTarget));
            AppendIfChanged(sb, "AoeCandidateInjected", frame.AoeCandidateInjected, prev?.AoeCandidateInjected);
            AppendIfChanged(sb, "TrashPhase", frame.TrashPull.Phase, prev?.TrashPull.Phase);
            AppendIfChanged(sb, "TrashReason", frame.TrashPull.Reason, prev?.TrashPull.Reason);
            AppendIfChanged(sb, "TankLeadDest", FormatVector(frame.TrashPull.LeadDestination), prev == null ? null : FormatVector(prev.TrashPull.LeadDestination));
            AppendIfChanged(sb, "TankLeadBehind", FormatNullableFloat(frame.TrashPull.BehindDistance), prev == null ? null : FormatNullableFloat(prev.TrashPull.BehindDistance));
            AppendIfChanged(sb, "TankLeadReject", frame.TrashPull.LeadRejectionReason, prev?.TrashPull.LeadRejectionReason);

            // Survivability zone — only print when active or just cleared
            if (frame.SurvZoneInjected || prev?.SurvZoneInjected == true)
            {
                AppendIfChanged(sb, "SurvZone", frame.SurvZoneReason, prev?.SurvZoneReason);
                AppendIfChanged(sb, "SurvZoneInjected", frame.SurvZoneInjected, prev?.SurvZoneInjected);
                if (frame.SurvZoneName != "<none>")
                {
                    AppendIfChanged(sb, "SurvZoneName", frame.SurvZoneName, prev?.SurvZoneName);
                }

                AppendIfChanged(sb, "SurvZoneDist", $"{frame.SurvZoneDistance:0.0}", prev == null ? null : $"{prev.SurvZoneDistance:0.0}");
                AppendIfChanged(sb, "SurvZoneCenter", FormatVector(frame.SurvZoneCenter), prev == null ? null : FormatVector(prev.SurvZoneCenter));
                AppendIfChanged(sb, "SurvZoneCaster", FormatVector(frame.SurvZoneCaster), prev == null ? null : FormatVector(prev.SurvZoneCaster));
            }
            else
            {
                AppendIfChanged(sb, "SurvZone", frame.SurvZoneReason, prev?.SurvZoneReason);
            }

            // Passage of arms — only print when active or just cleared
            if (frame.PassageInjected || prev?.PassageInjected == true)
            {
                AppendIfChanged(sb, "Passage", frame.PassageReason, prev?.PassageReason);
                AppendIfChanged(sb, "PassageInjected", frame.PassageInjected, prev?.PassageInjected);
                AppendIfChanged(sb, "PassageDist", $"{frame.PassageDistance:0.0}", prev == null ? null : $"{prev.PassageDistance:0.0}");
                AppendIfChanged(sb, "PassageInCone", frame.PassageInCone, prev?.PassageInCone);
                AppendIfChanged(sb, "PassagePaladin", FormatVector(frame.PassagePaladin), prev == null ? null : FormatVector(prev.PassagePaladin));
                AppendIfChanged(sb, "PassagePreferred", FormatVector(frame.PassagePreferred), prev == null ? null : FormatVector(prev.PassagePreferred));
            }
            else
            {
                AppendIfChanged(sb, "Passage", frame.PassageReason, prev?.PassageReason);
            }

            // Aggro safety — only print when active or just cleared
            if (frame.AggroInjected || prev?.AggroInjected == true)
            {
                AppendIfChanged(sb, "Aggro", frame.AggroReason, prev?.AggroReason);
                AppendIfChanged(sb, "AggroInjected", frame.AggroInjected, prev?.AggroInjected);
                AppendIfChanged(sb, "AggroSecs", $"{frame.AggroSeconds:0.0}", prev == null ? null : $"{prev.AggroSeconds:0.0}");
            }
            else
            {
                AppendIfChanged(sb, "Aggro", frame.AggroReason, prev?.AggroReason);
            }

            AppendIfChanged(sb, "ArenaEdge", frame.ArenaEdgeReason, prev?.ArenaEdgeReason);
            AppendIfChanged(sb, "HCCenter", FormatVector(frame.HealerCoverageCenter), prev == null ? null : FormatVector(prev.HealerCoverageCenter));

            sb.AppendLine();
            prev = frame;
        }

        return sb.ToString();
    }

    public string BuildJsonLines(Configuration config)
    {
        var sb = new StringBuilder();

        if (this.count == 0)
        {
            sb.AppendLine(JsonSerializer.Serialize(
                new CombatHistoryJsonHeader(
                    Type: "header",
                    SchemaVersion: 3,
                    LogScope: this.logScope,
                    PluginVersion: PluginVersion(),
                    RunStartUtc: this.combatStart,
                    RunEndUtc: this.combatStart,
                    CombatStartUtc: this.combatStart,
                    CombatEndUtc: this.combatStart,
                    DurationSeconds: 0,
                    FrameCount: 0,
                    PlayerClassJobId: 0,
                    TerritoryType: 0,
                    ContentFinderConditionId: 0,
                    BossModActiveModule: "<none>",
                    BossModActiveZoneModule: "<none>",
                    Config: CombatHistoryConfigSnapshot.From(config),
                    RsrSnapshotMode: this.lastSeenRsrSnapshotMode.ToString()),
                JsonOptions));
            return sb.ToString();
        }

        var last = this.frames[(this.head + this.count - 1) % MaxFrames]!;
        sb.AppendLine(JsonSerializer.Serialize(
            new CombatHistoryJsonHeader(
                Type: "header",
                SchemaVersion: 3,
                LogScope: this.logScope,
                PluginVersion: PluginVersion(),
                RunStartUtc: this.combatStart,
                RunEndUtc: last.TimestampUtc,
                CombatStartUtc: this.combatStart,
                CombatEndUtc: last.TimestampUtc,
                DurationSeconds: last.T,
                FrameCount: this.count,
                PlayerClassJobId: this.FirstFrame?.PlayerClassJobId ?? 0,
                TerritoryType: this.FirstFrame?.TerritoryType ?? 0,
                ContentFinderConditionId: this.FirstFrame?.ContentFinderConditionId ?? 0,
                BossModActiveModule: last.BossModActiveModule,
                BossModActiveZoneModule: last.BossModActiveZoneModule,
                Config: CombatHistoryConfigSnapshot.From(config),
                RsrSnapshotMode: this.lastSeenRsrSnapshotMode.ToString()),
            JsonOptions));

        CombatHistoryFrame? previous = null;
        for (var i = 0; i < this.count; i++)
        {
            var frame = this.frames[(this.head + i) % MaxFrames]!;
            sb.AppendLine(JsonSerializer.Serialize(
                new CombatHistoryJsonFrame(
                    Type: "frame",
                    Frame: frame,
                    Motion: CombatHistoryMotionSnapshot.From(frame, previous)),
                JsonOptions));
            previous = frame;
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

    private static string FormatVector(Vector3? value)
    {
        if (value == null)
        {
            return "<none>";
        }

        var v = value.Value;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})");
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableFloat(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "<none>";
    }

    private static string FormatPlannerRejects(IReadOnlyDictionary<string, int> rejected)
    {
        return rejected.Count == 0
            ? "<none>"
            : string.Join(",", rejected.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}:{entry.Value}"));
    }

    private static string FormatLineOfSight(MovementLineOfSightDiagnostics lineOfSight)
    {
        if (!lineOfSight.Checked)
        {
            return $"unchecked/{lineOfSight.Reason}";
        }

        var state = lineOfSight.Blocked ? "blocked" : "clear";
        var blockedDistance = lineOfSight.BlockedDistance.HasValue
            ? lineOfSight.BlockedDistance.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "<none>";
        return $"{state}/combat={lineOfSight.CombatClear}/nav={lineOfSight.NavigationClear}/blocked={blockedDistance}/{lineOfSight.Reason}";
    }

    private static string FormatRouteMemory(TrashRouteMemoryDiagnostics routeMemory)
    {
        if (!routeMemory.Active)
        {
            return $"{routeMemory.State}/{routeMemory.Reason}";
        }

        return $"{routeMemory.Source}/{routeMemory.State}/dest={FormatVector(routeMemory.LocalDestination)}/offset={routeMemory.OffsetSide}:{routeMemory.OffsetDistance:0.0}/vnav={routeMemory.VnavStatus}/budget={routeMemory.QueryBudgetUsed}/{routeMemory.QueryBudgetLimit}/{routeMemory.InvalidationReason}";
    }

    private static string FormatBmrGeometry(MovementPlannerDiagnostics planner)
    {
        return $"goals={planner.BmrGoalZones}/forbid={planner.BmrForbiddenZones}/obs={planner.BmrTemporaryObstacles}/tele={planner.BmrTeleporters}/active={planner.BmrDynamicGeometry}";
    }

    private static MobilityDecisionDiagnostics FreshMobilityDecision(DateTime now, MobilityDecisionDiagnostics decision)
    {
        return decision.TimestampUtc != DateTime.MinValue &&
               now - decision.TimestampUtc <= MobilityDecisionFreshness
            ? decision
            : MobilityDecisionDiagnostics.Empty;
    }

    private static string FormatMobility(MobilityDecisionDiagnostics mobility)
    {
        return $"{mobility.State}/{mobility.IntentLabel}/{mobility.ActionName}/{mobility.RiskReason}";
    }

    private static string FormatFacing(FacingStatus facing)
    {
        var source = facing.Source?.ToString() ?? "None";
        var rejection = string.IsNullOrEmpty(facing.RejectionReason)
            ? "<none>"
            : facing.RejectionReason;
        return $"{source}/{facing.Reason}/applied={facing.Applied}/reject={rejection}/members={facing.ConsensusMembers}/desired={FormatNullableFloat(facing.DesiredRotation)}/current={FormatNullableFloat(facing.CurrentRotation)}/delta={FormatNullableFloat(facing.DeltaRadians)}";
    }

    private static string FormatRedMageMelee(RedMageMeleeComboStatus status)
    {
        var candidate = FormatVector(status.CandidateDestination);
        var landing = FormatVector(status.LastJumpLanding);
        return $"enabled={status.Enabled}/mode={status.Mode}/reason={status.LastReason}/mana={status.WhiteMana}:{status.BlackMana}/stacks={status.ManaStacks}/next={status.NextActionName}({status.NextActionId})/targets={status.AffectedTargets}/candidate={candidate}/jump={landing}";
    }

    private sealed record CombatHistoryJsonHeader(
        string Type,
        int SchemaVersion,
        string LogScope,
        string PluginVersion,
        DateTime RunStartUtc,
        DateTime RunEndUtc,
        DateTime CombatStartUtc,
        DateTime CombatEndUtc,
        float DurationSeconds,
        int FrameCount,
        uint PlayerClassJobId,
        uint TerritoryType,
        uint ContentFinderConditionId,
        string BossModActiveModule,
        string BossModActiveZoneModule,
        CombatHistoryConfigSnapshot Config,
        string RsrSnapshotMode);

    private sealed record CombatHistoryConfigSnapshot(
        bool TargetUptime,
        bool PickAoeTarget,
        bool KeepTrashTarget,
        bool LeadTrashPullsWithTank,
        bool ManagePositionals,
        bool ManageTrueNorth,
        bool ManageSocialTurning,
        bool UseRedMageMeleeComboMovement,
        bool FightReviewLoggingEnabled,
        string CombatStyle,
        bool GreedyUnsafeEscapeDashes)
    {
        public static CombatHistoryConfigSnapshot From(Configuration config)
        {
            return new CombatHistoryConfigSnapshot(
                config.ManageTargetUptime,
                config.PickBetterAoeTarget,
                config.KeepTrashTargetSelected,
                config.LeadTrashPullsWithTank,
                config.ManagePositionals,
                config.ManageTrueNorth,
                config.ManageSocialTurning,
                config.UseRedMageMeleeComboMovement,
                config.FightReviewLoggingEnabled,
                config.CombatStyle.ToString(),
                config.UseGapCloser && config.CombatStyle != XelsCombatAI.Models.CombatStyle.Normal);
        }
    }

    private sealed record CombatHistoryJsonFrame(
        string Type,
        CombatHistoryFrame Frame,
        CombatHistoryMotionSnapshot Motion);

    private sealed record CombatHistoryMotionSnapshot(
        Vector3? PlayerDelta,
        float? PlayerStepDistance,
        float? PlayerSpeed,
        float? PlayerTurnRadians,
        float? TargetDistance,
        float? TargetSurfaceDistance)
    {
        public static CombatHistoryMotionSnapshot From(CombatHistoryFrame frame, CombatHistoryFrame? previous)
        {
            var playerDelta = frame.PlayerPosition.HasValue && previous?.PlayerPosition.HasValue == true
                ? frame.PlayerPosition.Value - previous.PlayerPosition.Value
                : (Vector3?)null;
            var step = playerDelta?.Length();
            var elapsed = previous == null ? 0 : frame.T - previous.T;
            var speed = step.HasValue && elapsed > 0
                ? step.Value / elapsed
                : (float?)null;
            var turn = previous == null
                ? (float?)null
                : NormalizeRadians(frame.PlayerRotation - previous.PlayerRotation);
            var targetDistance = frame.PlayerPosition.HasValue && frame.TargetPosition.HasValue
                ? Vector3.Distance(frame.PlayerPosition.Value, frame.TargetPosition.Value)
                : (float?)null;
            var targetSurfaceDistance = targetDistance.HasValue
                ? MathF.Max(0, targetDistance.Value - frame.TargetRadius)
                : (float?)null;

            return new CombatHistoryMotionSnapshot(
                playerDelta,
                FiniteOrNull(step),
                FiniteOrNull(speed),
                FiniteOrNull(turn),
                FiniteOrNull(targetDistance),
                FiniteOrNull(targetSurfaceDistance));
        }

        private static float? FiniteOrNull(float? value)
        {
            return value.HasValue && float.IsFinite(value.Value) ? value.Value : null;
        }

        private static float NormalizeRadians(float value)
        {
            while (value > MathF.PI)
            {
                value -= MathF.Tau;
            }

            while (value < -MathF.PI)
            {
                value += MathF.Tau;
            }

            return value;
        }
    }

    private static string PluginVersion()
    {
        var assembly = typeof(CombatHistory).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
               assembly.GetName().Version?.ToString() ??
               "unknown";
    }
}
