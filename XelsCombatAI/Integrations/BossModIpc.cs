using System;
using System.Linq;
using System.Numerics;
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
    private ICallGateSubscriber<float>? nextRaidwideIn;
    private ICallGateSubscriber<float>? nextTankbusterIn;
    private ICallGateSubscriber<float>? nextKnockbackIn;
    private ICallGateSubscriber<float>? nextDowntimeIn;
    private ICallGateSubscriber<float>? nextDowntimeEndIn;
    private ICallGateSubscriber<float>? nextVulnerableIn;
    private ICallGateSubscriber<float>? nextVulnerableEndIn;
    private ICallGateSubscriber<float>? nextDamageIn;
    private ICallGateSubscriber<int>? nextDamageType;
    private ICallGateSubscriber<float>? nextRaidwideDamageIn;
    private ICallGateSubscriber<float>? nextTankbusterDamageIn;
    private ICallGateSubscriber<float>? specialModeIn;
    private ICallGateSubscriber<int>? specialModeType;
    private ICallGateSubscriber<bool>? hasActiveModule;
    private ICallGateSubscriber<string?>? activeModuleName;
    private ICallGateSubscriber<Vector3, bool>? isPositionSafe;
    private ICallGateSubscriber<Vector3, Vector3, bool>? isDashSafe;
    private ICallGateSubscriber<float, bool, bool>? isFixedDashSafe;
    private ICallGateSubscriber<Vector3, float, bool>? isBackdashSafe;
    private ICallGateSubscriber<Vector3?>? navigationTargetPos;
    private ICallGateSubscriber<int>? forbiddenZonesCount;
    private ICallGateSubscriber<int>? forbiddenDirectionsCount;
    private ICallGateSubscriber<string?>? timelineDebug;
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

    /// <summary>
    /// Reads whether BossMod Reborn currently has an active module, independent of
    /// XCAI's own enable state or preset lifecycle. Used only to decide whether XCAI
    /// should automatically enable/disable itself; never gates movement or preset writes.
    /// </summary>
    public bool TryGetHasActiveModule(out bool hasActiveModule)
    {
        hasActiveModule = false;
        if (!this.EnsureSubscribers() || this.hasActiveModule is not { } subscriber)
        {
            return false;
        }

        try
        {
            if (!subscriber.HasFunction)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "Could not check BossMod active-module IPC readiness.");
            return false;
        }

        hasActiveModule = this.Invoke(subscriber, () => subscriber.InvokeFunc());
        return true;
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

    public BossModMechanicPressure GetMechanicPressure()
    {
        if (!this.IsAvailable())
        {
            return BossModMechanicPressure.None;
        }

        return new BossModMechanicPressure(
            this.InvokeOptional(this.nextRaidwideIn, float.MaxValue),
            this.InvokeOptional(this.nextTankbusterIn, float.MaxValue),
            this.InvokeOptional(this.nextKnockbackIn, float.MaxValue),
            this.InvokeOptional(this.nextDamageIn, float.MaxValue),
            this.InvokeOptional(this.nextDowntimeIn, float.MaxValue),
            this.InvokeOptional(this.nextDowntimeEndIn, float.MaxValue),
            this.InvokeOptional(this.nextVulnerableIn, float.MaxValue),
            this.InvokeOptional(this.nextVulnerableEndIn, float.MaxValue),
            this.InvokeOptional(this.nextRaidwideDamageIn, float.MaxValue),
            this.InvokeOptional(this.nextTankbusterDamageIn, float.MaxValue),
            this.InvokeOptional(this.nextDamageType, 0),
            this.InvokeOptional(this.specialModeIn, float.MaxValue),
            this.InvokeOptional(this.specialModeType, 0),
            this.InvokeOptional(this.hasActiveModule, false),
            this.InvokeOptional(this.activeModuleName, null),
            this.InvokeOptional(this.timelineDebug, "<unavailable>") ?? "<unavailable>",
            DateTime.MinValue);
    }

    public bool TryIsPositionSafe(Vector3 position, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;
        if (!this.IsAvailable() || this.isPositionSafe is not { } subscriber)
        {
            reason = "BMR IPC position safety unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(position), out safe))
        {
            reason = safe ? "BMR IPC position safe" : "BMR IPC reports position dangerous";
            return true;
        }

        reason = "BMR IPC position safety failed";
        return false;
    }

    public bool TryIsDashSafe(Vector3 from, Vector3 to, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;
        if (!this.IsAvailable() || this.isDashSafe is not { } subscriber)
        {
            reason = "BMR IPC dash safety unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(from, to), out safe))
        {
            reason = safe ? "BMR IPC dash safe" : "BMR IPC reports dash dangerous";
            return true;
        }

        reason = "BMR IPC dash safety failed";
        return false;
    }

    public bool TryIsFixedDashSafe(float range, bool backwards, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;
        if (!this.IsAvailable() || this.isFixedDashSafe is not { } subscriber)
        {
            reason = "BMR IPC fixed dash safety unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(range, backwards), out safe))
        {
            reason = safe ? "BMR IPC fixed dash safe" : "BMR IPC reports fixed dash dangerous";
            return true;
        }

        reason = "BMR IPC fixed dash safety failed";
        return false;
    }

    public bool TryIsBackdashSafe(Vector3 enemyPosition, float range, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;
        if (!this.IsAvailable() || this.isBackdashSafe is not { } subscriber)
        {
            reason = "BMR IPC backdash safety unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(enemyPosition, range), out safe))
        {
            reason = safe ? "BMR IPC backdash safe" : "BMR IPC reports backdash dangerous";
            return true;
        }

        reason = "BMR IPC backdash safety failed";
        return false;
    }

    public bool TryGetNavigationTarget(out Vector3 destination, out string reason)
    {
        destination = default;
        reason = string.Empty;
        if (!this.IsAvailable() || this.navigationTargetPos is not { } subscriber)
        {
            reason = "BMR IPC navigation target unavailable";
            return false;
        }

        if (!this.TryInvoke(subscriber, () => subscriber.InvokeFunc(), out var ipcDestination))
        {
            reason = "BMR IPC navigation target failed";
            return false;
        }

        if (!ipcDestination.HasValue)
        {
            reason = "BMR IPC navigation target not set";
            return false;
        }

        destination = ipcDestination.Value;
        reason = "BMR IPC navigation target available";
        return true;
    }

    public bool TryGetForbiddenZoneCount(out int count, out string reason)
    {
        count = 0;
        reason = string.Empty;
        if (!this.IsAvailable() || this.forbiddenZonesCount is not { } subscriber)
        {
            reason = "BMR IPC forbidden zone count unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(), out count))
        {
            reason = "BMR IPC forbidden zone count available";
            return true;
        }

        reason = "BMR IPC forbidden zone count failed";
        return false;
    }

    public bool TryGetForbiddenDirectionCount(out int count, out string reason)
    {
        count = 0;
        reason = string.Empty;
        if (!this.IsAvailable() || this.forbiddenDirectionsCount is not { } subscriber)
        {
            reason = "BMR IPC forbidden direction count unavailable";
            return false;
        }

        if (this.TryInvoke(subscriber, () => subscriber.InvokeFunc(), out count))
        {
            reason = "BMR IPC forbidden direction count available";
            return true;
        }

        reason = "BMR IPC forbidden direction count failed";
        return false;
    }

    public bool TryGetSpecialMode(out BossModSpecialMode mode, out float activationIn, out string reason)
    {
        mode = BossModSpecialMode.Normal;
        activationIn = float.MaxValue;
        reason = string.Empty;
        if (!this.IsAvailable())
        {
            reason = "BMR IPC special mode unavailable";
            return false;
        }

        var specialModeType = this.specialModeType;
        var specialModeIn = this.specialModeIn;
        if (specialModeType == null ||
            specialModeIn == null ||
            !this.TryInvoke(specialModeType, () => specialModeType.InvokeFunc(), out var modeValue) ||
            !this.TryInvoke(specialModeIn, () => specialModeIn.InvokeFunc(), out activationIn))
        {
            reason = "BMR IPC special mode failed";
            return false;
        }

        mode = Enum.IsDefined(typeof(BossModSpecialMode), modeValue)
            ? (BossModSpecialMode)modeValue
            : BossModSpecialMode.Normal;
        reason = "BMR IPC special mode available";
        return true;
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

    private float InvokeOptional(ICallGateSubscriber<float>? subscriber, float fallback)
    {
        return subscriber == null
            ? fallback
            : this.Invoke(subscriber, () => subscriber.InvokeFunc(), fallback);
    }

    private int InvokeOptional(ICallGateSubscriber<int>? subscriber, int fallback)
    {
        return subscriber == null
            ? fallback
            : this.Invoke(subscriber, () => subscriber.InvokeFunc(), fallback);
    }

    private bool InvokeOptional(ICallGateSubscriber<bool>? subscriber, bool fallback)
    {
        return subscriber == null
            ? fallback
            : this.Invoke(subscriber, () => subscriber.InvokeFunc());
    }

    private string? InvokeOptional(ICallGateSubscriber<string?>? subscriber, string? fallback)
    {
        return subscriber == null
            ? fallback
            : this.Invoke(subscriber, () => subscriber.InvokeFunc()) ?? fallback;
    }

    private int Invoke(ICallGateSubscriber subscriber, Func<int> action, int fallback)
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

    private bool TryInvoke(ICallGateSubscriber subscriber, Func<bool> action, out bool result)
    {
        result = false;
        if (!this.CanInvoke(subscriber))
        {
            return false;
        }

        try
        {
            result = action();
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return false;
        }
    }

    private bool TryInvoke(ICallGateSubscriber subscriber, Func<int> action, out int result)
    {
        result = 0;
        if (!this.CanInvoke(subscriber))
        {
            return false;
        }

        try
        {
            result = action();
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return false;
        }
    }

    private bool TryInvoke(ICallGateSubscriber subscriber, Func<float> action, out float result)
    {
        result = float.MaxValue;
        if (!this.CanInvoke(subscriber))
        {
            return false;
        }

        try
        {
            result = action();
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return false;
        }
    }

    private bool TryInvoke(ICallGateSubscriber subscriber, Func<Vector3?> action, out Vector3? result)
    {
        result = null;
        if (!this.CanInvoke(subscriber))
        {
            return false;
        }

        try
        {
            result = action();
            return true;
        }
        catch (Exception ex)
        {
            this.LogRecoverableFailure(ex, "BossMod IPC invocation failed.");
            return false;
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
            this.nextRaidwideIn != null &&
            this.nextTankbusterIn != null &&
            this.nextKnockbackIn != null &&
            this.nextDowntimeIn != null &&
            this.nextDowntimeEndIn != null &&
            this.nextVulnerableIn != null &&
            this.nextVulnerableEndIn != null &&
            this.nextDamageIn != null &&
            this.nextDamageType != null &&
            this.nextRaidwideDamageIn != null &&
            this.nextTankbusterDamageIn != null &&
            this.specialModeIn != null &&
            this.specialModeType != null &&
            this.hasActiveModule != null &&
            this.activeModuleName != null &&
            this.isPositionSafe != null &&
            this.isDashSafe != null &&
            this.isFixedDashSafe != null &&
            this.isBackdashSafe != null &&
            this.navigationTargetPos != null &&
            this.forbiddenZonesCount != null &&
            this.forbiddenDirectionsCount != null &&
            this.timelineDebug != null)
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
            this.nextRaidwideIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextRaidwideIn");
            this.nextTankbusterIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextTankbusterIn");
            this.nextKnockbackIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextKnockbackIn");
            this.nextDowntimeIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeIn");
            this.nextDowntimeEndIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextDowntimeEndIn");
            this.nextVulnerableIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextVulnerableIn");
            this.nextVulnerableEndIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextVulnerableEndIn");
            this.nextDamageIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.NextDamageIn");
            this.nextDamageType = this.pluginInterface.GetIpcSubscriber<int>("BossMod.Hints.NextDamageType");
            this.nextRaidwideDamageIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.NextRaidwideDamageIn");
            this.nextTankbusterDamageIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.NextTankbusterDamageIn");
            this.specialModeIn = this.pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.SpecialModeIn");
            this.specialModeType = this.pluginInterface.GetIpcSubscriber<int>("BossMod.Hints.SpecialModeType");
            this.hasActiveModule = this.pluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
            this.activeModuleName = this.pluginInterface.GetIpcSubscriber<string?>("BossMod.ActiveModuleName");
            this.isPositionSafe = this.pluginInterface.GetIpcSubscriber<Vector3, bool>("BossMod.Hints.IsPositionSafe");
            this.isDashSafe = this.pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
            this.isFixedDashSafe = this.pluginInterface.GetIpcSubscriber<float, bool, bool>("BossMod.Hints.IsFixedDashSafe");
            this.isBackdashSafe = this.pluginInterface.GetIpcSubscriber<Vector3, float, bool>("BossMod.Hints.IsBackdashSafe");
            this.navigationTargetPos = this.pluginInterface.GetIpcSubscriber<Vector3?>("BossMod.AI.NaviTargetPos");
            this.forbiddenZonesCount = this.pluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");
            this.forbiddenDirectionsCount = this.pluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenDirectionsCount");
            this.timelineDebug = this.pluginInterface.GetIpcSubscriber<string?>("BossMod.Debug.TimelineWalk");
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
        this.nextRaidwideIn = null;
        this.nextTankbusterIn = null;
        this.nextKnockbackIn = null;
        this.nextDowntimeIn = null;
        this.nextDowntimeEndIn = null;
        this.nextVulnerableIn = null;
        this.nextVulnerableEndIn = null;
        this.nextDamageIn = null;
        this.nextDamageType = null;
        this.nextRaidwideDamageIn = null;
        this.nextTankbusterDamageIn = null;
        this.specialModeIn = null;
        this.specialModeType = null;
        this.hasActiveModule = null;
        this.activeModuleName = null;
        this.isPositionSafe = null;
        this.isDashSafe = null;
        this.isFixedDashSafe = null;
        this.isBackdashSafe = null;
        this.navigationTargetPos = null;
        this.forbiddenZonesCount = null;
        this.forbiddenDirectionsCount = null;
        this.timelineDebug = null;
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
