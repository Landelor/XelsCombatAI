using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModGoalZoneHook : IDisposable
{
    private const string BossModPluginTypeName = "BossMod.Plugin";
    private const int MaxFailures = 3;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DalamudServices services;
    private readonly IPluginLog log;
    private readonly IReadOnlyList<IBossModGoalZoneContributor> contributors;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private int failures;
    private bool disabledAfterFailure;
    private string status = "unresolved";
    private ReflectedDraw? draw;

    public BossModGoalZoneHook(IDalamudPluginInterface pluginInterface, DalamudServices services, IPluginLog log, IReadOnlyList<IBossModGoalZoneContributor> contributors)
    {
        this.pluginInterface = pluginInterface;
        this.services = services;
        this.log = log;
        this.contributors = contributors;
        this.SetContributorHookState(this.status);
    }

    public string Status => this.status;
    public string Diagnostics => string.Join(
        "; ",
        $"Status={this.status}",
        $"DrawActive={this.draw != null}",
        $"Failures={this.failures}",
        $"DisabledAfterFailure={this.disabledAfterFailure}",
        $"ActiveGoalPriority={this.draw?.LastGoalPriority ?? "None"}",
        $"ActiveGoalSources={this.draw?.LastGoalSources ?? "<none>"}",
        $"NextResolveUtc={this.nextResolveAttempt:O}");

    public void EnsureActive()
    {
        if (this.draw != null || this.disabledAfterFailure)
        {
            return;
        }

        if (DateTime.UtcNow < this.nextResolveAttempt)
        {
            return;
        }

        this.nextResolveAttempt = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var plugin = ReflectionObjectSearch.FindLoadedPlugin(
                this.pluginInterface,
                BossModPluginTypeName,
                maxDepth: 8,
                "BossModReborn",
                "BossMod Reborn",
                "BossMod");
            if (plugin == null)
            {
                this.SetStatus("BMR plugin instance unavailable");
                return;
            }

            var reflectedDraw = ReflectedDraw.TryCreate(plugin, this.contributors, this.services, this.log, out var reason);
            if (reflectedDraw == null)
            {
                this.SetStatus(reason);
                return;
            }

            reflectedDraw.Install();
            this.draw = reflectedDraw;
            this.failures = 0;
            this.SetStatus("draw wrapper active");
        }
        catch (Exception ex)
        {
            this.HandleFailure(ex, "Could not install BossMod goal draw wrapper.", $"BMR goal wrapper install failed: {ex.Message}");
        }
    }

    public void Reset()
    {
        this.failures = 0;
        this.disabledAfterFailure = false;
        foreach (var contributor in this.contributors)
        {
            contributor.Reset();
        }

        this.DisposeDraw("unresolved");
    }

    public void Dispose()
    {
        this.DisposeDraw("disposed");
    }

    private void HandleFailure(Exception ex, string logMessage, string statusMessage)
    {
        ++this.failures;
        if (this.failures == 1)
        {
            this.log.Error(ex, logMessage);
        }
        else
        {
            this.log.Verbose($"{logMessage} {ex.Message}");
        }

        if (this.failures >= MaxFailures)
        {
            this.disabledAfterFailure = true;
            this.DisposeDraw("BMR goal wrapper disabled after repeated errors");
            return;
        }

        this.DisposeDraw(statusMessage);
    }

    private void DisposeDraw(string newStatus)
    {
        try
        {
            this.draw?.Dispose();
        }
        catch (Exception ex)
        {
            this.log.Verbose($"Could not dispose BossMod goal draw wrapper cleanly: {ex.Message}");
        }

        this.draw = null;
        this.SetStatus(newStatus);
    }

    private void SetStatus(string newStatus)
    {
        this.status = newStatus;
        this.SetContributorHookState(newStatus);
    }

    private void SetContributorHookState(string newStatus)
    {
        foreach (var contributor in this.contributors)
        {
            contributor.SetHookState(newStatus);
        }
    }

    private sealed class ReflectedDraw : IDisposable
    {
        private readonly object plugin;
        private readonly IDalamudPluginInterface bmrPluginInterface;
        private readonly Action originalDraw;
        private readonly Action wrapperDraw;
        private readonly IReadOnlyList<IBossModGoalZoneContributor> contributors;
        private readonly DalamudServices services;
        private readonly IPluginLog log;
        private readonly MethodInfo executeHintsMethod;
        private readonly FieldInfo previousUpdateTimeField;
        private readonly object dtr;
        private readonly object worldStateSync;
        private readonly object bossModuleManager;
        private readonly object zoneModuleManager;
        private readonly object hintsBuilder;
        private readonly object hints;
        private readonly FieldInfo goalZonesField;
        private readonly object actionManager;
        private readonly object rotation;
        private readonly object ai;
        private readonly object broadcast;
        private readonly object movementOverride;
        private readonly MethodInfo dtrUpdateMethod;
        private readonly MethodInfo worldStateSyncUpdateMethod;
        private readonly MethodInfo bossModuleUpdateMethod;
        private readonly FieldInfo activeZoneModuleField;
        private readonly MethodInfo? activeZoneModuleUpdateMethod;
        private readonly MethodInfo hintsBuilderUpdateMethod;
        private readonly MethodInfo queueManualActionsMethod;
        private readonly MethodInfo finishActionGatherMethod;
        private readonly PropertyInfo animationLockDelayEstimateProperty;
        private readonly MethodInfo rotationUpdateMethod;
        private readonly MethodInfo aiUpdateMethod;
        private readonly MethodInfo broadcastUpdateMethod;
        private readonly MethodInfo isMoveRequestedMethod;
        private readonly MethodInfo isForceUnblockedMethod;
        private readonly MethodInfo isMovingMethod;
        private readonly object actionManagerConfig;
        private readonly MemberInfo? preventMovingWhileCastingMember;
        private readonly FieldInfo playerSlotField;
        private readonly FieldInfo? cameraInstanceField;
        private readonly MethodInfo? cameraUpdateMethod;
        private readonly MethodInfo? cameraDrawWorldPrimitivesMethod;
        private readonly object? gameGui;
        private readonly PropertyInfo? gameUiHiddenProperty;
        private readonly FieldInfo? windowSystemField;
        private readonly MethodInfo? windowSystemDrawMethod;
        private string lastGoalPriority = "None";
        private string lastGoalSources = "<none>";

        private ReflectedDraw(
            object plugin,
            IDalamudPluginInterface bmrPluginInterface,
            Action originalDraw,
            IReadOnlyList<IBossModGoalZoneContributor> contributors,
            DalamudServices services,
            IPluginLog log,
            ReflectedMembers members)
        {
            this.plugin = plugin;
            this.bmrPluginInterface = bmrPluginInterface;
            this.originalDraw = originalDraw;
            this.wrapperDraw = this.Draw;
            this.contributors = contributors;
            this.services = services;
            this.log = log;
            this.executeHintsMethod = members.ExecuteHintsMethod;
            this.previousUpdateTimeField = members.PreviousUpdateTimeField;
            this.dtr = members.Dtr;
            this.worldStateSync = members.WorldStateSync;
            this.bossModuleManager = members.BossModuleManager;
            this.zoneModuleManager = members.ZoneModuleManager;
            this.hintsBuilder = members.HintsBuilder;
            this.hints = members.Hints;
            this.goalZonesField = members.GoalZonesField;
            this.actionManager = members.ActionManager;
            this.rotation = members.Rotation;
            this.ai = members.Ai;
            this.broadcast = members.Broadcast;
            this.movementOverride = members.MovementOverride;
            this.dtrUpdateMethod = members.DtrUpdateMethod;
            this.worldStateSyncUpdateMethod = members.WorldStateSyncUpdateMethod;
            this.bossModuleUpdateMethod = members.BossModuleUpdateMethod;
            this.activeZoneModuleField = members.ActiveZoneModuleField;
            this.activeZoneModuleUpdateMethod = members.ActiveZoneModuleUpdateMethod;
            this.hintsBuilderUpdateMethod = members.HintsBuilderUpdateMethod;
            this.queueManualActionsMethod = members.QueueManualActionsMethod;
            this.finishActionGatherMethod = members.FinishActionGatherMethod;
            this.animationLockDelayEstimateProperty = members.AnimationLockDelayEstimateProperty;
            this.rotationUpdateMethod = members.RotationUpdateMethod;
            this.aiUpdateMethod = members.AiUpdateMethod;
            this.broadcastUpdateMethod = members.BroadcastUpdateMethod;
            this.isMoveRequestedMethod = members.IsMoveRequestedMethod;
            this.isForceUnblockedMethod = members.IsForceUnblockedMethod;
            this.isMovingMethod = members.IsMovingMethod;
            this.actionManagerConfig = members.ActionManagerConfig;
            this.preventMovingWhileCastingMember = members.PreventMovingWhileCastingMember;
            this.playerSlotField = members.PlayerSlotField;
            this.cameraInstanceField = members.CameraInstanceField;
            this.cameraUpdateMethod = members.CameraUpdateMethod;
            this.cameraDrawWorldPrimitivesMethod = members.CameraDrawWorldPrimitivesMethod;
            this.gameGui = members.GameGui;
            this.gameUiHiddenProperty = members.GameUiHiddenProperty;
            this.windowSystemField = members.WindowSystemField;
            this.windowSystemDrawMethod = members.WindowSystemDrawMethod;
        }

        public string LastGoalPriority => this.lastGoalPriority;
        public string LastGoalSources => this.lastGoalSources;

        public static ReflectedDraw? TryCreate(object plugin, IReadOnlyList<IBossModGoalZoneContributor> contributors, DalamudServices services, IPluginLog log, out string reason)
        {
            reason = string.Empty;
            const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var type = plugin.GetType();
            var assembly = type.Assembly;
            var drawUiMethod = type.GetMethod("DrawUI", InstanceFlags);
            var bmrPluginInterface = type.GetField("_dalamud", InstanceFlags)?.GetValue(plugin) as IDalamudPluginInterface;
            if (drawUiMethod == null || bmrPluginInterface == null)
            {
                reason = "BMR DrawUI members unavailable";
                return null;
            }

            var originalDraw = drawUiMethod.CreateDelegate<Action>(plugin);
            var actionManagerExType = assembly.GetType("BossMod.ActionManagerEx");
            var serviceType = assembly.GetType("BossMod.Service");
            var cameraType = assembly.GetType("BossMod.Camera");
            var partyStateType = assembly.GetType("BossMod.PartyState");
            var hintsType = assembly.GetType("BossMod.AIHints");
            if (actionManagerExType == null || serviceType == null || partyStateType == null || hintsType == null)
            {
                reason = "BMR draw support types unavailable";
                return null;
            }

            var actionManagerConfig = actionManagerExType.GetField("Config", StaticFlags)?.GetValue(null);
            var gameGui = serviceType.GetProperty("GameGui", StaticFlags)?.GetValue(null);
            var members = new ReflectedMembers
            {
                ExecuteHintsMethod = Require(type.GetMethod("ExecuteHints", InstanceFlags), "ExecuteHints"),
                PreviousUpdateTimeField = Require(type.GetField("_prevUpdateTime", InstanceFlags), "_prevUpdateTime"),
                Dtr = RequireValue(type.GetField("_dtr", InstanceFlags)?.GetValue(plugin), "_dtr"),
                WorldStateSync = RequireValue(type.GetField("_wsSync", InstanceFlags)?.GetValue(plugin), "_wsSync"),
                BossModuleManager = RequireValue(type.GetField("_bossmod", InstanceFlags)?.GetValue(plugin), "_bossmod"),
                ZoneModuleManager = RequireValue(type.GetField("_zonemod", InstanceFlags)?.GetValue(plugin), "_zonemod"),
                HintsBuilder = RequireValue(type.GetField("_hintsBuilder", InstanceFlags)?.GetValue(plugin), "_hintsBuilder"),
                Hints = RequireValue(type.GetField("_hints", InstanceFlags)?.GetValue(plugin), "_hints"),
                GoalZonesField = Require(hintsType.GetField("GoalZones", InstanceFlags), "AIHints.GoalZones"),
                ActionManager = RequireValue(type.GetField("_amex", InstanceFlags)?.GetValue(plugin), "_amex"),
                Rotation = RequireValue(type.GetField("_rotation", InstanceFlags)?.GetValue(plugin), "_rotation"),
                Ai = RequireValue(type.GetField("_ai", InstanceFlags)?.GetValue(plugin), "_ai"),
                Broadcast = RequireValue(type.GetField("_broadcast", InstanceFlags)?.GetValue(plugin), "_broadcast"),
                MovementOverride = RequireValue(type.GetField("_movementOverride", InstanceFlags)?.GetValue(plugin), "_movementOverride"),
                ActionManagerConfig = RequireValue(actionManagerConfig, "ActionManagerEx.Config"),
                PreventMovingWhileCastingMember = actionManagerConfig?.GetType().GetProperty("PreventMovingWhileCasting", InstanceFlags)
                                             ?? (MemberInfo?)actionManagerConfig?.GetType().GetField("PreventMovingWhileCasting", InstanceFlags),
                CameraInstanceField = cameraType?.GetField("Instance", StaticFlags),
                GameGui = gameGui,
                GameUiHiddenProperty = gameGui?.GetType().GetProperty("GameUiHidden", InstanceFlags),
                WindowSystemField = serviceType.GetField("WindowSystem", StaticFlags),
                PlayerSlotField = Require(partyStateType.GetField("PlayerSlot", StaticFlags), "PartyState.PlayerSlot")
            };

            members.DtrUpdateMethod = Require(members.Dtr.GetType().GetMethod("Update", InstanceFlags), "DTR.Update");
            members.WorldStateSyncUpdateMethod = Require(members.WorldStateSync.GetType().GetMethod("Update", InstanceFlags), "WorldStateSync.Update");
            members.BossModuleUpdateMethod = Require(members.BossModuleManager.GetType().GetMethod("Update", InstanceFlags), "BossModuleManager.Update");
            members.ActiveZoneModuleField = Require(members.ZoneModuleManager.GetType().GetField("ActiveModule", InstanceFlags), "ZoneModuleManager.ActiveModule");
            members.ActiveZoneModuleUpdateMethod = members.ActiveZoneModuleField.FieldType.GetMethod("Update", InstanceFlags);
            members.HintsBuilderUpdateMethod = Require(members.HintsBuilder.GetType().GetMethod("Update", InstanceFlags, null, [hintsType, typeof(int), typeof(bool)], null), "AIHintsBuilder.Update");
            members.QueueManualActionsMethod = Require(members.ActionManager.GetType().GetMethod("QueueManualActions", InstanceFlags), "ActionManagerEx.QueueManualActions");
            members.FinishActionGatherMethod = Require(members.ActionManager.GetType().GetMethod("FinishActionGather", InstanceFlags), "ActionManagerEx.FinishActionGather");
            members.AnimationLockDelayEstimateProperty = Require(members.ActionManager.GetType().GetProperty("AnimationLockDelayEstimate", InstanceFlags), "ActionManagerEx.AnimationLockDelayEstimate");
            members.RotationUpdateMethod = Require(members.Rotation.GetType().GetMethod("Update", InstanceFlags), "RotationModuleManager.Update");
            members.AiUpdateMethod = Require(members.Ai.GetType().GetMethod("Update", InstanceFlags), "AIManager.Update");
            members.BroadcastUpdateMethod = Require(members.Broadcast.GetType().GetMethod("Update", InstanceFlags), "Broadcast.Update");
            members.IsMoveRequestedMethod = Require(members.MovementOverride.GetType().GetMethod("IsMoveRequested", InstanceFlags), "MovementOverride.IsMoveRequested");
            members.IsForceUnblockedMethod = Require(members.MovementOverride.GetType().GetMethod("IsForceUnblocked", InstanceFlags), "MovementOverride.IsForceUnblocked");
            members.IsMovingMethod = Require(members.MovementOverride.GetType().GetMethod("IsMoving", InstanceFlags), "MovementOverride.IsMoving");
            var cameraInstance = members.CameraInstanceField?.GetValue(null);
            members.CameraUpdateMethod = cameraInstance?.GetType().GetMethod("Update", InstanceFlags);
            members.CameraDrawWorldPrimitivesMethod = cameraInstance?.GetType().GetMethod("DrawWorldPrimitives", InstanceFlags);
            var windowSystem = members.WindowSystemField?.GetValue(null);
            members.WindowSystemDrawMethod = windowSystem?.GetType().GetMethod("Draw", InstanceFlags);

            try
            {
                return new ReflectedDraw(plugin, bmrPluginInterface, originalDraw, contributors, services, log, members);
            }
            catch (Exception ex)
            {
                reason = $"BMR draw wrapper resolve failed: {ex.Message}";
                return null;
            }
        }

        public void Install()
        {
            this.bmrPluginInterface.UiBuilder.Draw -= this.originalDraw;
            this.bmrPluginInterface.UiBuilder.Draw += this.wrapperDraw;
        }

        public void Dispose()
        {
            this.bmrPluginInterface.UiBuilder.Draw -= this.wrapperDraw;
            this.bmrPluginInterface.UiBuilder.Draw += this.originalDraw;
        }

        private void Draw()
        {
            try
            {
                this.DrawReflected();
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "Reflected BossMod draw wrapper failed; falling back to original BossMod draw for this frame.");
                this.originalDraw();
            }
        }

        private void DrawReflected()
        {
            var tsStart = DateTime.Now;
            var moveRequested = (bool)this.isMoveRequestedMethod.Invoke(this.movementOverride, [])!;
            var preventMovingWhileCasting = ReadBoolMember(this.actionManagerConfig, this.preventMovingWhileCastingMember);
            var forceUnblocked = (bool)this.isForceUnblockedMethod.Invoke(this.movementOverride, [])!;
            var moveImminent = moveRequested && (!preventMovingWhileCasting || forceUnblocked);
            foreach (var contributor in this.contributors)
            {
                contributor.SetBossModMovementState(moveRequested, moveImminent);
            }

            this.dtrUpdateMethod.Invoke(this.dtr, []);
            var camera = this.cameraInstanceField?.GetValue(null);
            this.cameraUpdateMethod?.Invoke(camera, []);
            this.worldStateSyncUpdateMethod.Invoke(this.worldStateSync, [this.previousUpdateTimeField.GetValue(this.plugin)]);
            this.bossModuleUpdateMethod.Invoke(this.bossModuleManager, []);
            var activeModule = this.activeZoneModuleField.GetValue(this.zoneModuleManager);
            if (activeModule != null)
            {
                this.activeZoneModuleUpdateMethod?.Invoke(activeModule, []);
            }

            this.hintsBuilderUpdateMethod.Invoke(this.hintsBuilder, [this.hints, (int)this.playerSlotField.GetRawConstantValue()!, moveImminent]);
            this.InjectContributorGoals();
            this.queueManualActionsMethod.Invoke(this.actionManager, []);
            this.rotationUpdateMethod.Invoke(this.rotation, [this.animationLockDelayEstimateProperty.GetValue(this.actionManager), (bool)this.isMovingMethod.Invoke(this.movementOverride, [])!, this.services.Condition[ConditionFlag.DutyRecorderPlayback]]);
            this.aiUpdateMethod.Invoke(this.ai, []);
            this.broadcastUpdateMethod.Invoke(this.broadcast, []);
            this.finishActionGatherMethod.Invoke(this.actionManager, []);

            if (!this.IsUiHidden())
            {
                var windowSystem = this.windowSystemField?.GetValue(null);
                this.windowSystemDrawMethod?.Invoke(windowSystem, []);
            }

            this.executeHintsMethod.Invoke(this.plugin, []);
            this.cameraDrawWorldPrimitivesMethod?.Invoke(camera, []);
            this.previousUpdateTimeField.SetValue(this.plugin, DateTime.Now - tsStart);
        }

        private void InjectContributorGoals()
        {
            var goalZones = this.goalZonesField.GetValue(this.hints) as IList;
            if (goalZones == null)
            {
                this.lastGoalPriority = "None";
                this.lastGoalSources = "BMR goal zone list unavailable";
                return;
            }

            var contributions = new List<BossModGoalContribution>();
            foreach (var contributor in this.contributors)
            {
                contributor.TryInjectGoal(this.hints, contributions);
            }

            if (contributions.Count == 0)
            {
                this.lastGoalPriority = "None";
                this.lastGoalSources = "<none>";
                return;
            }

            var activePriority = contributions.Max(c => c.Priority);
            var active = contributions.Where(c => c.Priority == activePriority).ToArray();
            this.lastGoalPriority = activePriority.ToString();
            this.lastGoalSources = string.Join(", ", active.Select(c => c.Label).Distinct(StringComparer.Ordinal));
            goalZones.Add(CreateAdvisoryGoalDelegate(active));
        }

        private static Delegate CreateAdvisoryGoalDelegate(IReadOnlyList<BossModGoalContribution> contributions)
        {
            var goals = contributions.Select(c => c.Goal).ToArray();
            var invoke = Require(goals[0].GetType().GetMethod("Invoke"), $"{goals[0].GetType().FullName}.Invoke");
            var parameters = invoke.GetParameters();
            if (parameters.Length != 1 || invoke.ReturnType != typeof(float))
            {
                throw new InvalidOperationException($"Unexpected BossMod goal delegate signature: {goals[0].GetType().FullName}.");
            }

            var wposType = parameters[0].ParameterType;
            var parameter = Expression.Parameter(wposType, "p");
            Expression max = Expression.Constant(0f);
            var mathMax = typeof(Math).GetMethod(nameof(Math.Max), [typeof(float), typeof(float)])!;
            foreach (var goal in goals)
            {
                max = Expression.Call(mathMax, max, Expression.Invoke(Expression.Constant(goal, goal.GetType()), parameter));
            }

            var clamp = typeof(GoalZoneScorePolicy).GetMethod(nameof(GoalZoneScorePolicy.ClampAdvisoryScore), BindingFlags.Static | BindingFlags.Public)!;
            var score = Expression.Call(clamp, max);
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, score, parameter).Compile();
        }

        private bool IsUiHidden()
        {
            return (this.gameUiHiddenProperty?.GetValue(this.gameGui) as bool? ?? false) ||
                   this.services.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                   this.services.Condition[ConditionFlag.WatchingCutscene78] ||
                   this.services.Condition[ConditionFlag.WatchingCutscene];
        }

        private static T Require<T>(T? value, string name)
            where T : class
        {
            return value ?? throw new MissingMemberException(name);
        }

        private static object RequireValue(object? value, string name)
        {
            return value ?? throw new MissingMemberException(name);
        }

        private static bool ReadBoolMember(object instance, MemberInfo? member)
        {
            return member switch
            {
                FieldInfo field       => (bool)(field.GetValue(instance) ?? false),
                PropertyInfo property => (bool)(property.GetValue(instance) ?? false),
                _                     => false
            };
        }

        private sealed class ReflectedMembers
        {
            public MethodInfo ExecuteHintsMethod = null!;
            public FieldInfo PreviousUpdateTimeField = null!;
            public object Dtr = null!;
            public object WorldStateSync = null!;
            public object BossModuleManager = null!;
            public object ZoneModuleManager = null!;
            public object HintsBuilder = null!;
            public object Hints = null!;
            public FieldInfo GoalZonesField = null!;
            public object ActionManager = null!;
            public object Rotation = null!;
            public object Ai = null!;
            public object Broadcast = null!;
            public object MovementOverride = null!;
            public object ActionManagerConfig = null!;
            public FieldInfo PlayerSlotField = null!;

            public MethodInfo DtrUpdateMethod = null!;
            public MethodInfo WorldStateSyncUpdateMethod = null!;
            public MethodInfo BossModuleUpdateMethod = null!;
            public FieldInfo ActiveZoneModuleField = null!;
            public MethodInfo? ActiveZoneModuleUpdateMethod;
            public MethodInfo HintsBuilderUpdateMethod = null!;
            public MethodInfo QueueManualActionsMethod = null!;
            public MethodInfo FinishActionGatherMethod = null!;
            public PropertyInfo AnimationLockDelayEstimateProperty = null!;
            public MethodInfo RotationUpdateMethod = null!;
            public MethodInfo AiUpdateMethod = null!;
            public MethodInfo BroadcastUpdateMethod = null!;
            public MethodInfo IsMoveRequestedMethod = null!;
            public MethodInfo IsForceUnblockedMethod = null!;
            public MethodInfo IsMovingMethod = null!;
            public MemberInfo? PreventMovingWhileCastingMember;
            public FieldInfo? CameraInstanceField;
            public MethodInfo? CameraUpdateMethod;
            public MethodInfo? CameraDrawWorldPrimitivesMethod;
            public object? GameGui;
            public PropertyInfo? GameUiHiddenProperty;
            public FieldInfo? WindowSystemField;
            public MethodInfo? WindowSystemDrawMethod;
        }
    }
}
