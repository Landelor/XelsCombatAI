using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XelsCombatAI;

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
        "BossMod.Autorotation.MiscAI.StayCloseToPartyRole": [
          {
            "Track": "range",
            "Option": "20"
          }
        ],
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

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string?> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ICallGateSubscriber<uint, bool> hasModuleByDataId;
    private readonly ICallGateSubscriber<string, bool, bool> disableModule;
    private readonly ICallGateSubscriber<string, string, string, string, bool> addTransientStrategy;
    private readonly ICallGateSubscriber<float> nextDamageIn;
    private readonly ICallGateSubscriber<float> specialModeIn;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool> isDashToPositionSafe;

    public BossModIpc(IDalamudPluginInterface pluginInterface)
    {
        this.getPreset = pluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get");
        this.createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        this.getActivePreset = pluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive");
        this.setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        this.clearActivePreset = pluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        this.hasModuleByDataId = pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId");
        this.disableModule = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Configuration.DisableModule");
        this.addTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
        this.nextDamageIn = pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.NextDamageIn");
        this.specialModeIn = pluginInterface.GetIpcSubscriber<float>("BossMod.Hints.SpecialModeIn");
        this.isDashToPositionSafe = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashToPositionSafe");
    }

    public bool EnsurePreset()
    {
        if (this.getPreset.InvokeFunc(DefaultPresetName) != null)
        {
            return true;
        }

        return this.createPreset.InvokeFunc(PresetPayload, true);
    }

    public bool IsAvailable()
    {
        try
        {
            _ = this.getPreset.InvokeFunc(DefaultPresetName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetActive(string presetName) => this.setActivePreset.InvokeFunc(presetName);

    public string? GetActive() => this.getActivePreset.InvokeFunc();

    public bool ClearActive() => this.clearActivePreset.InvokeFunc();

    public bool SetPositional(string presetName, Positional positional)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.GoToPositional",
            "Positional",
            positional.ToString());
    }

    public bool SetRange(string presetName, float range)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToTarget",
            "range",
            MathF.Round(range, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool SetMovement(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Destination",
            enabled ? "Pathfind" : "None");
    }

    public bool SetForbiddenZoneCushion(string presetName, string cushion)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "ForbiddenZoneCushion",
            cushion);
    }

    public bool SetPartyRole(string presetName, string role)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToPartyRole",
            "Role",
            role);
    }

    public bool SetLeylinesBetweenTheLines(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Between The Lines",
            enabled ? "Yes" : "No");
    }

    public bool SetLeylinesRetrace(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Use Retrace",
            enabled ? "Yes" : "No");
    }

    public bool SetLeylinesGoal(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayWithinLeylines",
            "Goal",
            enabled ? "Enabled" : "Disabled");
    }

    public bool IsSafeToEngage(float withinSeconds = 3f)
    {
        try
        {
            if (this.nextDamageIn.InvokeFunc() <= withinSeconds) return false;
            if (this.specialModeIn.InvokeFunc() <= withinSeconds) return false;
            return true;
        }
        catch
        {
            return true;
        }
    }

    public bool IsDashToPositionSafe(Vector3 from, Vector3 to)
    {
        try
        {
            return this.isDashToPositionSafe.InvokeFunc(from, to);
        }
        catch
        {
            return this.IsSafeToEngage();
        }
    }

    public bool HasModuleByDataId(uint dataId) => this.hasModuleByDataId.InvokeFunc(dataId);

    public bool DisableModule(string moduleName, bool disabled) => this.disableModule.InvokeFunc(moduleName, disabled);
}
