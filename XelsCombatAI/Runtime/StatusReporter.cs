using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace XelsCombatAI.Runtime;

internal static class StatusReporter
{
    public static string Build(RuntimeStatus status)
    {
        return $"Enabled={status.Enabled}, InCombat={status.InCombat}, Dead={status.IsDead}, Dependencies={(status.DependencyWarning ?? "OK")}, TrueNorthManagement={(status.TrueNorthWarning ?? status.RsrTrueNorthDisabled?.ToString() ?? "NotManaged")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={status.LastPositional}, TrueNorthCharges={status.TrueNorthCharges}, TrueNorthActive={status.TrueNorthActive}, BmrRange={status.LastTargetUptimeRange:0.0}, Movement={status.LastMovement}, MovementRange={status.LastMovementRangeStrategy}, SafetyBuffer={status.LastForbiddenZoneCushion}, MovementSuppressed={status.AutomatedMovementSuppressed}, Facing={FormatFacingSummary(status.Facing)}, Mobility={status.MobilityDecision.State}/{status.MobilityDecision.IntentLabel}, TrashPull={status.AoePackPositioning.TrashPull.Phase}/{status.AoePackPositioning.TrashPull.Reason}, AoEPack={status.AoePackPositioning.LastReason}, HealerCoverage={status.HealerCoveragePositioning.LastReason}, Passage={status.PassageOfArmsPositioning.LastReason}, SurvZone={status.SurvivabilityZonePositioning.LastReason}, RedMageMelee={status.RedMageMeleeCombo.Mode}/{status.RedMageMeleeCombo.LastReason}, Initialized={status.InitializedPreset}";
    }

    public static string BuildDebug(Configuration config, RuntimeStatus status)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Xel's Combat AI debug state");
        builder.AppendLine($"GeneratedUtc={DateTime.UtcNow:O}");
        builder.AppendLine($"Preset={BossModIpc.DefaultPresetName}");
        builder.AppendLine();

        AppendSection(builder, "Runtime");
        Append(builder, "Enabled", status.Enabled);
        Append(builder, "InCombat", status.InCombat);
        Append(builder, "IsDead", status.IsDead);
        Append(builder, "PlayerClassJobId", status.PlayerClassJobId);
        Append(builder, "TerritoryType", status.TerritoryType);
        Append(builder, "ContentFinderConditionId", status.ContentFinderConditionId);
        Append(builder, "EngagementRange", status.EngagementRange);
        Append(builder, "PackEngagementRange", status.PackAoeRange);
        Append(builder, "HasTarget", status.HasTarget);
        Append(builder, "TargetBaseId", status.TargetBaseId);
        Append(builder, "TargetObjectId", status.TargetObjectId);
        Append(builder, "PartyCount", status.PartyCount);
        Append(builder, "DependencyWarning", status.DependencyWarning ?? "OK");
        Append(builder, "TrueNorthWarning", status.TrueNorthWarning ?? "OK");
        Append(builder, "RsrTrueNorthDisabled", status.RsrTrueNorthDisabled);
        Append(builder, "RotationSolverIpcDiagnostics", status.RotationSolverIpcDiagnostics);
        Append(builder, "ReflectionHealth", BuildReflectionHealthSummary(status));
        Append(builder, "InitializedPreset", status.InitializedPreset);
        Append(builder, "AutomatedMovementSuppressed", status.AutomatedMovementSuppressed);
        builder.AppendLine();

        AppendSection(builder, "Facing State");
        Append(builder, "ManageSocialTurning", config.ManageSocialTurning);
        Append(builder, "Source", status.Facing.Source);
        Append(builder, "Reason", status.Facing.Reason);
        Append(builder, "DesiredRotation", status.Facing.DesiredRotation);
        Append(builder, "CurrentRotation", status.Facing.CurrentRotation);
        Append(builder, "DeltaRadians", status.Facing.DeltaRadians);
        Append(builder, "Applied", status.Facing.Applied);
        Append(builder, "RejectionReason", status.Facing.RejectionReason);
        Append(builder, "ConsensusMembers", status.Facing.ConsensusMembers);
        builder.AppendLine();

        AppendSection(builder, "BossMod Strategy Cache");
        Append(builder, "LastPositional", status.LastPositional);
        Append(builder, "TrueNorthCharges", status.TrueNorthCharges);
        Append(builder, "TrueNorthActive", status.TrueNorthActive);
        Append(builder, "LastTargetUptimeRange", status.LastTargetUptimeRange);
        Append(builder, "LastMovement", status.LastMovement);
        Append(builder, "LastMovementRangeStrategy", status.LastMovementRangeStrategy);
        Append(builder, "LastSafetyBuffer", status.LastForbiddenZoneCushion);
        Append(builder, "LastLeylinesBetweenTheLines", status.LastLeylinesBetweenTheLines);
        Append(builder, "LastLeylinesRetrace", status.LastLeylinesRetrace);
        Append(builder, "LastLeylinesGoal", status.LastLeylinesGoal);
        builder.AppendLine();

