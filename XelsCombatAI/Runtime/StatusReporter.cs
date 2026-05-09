using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace XelsCombatAI.Runtime;

internal static class StatusReporter
{
    public static string Build(RuntimeStatus status)
    {
        return $"Enabled={status.Enabled}, InCombat={status.InCombat}, Dead={status.IsDead}, Dependencies={(status.DependencyWarning ?? "OK")}, TrueNorthManagement={(status.TrueNorthWarning ?? status.RsrTrueNorthDisabled?.ToString() ?? "NotManaged")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={status.LastPositional}, TrueNorthCharges={status.TrueNorthCharges}, TrueNorthActive={status.TrueNorthActive}, TargetUptime={status.LastTargetUptimeRange:0.0}, Movement={status.LastMovement}, MovementRange={status.LastMovementRangeStrategy}, SafetyBuffer={status.LastForbiddenZoneCushion}, MovementSuppressed={status.AutomatedMovementSuppressed}, AoEPack={status.AoePackPositioning.LastReason}, HealerCoverage={status.HealerCoveragePositioning.LastReason}, Passage={status.PassageOfArmsPositioning.LastReason}, SurvZone={status.SurvivabilityZonePositioning.LastReason}, AggroSafety={status.AggroSafety.LastReason}, Initialized={status.InitializedPreset}";
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
        Append(builder, "EngagementRange", status.EngagementRange);
        Append(builder, "PackEngagementRange", status.PackAoeRange);
        Append(builder, "HasTarget", status.HasTarget);
        Append(builder, "TargetBaseId", status.TargetBaseId);
        Append(builder, "TargetObjectId", status.TargetObjectId);
        Append(builder, "PartyCount", status.PartyCount);
        Append(builder, "DependencyWarning", status.DependencyWarning ?? "OK");
        Append(builder, "TrueNorthWarning", status.TrueNorthWarning ?? "OK");
        Append(builder, "RsrTrueNorthDisabled", status.RsrTrueNorthDisabled);
        Append(builder, "InitializedPreset", status.InitializedPreset);
        Append(builder, "AutomatedMovementSuppressed", status.AutomatedMovementSuppressed);
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

        AppendSection(builder, "Gap Closer State");
        Append(builder, "GapCloserPLD", status.GapCloserPLD);
        Append(builder, "GapCloserWAR", status.GapCloserWAR);
        Append(builder, "GapCloserDRK", status.GapCloserDRK);
        Append(builder, "GapCloserGNB", status.GapCloserGNB);
        Append(builder, "GapCloserMNK", status.GapCloserMNK);
        Append(builder, "GapCloserDRG", status.GapCloserDRG);
        Append(builder, "GapCloserNIN", status.GapCloserNIN);
        Append(builder, "GapCloserSAM", status.GapCloserSAM);
        Append(builder, "GapCloserDNC", status.GapCloserDNC);
        Append(builder, "GapCloserRPR", status.GapCloserRPR);
        Append(builder, "GapCloserVPR", status.GapCloserVPR);
        Append(builder, "GapCloserWHM", status.GapCloserWHM);
        Append(builder, "EscapeGapCloserMNK", status.EscapeGapCloserMNK);
        Append(builder, "EscapeGapCloserNIN", status.EscapeGapCloserNIN);
        Append(builder, "EscapeGapCloserDNC", status.EscapeGapCloserDNC);
        Append(builder, "EscapeGapCloserRPR", status.EscapeGapCloserRPR);
        Append(builder, "EscapeGapCloserVPR", status.EscapeGapCloserVPR);
        Append(builder, "EscapeGapCloserWHM", status.EscapeGapCloserWHM);
        Append(builder, "EscapeGapCloserBLM", status.EscapeGapCloserBLM);
        Append(builder, "EscapeGapCloserSGE", status.EscapeGapCloserSGE);
        Append(builder, "EscapeGapCloserPCT", status.EscapeGapCloserPCT);
        Append(builder, "LastGapCloserSafety", status.LastGapCloserSafety);
        Append(builder, "LastEscapeGapCloserSafety", status.LastEscapeGapCloserSafety);
        Append(builder, "ReflectedGapSafety", status.ReflectedGapSafety);
        Append(builder, "ManualMovementInput", status.ManualMovementInput);
        builder.AppendLine();

        AppendSection(builder, "Reflection Diagnostics");
        Append(builder, "BossModSafety", status.ReflectedGapSafetyDiagnostics);
        Append(builder, "BossModGoalHook", status.AoeGoalHookDiagnostics);
        Append(builder, "RotationSolverAction", status.AoePackPositioning.RsrReflectionDiagnostics);
        Append(builder, "AoePackGoalMembers", status.AoePackPositioning.LastReason);
        Append(builder, "PassageGoalMembers", status.PassageOfArmsPositioning.LastReason);
        Append(builder, "HealerCoverageGoalMembers", status.HealerCoveragePositioning.LastReason);
        Append(builder, "SurvivabilityZoneGoalMembers", status.SurvivabilityZonePositioning.LastReason);
        Append(builder, "AggroSafetyGoalMembers", status.AggroSafety.LastReason);
        Append(builder, "BossFrontalCone", status.BossFrontalConeReason);
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

        AppendSection(builder, "Aggro Safety");
        Append(builder, "HookState", status.AggroSafety.HookState);
        Append(builder, "LastReason", status.AggroSafety.LastReason);
        Append(builder, "Injected", status.AggroSafety.Injected);
        Append(builder, "ActiveMobId", status.AggroSafety.ActiveMobId);
        Append(builder, "SelectedTankId", status.AggroSafety.SelectedTankId);
        Append(builder, "AggroSeconds", status.AggroSafety.AggroSeconds);
        Append(builder, "PriorityDevalued", status.AggroSafety.PriorityDevalued);
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
}
