using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModIpc
{
    public const string DefaultPresetName = "Xel's Combat AI";

    private const string PresetPayload = """
    {
      "Name": "Xel's Combat AI",
      "Modules": {
        "BossMod.Autorotation.MiscAI.StayCloseToTarget": [],
        "BossMod.Autorotation.MiscAI.StayWithinLeylines": [
          {
            "Track": "Use Between The Lines",
            "Option": "Yes"
          },
          {
            "Track": "Use Retrace",
            "Option": "Yes"
          }
        ],
        "BossMod.Autorotation.MiscAI.GoToPositional": [],
        "BossMod.Autorotation.MiscAI.NormalMovement": [
          {
            "Track": "Destination",
            "Option": "Pathfind"
          },
          {
            "Track": "ForbiddenZoneCushion",
            "Option": "Medium"
          }
        ]
      }
    }
    """;

    private static readonly string[] RequiredPresetModules =
    [
        "BossMod.Autorotation.MiscAI.StayCloseToTarget",
        "BossMod.Autorotation.MiscAI.StayWithinLeylines",
        "BossMod.Autorotation.MiscAI.GoToPositional",
        "BossMod.Autorotation.MiscAI.NormalMovement"
    ];

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string?> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ICallGateSubscriber<uint, bool> hasModuleByDataId;
    private readonly ICallGateSubscriber<string, bool, bool> disableModule;
    private readonly ICallGateSubscriber<string, string, string, string, bool> addTransientStrategy;
    private readonly ICallGateSubscriber<string, string, string, bool> clearTransientStrategy;
    private readonly ICallGateSubscriber<string, bool> clearTransientPresetStrategies;
    private readonly ICallGateSubscriber<float> nextDowntimeIn;
    private readonly ICallGateSubscriber<float> nextDowntimeEndIn;
    private readonly IPluginLog log;
    private DateTime nextFailureLog = DateTime.MinValue;

    public BossModIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.getPreset = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
        this.createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        this.getActivePreset = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
        this.setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        this.clearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        this.hasModuleByDataId = pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId");
        this.disableModule = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Configuration.DisableModule");
        this.addTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
        this.clearTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy");
        this.clearTransientPresetStrategies = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.ClearTransientPresetStrategies");
        this.nextDowntimeIn = pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeIn");
        this.nextDowntimeEndIn = pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeEndIn");
    }

    public bool EnsurePreset()
    {
        var preset = this.Invoke(() => this.getPreset.InvokeFunc(DefaultPresetName));
        if (preset != null && RequiredPresetModules.All(preset.Contains))
        {
            return true;
        }

        return this.Invoke(() => this.createPreset.InvokeFunc(PresetPayload, true));
    }

    public bool IsAvailable()
    {
        try
        {
            _ = this.getPreset.InvokeFunc(DefaultPresetName);
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod preset IPC is unavailable.");
            return false;
        }
    }

    public bool SetActive(string presetName) => this.Invoke(() => this.setActivePreset.InvokeFunc(presetName));

    public string? GetActive() => this.Invoke(() => this.getActivePreset.InvokeFunc());

    public bool ClearActive() => this.Invoke(() => this.clearActivePreset.InvokeFunc());

    public bool SetPositional(string presetName, Positional positional)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.GoToPositional",
            "Positional",
            positional.ToString()));
    }

    public bool SetRange(string presetName, float range)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToTarget",
            "range",
            MathF.Round(range, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public bool SetMovement(string presetName, bool enabled)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Destination",
            enabled ? "Pathfind" : "None"));
    }

    public bool SetForbiddenZoneCushion(string presetName, string cushion)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "ForbiddenZoneCushion",
            cushion));
    }

    public bool SetMovementRangeStrategy(string presetName, string strategy)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Range",
            strategy));
    }

    public bool SetLeylinesBetweenTheLines(string presetName, bool enabled)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Between The Lines",
            enabled ? "Yes" : "No"));
    }

    public bool SetLeylinesRetrace(string presetName, bool enabled)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Retrace",
            enabled ? "Yes" : "No"));
    }

    public bool SetLeylinesGoal(string presetName, bool enabled)
    {
        return this.Invoke(() => this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Goal",
            enabled ? "Enabled" : "Disabled"));
    }

    public bool ClearTransientStrategy(string presetName, string moduleTypeName, string trackName)
    {
        return this.Invoke(() => this.clearTransientStrategy.InvokeFunc(presetName, moduleTypeName, trackName));
    }

    public bool ClearTransientPresetStrategies(string presetName)
    {
        return this.Invoke(() => this.clearTransientPresetStrategies.InvokeFunc(presetName));
    }

    public bool HasModuleByDataId(uint dataId) => this.Invoke(() => this.hasModuleByDataId.InvokeFunc(dataId));

    public bool DisableModule(string moduleName, bool disabled) => this.Invoke(() => this.disableModule.InvokeFunc(moduleName, disabled));

    public float NextDowntimeIn() => this.Invoke(() => this.nextDowntimeIn.InvokeFunc(), float.MaxValue);

    public float NextDowntimeEndIn() => this.Invoke(() => this.nextDowntimeEndIn.InvokeFunc(), float.MaxValue);

    private bool Invoke(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return false;
        }
    }

    private string? Invoke(Func<string?> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return null;
        }
    }

    private float Invoke(Func<float> action, float fallback)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return fallback;
        }
    }

    private void LogRecoverableFailure(Exception ex, string message)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextFailureLog)
        {
            return;
        }

        this.log.Verbose(ex, message);
        this.nextFailureLog = now.AddSeconds(10);
    }
}
