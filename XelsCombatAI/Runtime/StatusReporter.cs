using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace XelsCombatAI.Runtime;

internal static class StatusReporter
{
    public static string Build(RuntimeStatus status)
    {
        return $"Enabled={status.Enabled}, InCombat={status.InCombat}, Dead={status.IsDead}, Dependencies={(status.DependencyWarning ?? "OK")}, TrueNorthManagement={(status.TrueNorthWarning ?? status.RsrTrueNorthDisabled?.ToString() ?? "NotManaged")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={status.LastPositional}, TrueNorthCharges={status.TrueNorthCharges}, TrueNorthActive={status.TrueNorthActive}, Range={status.LastRange:0.0}, Movement={status.LastMovement}, MovementRange={status.LastMovementRangeStrategy}, Cushion={status.LastForbiddenZoneCushion}, Role={status.LastPartyRole}, MovementSuppressed={status.AutomatedMovementSuppressed}, Initialized={status.InitializedPreset}";
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
        Append(builder, "HasTarget", status.HasTarget);
        Append(builder, "TargetBaseId", status.TargetBaseId);
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
        Append(builder, "LastRange", status.LastRange);
        Append(builder, "LastMovement", status.LastMovement);
        Append(builder, "LastMovementRangeStrategy", status.LastMovementRangeStrategy);
        Append(builder, "LastForbiddenZoneCushion", status.LastForbiddenZoneCushion);
        Append(builder, "LastPartyRole", status.LastPartyRole);
        Append(builder, "LastLeylinesBetweenTheLines", status.LastLeylinesBetweenTheLines);
        Append(builder, "LastLeylinesRetrace", status.LastLeylinesRetrace);
        Append(builder, "LastLeylinesGoal", status.LastLeylinesGoal);
        Append(builder, "LastHealerStayNearParty", status.LastHealerStayNearParty);
        Append(builder, "LastHealerHeal", status.LastHealerHeal);
        Append(builder, "LastHealerEsuna", status.LastHealerEsuna);
        Append(builder, "LastHealerOutOfCombat", status.LastHealerOutOfCombat);
        Append(builder, "LastHealerRaise", status.LastHealerRaise);
        Append(builder, "LastMonkThunderclap", status.LastMonkThunderclap);
        Append(builder, "LastDragoonWingedGlide", status.LastDragoonWingedGlide);
        Append(builder, "LastNinjaShukuchi", status.LastNinjaShukuchi);
        Append(builder, "LastViperSlither", status.LastViperSlither);
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
        Append(builder, "EscapeGapCloserMNK", status.EscapeGapCloserMNK);
        Append(builder, "EscapeGapCloserNIN", status.EscapeGapCloserNIN);
        Append(builder, "EscapeGapCloserDNC", status.EscapeGapCloserDNC);
        Append(builder, "EscapeGapCloserRPR", status.EscapeGapCloserRPR);
        Append(builder, "EscapeGapCloserVPR", status.EscapeGapCloserVPR);
        Append(builder, "EscapeGapCloserBLM", status.EscapeGapCloserBLM);
        Append(builder, "EscapeGapCloserSGE", status.EscapeGapCloserSGE);
        Append(builder, "EscapeGapCloserPCT", status.EscapeGapCloserPCT);
        Append(builder, "EscapeGapCloserBLU", status.EscapeGapCloserBLU);
        Append(builder, "LastGapCloserSafety", status.LastGapCloserSafety);
        Append(builder, "LastEscapeGapCloserSafety", status.LastEscapeGapCloserSafety);
        Append(builder, "ReflectedGapSafety", status.ReflectedGapSafety);
        Append(builder, "ManualMovementInput", status.ManualMovementInput);
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
