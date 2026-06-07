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
    IDutyState dutyState,
    IGameGui gameGui,
    IObjectTable objectTable,
    ITargetManager targetManager,
    IPartyList partyList,
    IGameConfig gameConfig)
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
    public IDutyState DutyState { get; } = dutyState;
    public IGameGui GameGui { get; } = gameGui;
    public IObjectTable ObjectTable { get; } = objectTable;
    public ITargetManager TargetManager { get; } = targetManager;
    public IPartyList PartyList { get; } = partyList;
    public IGameConfig GameConfig { get; } = gameConfig;
}
