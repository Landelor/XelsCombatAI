using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

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
        "BossMod.Autorotation.MiscAI.StayCloseToPartyRole": [
          {
            "Track": "range",
            "Option": "20"
          }
        ],
        "BossMod.Autorotation.xan.HealerAI": [
          {
            "Track": "Raise",
            "Option": "None"
          },
          {
            "Track": "Heal",
            "Option": "Disabled"
          },
          {
            "Track": "Esuna2",
            "Option": "Disabled"
          },
          {
            "Track": "Stay near party",
            "Option": "Disabled"
          },
          {
            "Track": "OutOfCombat",
            "Option": "Disabled"
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
        ],
        "BossMod.Autorotation.ClassMNKUtility": [
          {
            "Track": "Thunderclap",
            "Option": "None"
          }
        ],
        "BossMod.Autorotation.ClassDRGUtility": [
          {
            "Track": "Winged Glide",
            "Option": "None"
          }
        ],
        "BossMod.Autorotation.ClassNINUtility": [
          {
            "Track": "Shukuchi",
            "Option": "None"
          }
        ],
        "BossMod.Autorotation.ClassVPRUtility": [
          {
            "Track": "Slither",
            "Option": "None"
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
        "BossMod.Autorotation.MiscAI.StayCloseToPartyRole",
        "BossMod.Autorotation.xan.HealerAI",
        "BossMod.Autorotation.MiscAI.NormalMovement",
        "BossMod.Autorotation.ClassMNKUtility",
        "BossMod.Autorotation.ClassDRGUtility",
        "BossMod.Autorotation.ClassNINUtility",
        "BossMod.Autorotation.ClassVPRUtility"
    ];

    private readonly ICallGateSubscriber<string, string?> getPreset;
    private readonly ICallGateSubscriber<string, bool, bool> createPreset;
    private readonly ICallGateSubscriber<string?> getActivePreset;
    private readonly ICallGateSubscriber<string, bool> setActivePreset;
    private readonly ICallGateSubscriber<bool> clearActivePreset;
    private readonly ICallGateSubscriber<uint, bool> hasModuleByDataId;
    private readonly ICallGateSubscriber<string, bool, bool> disableModule;
    private readonly ICallGateSubscriber<string, string, string, string, bool> addTransientStrategy;

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
    }

    public bool EnsurePreset()
    {
        var preset = this.getPreset.InvokeFunc(DefaultPresetName);
        if (preset != null && RequiredPresetModules.All(preset.Contains))
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

    public bool SetMovementRangeStrategy(string presetName, string strategy)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.NormalMovement",
            "Range",
            strategy);
    }

    public bool SetPartyRole(string presetName, string role)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.MiscAI.StayCloseToPartyRole",
            "Role",
            role);
    }

    public bool SetHealerStayNearParty(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.xan.HealerAI",
            "Stay near party",
            enabled ? "Enabled" : "Disabled");
    }

    public bool SetHealerHeal(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.xan.HealerAI",
            "Heal",
            enabled ? "Enabled" : "Disabled");
    }

    public bool SetHealerEsuna(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.xan.HealerAI",
            "Esuna2",
            enabled ? "Enabled" : "Disabled");
    }

    public bool SetHealerOutOfCombat(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.xan.HealerAI",
            "OutOfCombat",
            enabled ? "Enabled" : "Disabled");
    }

    public bool SetHealerRaise(string presetName, string strategy)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.xan.HealerAI",
            "Raise",
            strategy);
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

    public bool SetMonkThunderclap(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.ClassMNKUtility",
            "Thunderclap",
            enabled ? "GapClose" : "None");
    }

    public bool SetDragoonWingedGlide(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.ClassDRGUtility",
            "Winged Glide",
            enabled ? "GapClose" : "None");
    }

    public bool SetNinjaShukuchi(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.ClassNINUtility",
            "Shukuchi",
            enabled ? "GapClose" : "None");
    }

    public bool SetViperSlither(string presetName, bool enabled)
    {
        return this.addTransientStrategy.InvokeFunc(
            presetName,
            "BossMod.Autorotation.ClassVPRUtility",
            "Slither",
            enabled ? "GapClose" : "None");
    }

    public bool HasModuleByDataId(uint dataId) => this.hasModuleByDataId.InvokeFunc(dataId);

    public bool DisableModule(string moduleName, bool disabled) => this.disableModule.InvokeFunc(moduleName, disabled);
}