        AppendSection(builder, "Job Specific State");
        Append(builder, "UseRedMageMeleeComboMovement", config.UseRedMageMeleeComboMovement);
        Append(builder, "RedMageMeleeEnabled", status.RedMageMeleeCombo.Enabled);
        Append(builder, "RedMageMeleeMode", status.RedMageMeleeCombo.Mode);
        Append(builder, "RedMageMeleeReason", status.RedMageMeleeCombo.LastReason);
        Append(builder, "RedMageWhiteMana", status.RedMageMeleeCombo.WhiteMana);
        Append(builder, "RedMageBlackMana", status.RedMageMeleeCombo.BlackMana);
        Append(builder, "RedMageManaStacks", status.RedMageMeleeCombo.ManaStacks);
        Append(builder, "RedMageNextAction", $"{status.RedMageMeleeCombo.NextActionName} ({status.RedMageMeleeCombo.NextActionId})");
        Append(builder, "RedMageAffectedTargets", status.RedMageMeleeCombo.AffectedTargets);
        Append(builder, "RedMageCandidateDestination", status.RedMageMeleeCombo.CandidateDestination);
        Append(builder, "RedMageLastJumpLanding", status.RedMageMeleeCombo.LastJumpLanding);
        builder.AppendLine();

        AppendSection(builder, "Gap Closer State");
        Append(builder, "GapCloserPLD", status.GapCloserPLD);
        Append(builder, "GapCloserWAR", status.GapCloserWAR);
        Append(builder, "GapCloserDRK", status.GapCloserDRK);
        Append(builder, "GapCloserGNB", status.GapCloserGNB);
        Append(builder, "GapCloserMNK", status.GapCloserMNK);
        Append(builder, "GapCloserDRG", status.GapCloserDRG);
        Append(builder, "GapCloserBRD", status.GapCloserBRD);
        Append(builder, "GapCloserNIN", status.GapCloserNIN);
        Append(builder, "GapCloserSAM", status.GapCloserSAM);
        Append(builder, "GapCloserDNC", status.GapCloserDNC);
        Append(builder, "GapCloserRPR", status.GapCloserRPR);
        Append(builder, "GapCloserVPR", status.GapCloserVPR);
        Append(builder, "GapCloserWHM", status.GapCloserWHM);
        Append(builder, "GapCloserBLM", status.GapCloserBLM);
        Append(builder, "GapCloserRDM", status.GapCloserRDM);
        Append(builder, "GapCloserSGE", status.GapCloserSGE);
        Append(builder, "GapCloserPCT", status.GapCloserPCT);
        Append(builder, "GreedDashStyleActive", status.GreedDashStyleActive);
        Append(builder, "LastDashStyleReason", status.LastDashStyleReason);
        Append(builder, "GreedyUnsafeEscapeDashes", status.GreedyUnsafeEscapeDashes);
        Append(builder, "LastGapCloserSafety", status.LastGapCloserSafety);
        Append(builder, "LastEscapeGapCloserSafety", status.LastEscapeGapCloserSafety);
        Append(builder, "LastEscapeLanding", status.LastEscapeLanding);
        Append(builder, "MobilityState", status.MobilityDecision.State);
        Append(builder, "MobilityIntent", status.MobilityDecision.IntentLabel);
        Append(builder, "MobilityAction", status.MobilityDecision.ActionName);
        Append(builder, "MobilityDestination", status.MobilityDecision.Destination);
        Append(builder, "MobilitySafetyGain", status.MobilityDecision.SafetyGain);
        Append(builder, "MobilityUptimeGain", status.MobilityDecision.UptimeGain);
        Append(builder, "MobilityPathGain", status.MobilityDecision.PathGain);
        Append(builder, "MobilitySafetyReason", status.MobilityDecision.SafetyReason);
        Append(builder, "MobilityUptimeReason", status.MobilityDecision.UptimeReason);
        Append(builder, "MobilityPathReason", status.MobilityDecision.PathReason);
        Append(builder, "MobilityRiskReason", status.MobilityDecision.RiskReason);
        Append(builder, "ReflectedGapSafety", status.ReflectedGapSafety);
        Append(builder, "ManualMovementInput", status.ManualMovementInput);
        builder.AppendLine();

