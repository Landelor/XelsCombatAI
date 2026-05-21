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

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly BossModRuntimeGate gate;
    private ICallGateSubscriber<string, string?>? getPreset;
    private ICallGateSubscriber<string, bool, bool>? createPreset;
    private ICallGateSubscriber<string?>? getActivePreset;
    private ICallGateSubscriber<string, bool>? setActivePreset;
    private ICallGateSubscriber<bool>? clearActivePreset;
    private ICallGateSubscriber<uint, bool>? hasModuleByDataId;
    private ICallGateSubscriber<string, bool, bool>? disableModule;
    private ICallGateSubscriber<string, string, string, string, bool>? addTransientStrategy;
    private ICallGateSubscriber<string, string, string, bool>? clearTransientStrategy;
    private ICallGateSubscriber<string, bool>? clearTransientPresetStrategies;
    private ICallGateSubscriber<float>? nextDowntimeIn;
    private ICallGateSubscriber<float>? nextDowntimeEndIn;
    private DateTime nextFailureLog = DateTime.MinValue;

    public BossModIpc(IDalamudPluginInterface pluginInterface, IPluginLog log, BossModRuntimeGate gate)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.gate = gate;
    }

    public bool EnsurePreset()
    {
        if (!this.IsAvailable() ||
            this.getPreset is not { } getPreset ||
            this.createPreset is not { } createPreset)
        {
            return false;
        }

        var preset = this.Invoke(getPreset, () => getPreset.InvokeFunc(DefaultPresetName));
        if (preset != null && RequiredPresetModules.All(preset.Contains))
        {
            return true;
        }

        return this.Invoke(createPreset, () => createPreset.InvokeFunc(PresetPayload, true));
    }

    public bool IsIpcReady()
    {
        return this.EnsureSubscribers() && this.HasRequiredPresetFunctions();
    }

    public bool IsAvailable()
    {
        return this.gate.IsOpen && this.IsIpcReady();
    }

    public bool SetActive(string presetName)
    {
        if (!this.IsAvailable() || this.setActivePreset is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(presetName));
    }

    public string? GetActive()
    {
        if (!this.IsAvailable() || this.getActivePreset is not { } subscriber)
        {
            return null;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc());
    }

    public bool ClearActive()
    {
        if (!this.IsAvailable() || this.clearActivePreset is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc());
    }

    public bool SetPositional(string presetName, Positional positional)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.GoToPositional",
            "Positional",
            positional.ToString()));
    }

    public bool SetRange(string presetName, float range)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToTarget",
            "range",
            MathF.Round(range, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public bool SetMovement(string presetName, bool enabled)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Destination",
            enabled ? "Pathfind" : "None"));
    }

    public bool SetForbiddenZoneCushion(string presetName, string cushion)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "ForbiddenZoneCushion",
            cushion));
    }

    public bool SetMovementRangeStrategy(string presetName, string strategy)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Range",
            strategy));
    }

    public bool SetLeylinesBetweenTheLines(string presetName, bool enabled)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Between The Lines",
            enabled ? "Yes" : "No"));
    }

    public bool SetLeylinesRetrace(string presetName, bool enabled)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Retrace",
            enabled ? "Yes" : "No"));
    }

    public bool SetLeylinesGoal(string presetName, bool enabled)
    {
        if (!this.IsAvailable() || this.addTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Goal",
            enabled ? "Enabled" : "Disabled"));
    }

    public bool ClearTransientStrategy(string presetName, string moduleTypeName, string trackName)
    {
        if (!this.IsAvailable() || this.clearTransientStrategy is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(presetName, moduleTypeName, trackName));
    }

    public bool ClearTransientPresetStrategies(string presetName)
    {
        if (!this.IsAvailable() || this.clearTransientPresetStrategies is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(presetName));
    }

    public bool HasModuleByDataId(uint dataId)
    {
        if (!this.IsAvailable() || this.hasModuleByDataId is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(dataId));
    }

    public bool DisableModule(string moduleName, bool disabled)
    {
        if (!this.IsAvailable() || this.disableModule is not { } subscriber)
        {
            return false;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(moduleName, disabled));
    }

    public float NextDowntimeIn()
    {
        if (!this.IsAvailable() || this.nextDowntimeIn is not { } subscriber)
        {
            return float.MaxValue;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(), float.MaxValue);
    }

    public float NextDowntimeEndIn()
    {
        if (!this.IsAvailable() || this.nextDowntimeEndIn is not { } subscriber)
        {
            return float.MaxValue;
        }

        return this.Invoke(subscriber, () => subscriber.InvokeFunc(), float.MaxValue);
    }

    private bool Invoke(ICallGateSubscriber subscriber, Func<bool> action)
    {
        if (!this.CanInvoke(subscriber))
        {
            return false;
        }

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

    private string? Invoke(ICallGateSubscriber subscriber, Func<string?> action)
    {
        if (!this.CanInvoke(subscriber))
        {
            return null;
        }

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

    private float Invoke(ICallGateSubscriber subscriber, Func<float> action, float fallback)
    {
        if (!this.CanInvoke(subscriber))
        {
            return fallback;
        }

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

    private bool CanInvoke(ICallGateSubscriber subscriber)
    {
        if (!this.gate.IsOpen || !this.EnsureSubscribers())
        {
            return false;
        }

        try
        {
            return subscriber.HasFunction;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Could not check BossMod IPC readiness.");
            return false;
        }
    }

    private bool HasRequiredPresetFunctions()
    {
        try
        {
            return this.getPreset?.HasFunction == true &&
                   this.createPreset?.HasFunction == true &&
                   this.getActivePreset?.HasFunction == true &&
                   this.setActivePreset?.HasFunction == true &&
                   this.clearActivePreset?.HasFunction == true &&
                   this.addTransientStrategy?.HasFunction == true &&
                   this.clearTransientPresetStrategies?.HasFunction == true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Could not check BossMod IPC readiness.");
            return false;
        }
    }

    private bool EnsureSubscribers()
    {
        if (this.getPreset != null &&
            this.createPreset != null &&
            this.getActivePreset != null &&
            this.setActivePreset != null &&
            this.clearActivePreset != null &&
            this.hasModuleByDataId != null &&
            this.disableModule != null &&
            this.addTransientStrategy != null &&
            this.clearTransientStrategy != null &&
            this.clearTransientPresetStrategies != null &&
            this.nextDowntimeIn != null &&
            this.nextDowntimeEndIn != null)
        {
            return true;
        }

        try
        {
            this.getPreset = this.pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
            this.createPreset = this.pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
            this.getActivePreset = this.pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
            this.setActivePreset = this.pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
            this.clearActivePreset = this.pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
            this.hasModuleByDataId = this.pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId");
            this.disableModule = this.pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Configuration.DisableModule");
            this.addTransientStrategy = this.pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
            this.clearTransientStrategy = this.pluginInterface.GetIpcSubscriber<string, string, string, bool>("BossMod.Presets.ClearTransientStrategy");
            this.clearTransientPresetStrategies = this.pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.ClearTransientPresetStrategies");
            this.nextDowntimeIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeIn");
            this.nextDowntimeEndIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeEndIn");
            return true;
        }
        catch (Exception ex)
        {
            this.ClearSubscribers();
            this.LogRecoverableFailure(ex, "Could not create BossMod IPC subscribers.");
            return false;
        }
    }

    private void ClearSubscribers()
    {
        this.getPreset = null;
        this.createPreset = null;
        this.getActivePreset = null;
        this.setActivePreset = null;
        this.clearActivePreset = null;
        this.hasModuleByDataId = null;
        this.disableModule = null;
        this.addTransientStrategy = null;
        this.clearTransientStrategy = null;
        this.clearTransientPresetStrategies = null;
        this.nextDowntimeIn = null;
        this.nextDowntimeEndIn = null;
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
