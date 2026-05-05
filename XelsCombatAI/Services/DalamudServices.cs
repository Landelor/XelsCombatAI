using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Services;

internal sealed class DalamudServices(
    IDalamudPluginInterface pluginInterface,
    ICommandManager commandManager,
    IFramework framework,
    IChatGui chatGui,
    IPluginLog log,
    IKeyState keyState,
    IDtrBar dtrBar,
    ICondition condition,
    IClientState clientState,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IPartyList partyList)
{
    public IDalamudPluginInterface PluginInterface { get; } = pluginInterface;
    public ICommandManager CommandManager { get; } = commandManager;
    public IFramework Framework { get; } = framework;
    public IChatGui ChatGui { get; } = chatGui;
    public IPluginLog Log { get; } = log;
    public IKeyState KeyState { get; } = keyState;
    public IDtrBar DtrBar { get; } = dtrBar;
    public ICondition Condition { get; } = condition;
    public IClientState ClientState { get; } = clientState;
    public IObjectTable ObjectTable { get; } = objectTable;
    public ITargetManager TargetManager { get; } = targetManager;
    public IPartyList PartyList { get; } = partyList;

    public bool HasLoadedPlugin(params string[] names)
    {
        return this.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            names.Any(name =>
                string.Equals(plugin.InternalName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase)));
    }
}