        AppendSection(builder, "Reflection Diagnostics");
        Append(builder, "BossModSafety", status.ReflectedGapSafetyDiagnostics);
        Append(builder, "BossModGoalHook", status.AoeGoalHookDiagnostics);
        Append(builder, "BossModActiveModule", status.BossModMovement.ActiveModule);
        Append(builder, "BossModActiveZoneModule", status.BossModMovement.ActiveZoneModule);
        Append(builder, "BossModNavigationDestination", status.BossModMovement.NavigationDestination);
        Append(builder, "BossModNavigationNextWaypoint", status.BossModMovement.NavigationNextWaypoint);
        Append(builder, "BossModNavigationStats", status.BossModMovement.NavigationStats);
        Append(builder, "BossModPlannerSteer", status.BossModMovement.PlannerSteer);
        Append(builder, "BossModControllerTarget", status.BossModMovement.ControllerTarget);
        Append(builder, "BossModMovementOverride", status.BossModMovement.MovementOverride);
        Append(builder, "BossModHintSummary", status.BossModMovement.HintSummary);
        Append(builder, "RotationSolverIpc", status.RotationSolverIpcDiagnostics);
        Append(builder, "RotationSolverAction", status.AoePackPositioning.RsrReflectionDiagnostics);
        Append(builder, "RotationSolverRedMageMelee", status.RotationSolverRedMageMeleeDiagnostics);
        Append(builder, "AoePackGoalMembers", status.AoePackPositioning.LastReason);
        Append(builder, "PassageGoalMembers", status.PassageOfArmsPositioning.LastReason);
        Append(builder, "HealerCoverageGoalMembers", status.HealerCoveragePositioning.LastReason);
        Append(builder, "SurvivabilityZoneGoalMembers", status.SurvivabilityZonePositioning.LastReason);
        Append(builder, "ArenaEdge", status.ArenaEdgeReason);
        builder.AppendLine();

        AppendSection(builder, "AoE Pack Positioning");
        Append(builder, "AoeGoalHook", status.AoeGoalHook);
        Append(builder, "HookState", status.AoePackPositioning.HookState);
        Append(builder, "LastReason", status.AoePackPositioning.LastReason);
        Append(builder, "RsrStatus", status.AoePackPositioning.RsrStatus);
        Append(builder, "RsrReflectionDiagnostics", status.AoePackPositioning.RsrReflectionDiagnostics);
        Append(builder, "ActionId", status.AoePackPositioning.ActionId);
        Append(builder, "ActionName", status.AoePackPositioning.ActionName);
        Append(builder, "Shape", status.AoePackPositioning.Shape);
        Append(builder, "CurrentHits", status.AoePackPositioning.CurrentHits);
        Append(builder, "BestHits", status.AoePackPositioning.BestHits);
        Append(builder, "Injected", status.AoePackPositioning.Injected);
        Append(builder, "RsrHenchedActive", status.AoePackPositioning.RsrHenchedActive);
        Append(builder, "RsrSnapshotMode", status.AoePackPositioning.RsrSnapshotMode);
        Append(builder, "RsrLastRestore", status.AoePackPositioning.RsrLastRestoreStatus);
        Append(builder, "PriorityTargetCount", status.AoePackPositioning.PriorityTargetCount);
        builder.AppendLine();

        AppendSection(builder, "Trash Pull Cognition");
        Append(builder, "Phase", status.AoePackPositioning.TrashPull.Phase);
        Append(builder, "Confidence", status.AoePackPositioning.TrashPull.Confidence);
        Append(builder, "Reason", status.AoePackPositioning.TrashPull.Reason);
        Append(builder, "TankObjectId", status.AoePackPositioning.TrashPull.TankObjectId);
        Append(builder, "TankPosition", status.AoePackPositioning.TrashPull.TankPosition);
        Append(builder, "TankSpeed", status.AoePackPositioning.TrashPull.TankSpeed);
        Append(builder, "ProjectedTankPosition", status.AoePackPositioning.TrashPull.ProjectedTankPosition);
        Append(builder, "PackCentroid", status.AoePackPositioning.TrashPull.PackCentroid);
        Append(builder, "PackSpeed", status.AoePackPositioning.TrashPull.PackSpeed);
        Append(builder, "PartyMedianSpeed", status.AoePackPositioning.TrashPull.PartyMedianSpeed);
        Append(builder, "DominantTargetCount", status.AoePackPositioning.TrashPull.DominantTargetCount);
        Append(builder, "StragglerTargetCount", status.AoePackPositioning.TrashPull.StragglerTargetCount);
        builder.AppendLine();

        AppendSection(builder, "Passage of Arms Positioning");
        Append(builder, "HookState", status.PassageOfArmsPositioning.HookState);
        Append(builder, "LastReason", status.PassageOfArmsPositioning.LastReason);
        Append(builder, "Injected", status.PassageOfArmsPositioning.Injected);
        Append(builder, "PaladinName", status.PassageOfArmsPositioning.PaladinName);
        Append(builder, "DistanceToPreferred", status.PassageOfArmsPositioning.DistanceToPreferred);
        Append(builder, "PlayerInCone", status.PassageOfArmsPositioning.PlayerInCone);
        builder.AppendLine();

        AppendSection(builder, "Healer Coverage Zone");
        Append(builder, "HookState", status.HealerCoveragePositioning.HookState);
        Append(builder, "LastReason", status.HealerCoveragePositioning.LastReason);
        Append(builder, "Injected", status.HealerCoveragePositioning.Injected);
        Append(builder, "PartyMembers", status.HealerCoveragePositioning.PartyMembers);
        Append(builder, "CoveredMembers", status.HealerCoveragePositioning.CoveredMembers);
        Append(builder, "DistanceToCoveragePosition", status.HealerCoveragePositioning.DistanceToCenter);
        builder.AppendLine();

        AppendSection(builder, "Survivability Zone Positioning");
        Append(builder, "HookState", status.SurvivabilityZonePositioning.HookState);
        Append(builder, "LastReason", status.SurvivabilityZonePositioning.LastReason);
        Append(builder, "Injected", status.SurvivabilityZonePositioning.Injected);
        Append(builder, "ZoneName", status.SurvivabilityZonePositioning.ZoneName);
        Append(builder, "CasterName", status.SurvivabilityZonePositioning.CasterName);
        Append(builder, "DistanceToCenter", status.SurvivabilityZonePositioning.DistanceToCenter);
        Append(builder, "Diagnostics", status.SurvivabilityZonePositioning.Diagnostics);
        builder.AppendLine();

        AppendSection(builder, "Configuration");
        foreach (var property in typeof(Configuration).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            Append(builder, property.Name, property.GetValue(config));
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title)
    {
        builder.AppendLine($"[{title}]");
    }

    private static void Append(StringBuilder builder, string name, object? value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.AppendLine(FormatValue(value));
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            float floatValue => floatValue.ToString("0.###", CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string FormatFacingSummary(FacingStatus facing)
    {
        var source = facing.Source?.ToString() ?? "None";
        var state = facing.Applied
            ? "applied"
            : string.IsNullOrEmpty(facing.RejectionReason)
                ? "ready"
                : facing.RejectionReason;
        return $"{source}/{facing.Reason}/{state}";
    }

    private static string BuildReflectionHealthSummary(RuntimeStatus status)
    {
        var issues = new List<string>();
        AddReflectionIssue(issues, "BossModSafety", status.ReflectedGapSafetyDiagnostics);
        AddReflectionIssue(issues, "BossModGoalHook", status.AoeGoalHookDiagnostics);
        AddReflectionIssue(issues, "BossModMovementDiagnostics", status.BossModMovement.NavigationStats);
        AddReflectionIssue(issues, "BossModHintDiagnostics", status.BossModMovement.HintSummary);
        AddReflectionIssue(issues, "RotationSolverIpc", status.RotationSolverIpcDiagnostics);
        AddReflectionIssue(issues, "RotationSolverAction", status.AoePackPositioning.RsrReflectionDiagnostics);
        AddReflectionIssue(issues, "RotationSolverRedMageMelee", status.RotationSolverRedMageMeleeDiagnostics);
        AddReflectionIssue(issues, "AoePackGoal", status.AoePackPositioning.LastReason);
        AddReflectionIssue(issues, "PassageGoal", status.PassageOfArmsPositioning.LastReason);
        AddReflectionIssue(issues, "HealerCoverageGoal", status.HealerCoveragePositioning.LastReason);
        AddReflectionIssue(issues, "SurvivabilityZoneGoal", status.SurvivabilityZonePositioning.LastReason);
        AddReflectionIssue(issues, "ArenaEdgeGoal", status.ArenaEdgeReason);
        return issues.Count == 0 ? "OK" : string.Join(" | ", issues);
    }

    private static void AddReflectionIssue(ICollection<string> issues, string name, string diagnostics)
    {
        var lower = diagnostics.ToLowerInvariant();
        if (lower.Contains("broken", StringComparison.Ordinal) ||
            lower.Contains("missing ", StringComparison.Ordinal) ||
            lower.Contains("not found", StringComparison.Ordinal) ||
            lower.Contains("members unavailable", StringComparison.Ordinal) ||
            lower.Contains("types unavailable", StringComparison.Ordinal) ||
            lower.Contains("resolve failed", StringComparison.Ordinal) ||
            lower.Contains("diagnostics failed", StringComparison.Ordinal) ||
            lower.Contains("capture failed", StringComparison.Ordinal) ||
            lower.Contains("query failed", StringComparison.Ordinal))
        {
            issues.Add(name);
        }
    }

}
