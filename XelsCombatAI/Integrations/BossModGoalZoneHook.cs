using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModGoalZoneHook : IDisposable
{
    private const string BossModPluginTypeName = "BossMod.Plugin";
    private const int MaxFailures = 3;
    private static readonly TimeSpan OwnerLivenessCheckInterval = TimeSpan.FromSeconds(2);
    private const float MechanicWhisperCandidateResetDistance = 3f;
    private const float MechanicEscapeMarginCandidateResetDistance = 0.75f;
    private const float MechanicEscapeMarginRadius = 2.5f;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly IPluginLog log;
    private readonly IReadOnlyList<IBossModGoalZoneContributor> contributors;
    private readonly ManualCorrectionFeedback manualCorrectionFeedback;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private DateTime nextOwnerLivenessCheck = DateTime.MinValue;
    private int failures;
    private bool disabledAfterFailure;
    private string status = "unresolved";
    private ReflectedDraw? draw;

    public BossModGoalZoneHook(Configuration config, IDalamudPluginInterface pluginInterface, DalamudServices services, IPluginLog log, IReadOnlyList<IBossModGoalZoneContributor> contributors, ManualCorrectionFeedback manualCorrectionFeedback)
    {
        this.config = config;
        this.pluginInterface = pluginInterface;
        this.services = services;
        this.log = log;
        this.contributors = contributors;
        this.manualCorrectionFeedback = manualCorrectionFeedback;
        this.SetContributorHookState(this.status);
    }

    public string Status => this.status;
    public string LastGoalPriority => this.draw?.LastGoalPriority ?? "None";
    public string LastGoalSources => this.draw?.LastGoalSources ?? "<none>";
    public BossModMovementDiagnostics MovementDiagnostics => this.draw?.MovementDiagnostics ?? BossModMovementDiagnostics.Empty;
    public string Diagnostics => string.Join(
        "; ",
        $"Status={this.status}",
        $"DrawActive={this.draw != null}",
        $"Failures={this.failures}",
        $"DisabledAfterFailure={this.disabledAfterFailure}",
        $"MechanicWhisper={this.draw?.MovementDiagnostics.MechanicWhisper ?? "not logged"}",
        $"ManualCorrectionFeedback={this.manualCorrectionFeedback.Status}",
        $"ActiveGoalPriority={this.draw?.LastGoalPriority ?? "None"}",
        $"ActiveGoalSources={this.draw?.LastGoalSources ?? "<none>"}",
        $"NextResolveUtc={this.nextResolveAttempt:O}");

    internal static bool ShouldAllowMechanicWhisperCandidate(
        Vector2 candidate,
        Vector2? bossModDestination,
        Vector2? playerPosition,
        TimeSpan stableFor,
        MechanicWhisperConfidence confidence = MechanicWhisperConfidence.Routine)
        => BossModMechanicGoalPolicy.ShouldAllowMechanicWhisperCandidate(candidate, bossModDestination, playerPosition, stableFor, confidence);

    internal static bool ShouldIsolateMechanicSafetyGoals(
        int forbiddenZones,
        int temporaryObstacles,
        int forbiddenDirections,
        string? imminentSpecialMode,
        bool forcedMovementActive)
        => BossModMechanicGoalPolicy.ShouldIsolateMechanicSafetyGoals(
            forbiddenZones,
            temporaryObstacles,
            forbiddenDirections,
            imminentSpecialMode,
            forcedMovementActive);

    internal static BossModGoalContribution[] SelectMechanicSafetyGoalContributions(IReadOnlyList<BossModGoalContribution> contributions)
        => BossModMechanicGoalPolicy.SelectMechanicSafetyGoalContributions(contributions);

    internal static bool TryResolveMechanicEscapeMarginCandidate(
        Vector2 playerPosition,
        Vector3? desiredMovement,
        bool forbiddenZonesActive,
        bool forcedMovementActive,
        bool moveRequested,
        bool moveImminent,
        out Vector2 candidate)
        => BossModMechanicGoalPolicy.TryResolveMechanicEscapeMarginCandidate(
            playerPosition,
            desiredMovement,
            forbiddenZonesActive,
            forcedMovementActive,
            moveRequested,
            moveImminent,
            out candidate);

    public void EnsureActive()
    {
        var now = DateTime.UtcNow;
        if (this.draw != null && now >= this.nextOwnerLivenessCheck)
        {
            this.nextOwnerLivenessCheck = now.Add(OwnerLivenessCheckInterval);
            if (!this.draw.IsOwnerLoaded())
            {
                this.DisposeDraw("BMR draw owner unloaded");
            }
        }

        if (this.draw != null || this.disabledAfterFailure)
        {
            return;
        }

        if (now < this.nextResolveAttempt)
        {
            return;
        }

        this.nextResolveAttempt = now.AddSeconds(5);
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

            var reflectedDraw = ReflectedDraw.TryCreate(plugin, this.config, this.contributors, this.manualCorrectionFeedback, this.services, this.log, this.HandleFailure, out var reason);
            if (reflectedDraw == null)
            {
                this.SetStatus(reason);
                return;
            }

            reflectedDraw.Install();
            this.draw = reflectedDraw;
            this.nextOwnerLivenessCheck = DateTime.UtcNow.Add(OwnerLivenessCheckInterval);
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
        this.nextOwnerLivenessCheck = DateTime.MinValue;
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
        private readonly Configuration config;
        private readonly IReadOnlyList<IBossModGoalZoneContributor> contributors;
        private readonly ManualCorrectionFeedback manualCorrectionFeedback;
        private readonly DalamudServices services;
        private readonly IPluginLog log;
        private readonly Action<Exception, string, string> failureHandler;
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
        private readonly FieldInfo? aiControllerField;
        private readonly FieldInfo? controllerNaviTargetPosField;
        private readonly FieldInfo? movementDesiredDirectionField;
        private readonly MethodInfo dtrUpdateMethod;
        private readonly MethodInfo worldStateSyncUpdateMethod;
        private readonly MethodInfo bossModuleUpdateMethod;
        private readonly PropertyInfo? activeBossModuleProperty;
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
        private bool installed;
        private string lastGoalPriority = "None";
        private string lastGoalSources = "<none>";
        private string lastMechanicWhisperStatus = "<none>";
        private BossModMovementDiagnostics movementDiagnostics = BossModMovementDiagnostics.Empty;
        private DateTime nextMovementDiagnosticsCapture = DateTime.MinValue;
        private DateTime nextDiagnosticsFailureLog = DateTime.MinValue;
        private bool currentMoveRequested;
        private bool currentMoveImminent;
        private readonly Dictionary<string, MechanicWhisperState> mechanicWhisperStates = new(StringComparer.Ordinal);
        private Delegate? lastMechanicEscapeMarginGoalDelegate;
        private Vector2 lastMechanicEscapeMarginCandidate;
        private Type? mechanicEscapeMarginWPosType;
        private FieldInfo? mechanicEscapeMarginWPosXField;
        private FieldInfo? mechanicEscapeMarginWPosZField;
        private static readonly TimeSpan MovementDiagnosticsCaptureInterval = TimeSpan.FromMilliseconds(250);
        private const string LegacyDirectMovementStatus = "legacy direct contributors";
        private const string LegacyForwardBrakeStatus = "legacy direct disabled";
        private const int MaxSafetyRasterDimension = 48;
        private static readonly MethodInfo ScoreMechanicEscapeMarginMethod = typeof(ReflectedDraw).GetMethod(nameof(ScoreMechanicEscapeMargin), BindingFlags.Static | BindingFlags.NonPublic)!;

        private ReflectedDraw(
            object plugin,
            IDalamudPluginInterface bmrPluginInterface,
            Action originalDraw,
            Configuration config,
            IReadOnlyList<IBossModGoalZoneContributor> contributors,
            ManualCorrectionFeedback manualCorrectionFeedback,
            DalamudServices services,
            IPluginLog log,
            Action<Exception, string, string> failureHandler,
            ReflectedMembers members)
        {
            this.plugin = plugin;
            this.bmrPluginInterface = bmrPluginInterface;
            this.originalDraw = originalDraw;
            this.wrapperDraw = this.Draw;
            this.config = config;
            this.contributors = contributors;
            this.manualCorrectionFeedback = manualCorrectionFeedback;
            this.services = services;
            this.log = log;
            this.failureHandler = failureHandler;
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
            this.aiControllerField = members.AiControllerField;
            this.controllerNaviTargetPosField = members.ControllerNaviTargetPosField;
            this.movementDesiredDirectionField = members.MovementDesiredDirectionField;
            this.dtrUpdateMethod = members.DtrUpdateMethod;
            this.worldStateSyncUpdateMethod = members.WorldStateSyncUpdateMethod;
            this.bossModuleUpdateMethod = members.BossModuleUpdateMethod;
            this.activeBossModuleProperty = members.ActiveBossModuleProperty;
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
        public BossModMovementDiagnostics MovementDiagnostics => this.movementDiagnostics;

        public bool IsOwnerLoaded()
        {
            var activePlugin = ReflectionObjectSearch.FindLoadedPlugin(
                this.bmrPluginInterface,
                BossModPluginTypeName,
                maxDepth: 8,
                "BossModReborn",
                "BossMod Reborn",
                "BossMod");
            if (activePlugin == null)
            {
                return false;
            }

            return ReferenceEquals(activePlugin, this.plugin) ||
                   activePlugin.GetType().Assembly == this.plugin.GetType().Assembly;
        }

        public static ReflectedDraw? TryCreate(
            object plugin,
            Configuration config,
            IReadOnlyList<IBossModGoalZoneContributor> contributors,
            ManualCorrectionFeedback manualCorrectionFeedback,
            DalamudServices services,
            IPluginLog log,
            Action<Exception, string, string> failureHandler,
            out string reason)
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
            members.ActiveBossModuleProperty = members.BossModuleManager.GetType().GetProperty("ActiveModule", InstanceFlags);
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
            members.AiControllerField = members.Ai.GetType().GetField("Controller", InstanceFlags);
            var controller = members.AiControllerField?.GetValue(members.Ai);
            members.ControllerNaviTargetPosField = controller?.GetType().GetField("NaviTargetPos", InstanceFlags);
            members.MovementDesiredDirectionField = members.MovementOverride.GetType().GetField("DesiredDirection", InstanceFlags);
            var cameraInstance = members.CameraInstanceField?.GetValue(null);
            members.CameraUpdateMethod = cameraInstance?.GetType().GetMethod("Update", InstanceFlags);
            members.CameraDrawWorldPrimitivesMethod = cameraInstance?.GetType().GetMethod("DrawWorldPrimitives", InstanceFlags);
            var windowSystem = members.WindowSystemField?.GetValue(null);
            members.WindowSystemDrawMethod = windowSystem?.GetType().GetMethod("Draw", InstanceFlags);

            try
            {
                return new ReflectedDraw(plugin, bmrPluginInterface, originalDraw, config, contributors, manualCorrectionFeedback, services, log, failureHandler, members);
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "Could not create reflected BossMod draw wrapper.");
                reason = $"BMR draw wrapper resolve failed: {ex.Message}";
                return null;
            }
        }

        public void Install()
        {
            this.bmrPluginInterface.UiBuilder.Draw -= this.originalDraw;
            this.bmrPluginInterface.UiBuilder.Draw += this.wrapperDraw;
            this.installed = true;
        }

        public void Dispose()
        {
            if (!this.installed)
            {
                return;
            }

            this.installed = false;
            this.bmrPluginInterface.UiBuilder.Draw -= this.wrapperDraw;
            if (this.IsOwnerLoaded())
            {
                this.bmrPluginInterface.UiBuilder.Draw += this.originalDraw;
            }
        }

        private void Draw()
        {
            try
            {
                this.DrawReflected();
            }
            catch (Exception ex)
            {
                try
                {
                    this.originalDraw();
                }
                catch (Exception fallbackEx)
                {
                    this.log.Verbose(fallbackEx, "Original BossMod draw fallback failed.");
                }

                this.failureHandler(
                    ex,
                    "Reflected BossMod draw wrapper failed; falling back to original BossMod draw for this frame.",
                    $"BMR goal wrapper draw failed: {ex.Message}");
            }
        }

        private void DrawReflected()
        {
            var tsStart = DateTime.Now;
            var moveRequested = (bool)this.isMoveRequestedMethod.Invoke(this.movementOverride, [])!;
            var preventMovingWhileCasting = ReadBoolMember(this.actionManagerConfig, this.preventMovingWhileCastingMember);
            var forceUnblocked = (bool)this.isForceUnblockedMethod.Invoke(this.movementOverride, [])!;
            var moveImminent = moveRequested && (!preventMovingWhileCasting || forceUnblocked);
            this.currentMoveRequested = moveRequested;
            this.currentMoveImminent = moveImminent;
            this.SetContributorBossModMovementState(moveRequested, moveImminent);

            this.dtrUpdateMethod.Invoke(this.dtr, []);
            var camera = this.cameraInstanceField?.GetValue(null);
            this.cameraUpdateMethod?.Invoke(camera, []);
            this.worldStateSyncUpdateMethod.Invoke(this.worldStateSync, [this.previousUpdateTimeField.GetValue(this.plugin)]);
            this.bossModuleUpdateMethod.Invoke(this.bossModuleManager, []);
            var activeBossModule = this.activeBossModuleProperty?.GetValue(this.bossModuleManager);
            var activeZoneModule = this.activeZoneModuleField.GetValue(this.zoneModuleManager);
            if (activeZoneModule != null)
            {
                this.activeZoneModuleUpdateMethod?.Invoke(activeZoneModule, []);
            }

            var encounterActive = BossModEncounterClassifier.IsEncounterActive(activeBossModule, activeZoneModule);
            this.SetContributorBossModEncounterState(encounterActive);

            this.hintsBuilderUpdateMethod.Invoke(this.hintsBuilder, [this.hints, (int)this.playerSlotField.GetRawConstantValue()!, moveImminent]);
            this.InjectContributorGoals();
            this.queueManualActionsMethod.Invoke(this.actionManager, []);
            this.rotationUpdateMethod.Invoke(this.rotation, [this.animationLockDelayEstimateProperty.GetValue(this.actionManager), (bool)this.isMovingMethod.Invoke(this.movementOverride, [])!, this.services.Condition[ConditionFlag.DutyRecorderPlayback]]);
            this.aiUpdateMethod.Invoke(this.ai, []);
            this.CaptureMovementDiagnosticsIfDue(activeBossModule);
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
                this.lastMechanicWhisperStatus = "BMR goal zone list unavailable";
                return;
            }

            var contributions = new List<BossModGoalContribution>();
            foreach (var contributor in this.contributors)
            {
                try
                {
                    contributor.TryInjectGoal(this.hints, contributions);
                }
                catch (Exception ex)
                {
                    this.log.Verbose(ex, $"BossMod goal contributor '{contributor.GetType().Name}' failed.");
                }
            }

            this.TryAddMechanicEscapeMarginGoal(contributions);

            var allContributions = contributions.ToArray();
            var castingSuppressed = this.ShouldSuppressAdvisoryMovementForCasting();
            var activeContributions = castingSuppressed
                ? SuppressAdvisoryMovementForCasting(allContributions)
                : allContributions;
            var mechanicSafetyIsolated = this.ShouldIsolateMechanicSafetyGoals();
            if (mechanicSafetyIsolated)
            {
                activeContributions = SelectMechanicSafetyGoalContributions(activeContributions);
            }

            if (allContributions.Length == 0)
            {
                this.mechanicWhisperStates.Clear();
                this.lastGoalPriority = "None";
                this.lastGoalSources = "<none>";
                this.lastMechanicWhisperStatus = "<none>";
                return;
            }

            if (activeContributions.Length == 0)
            {
                this.mechanicWhisperStates.Clear();
                this.lastGoalPriority = $"{FormatGoalPrioritySummary(allContributions)} (casting suppressed)";
                this.lastGoalSources = "casting; advisory movement suppressed";
                this.lastMechanicWhisperStatus = "<none>";
                return;
            }

            activeContributions = this.manualCorrectionFeedback.Apply(
                activeContributions,
                this.services.ObjectTable.LocalPlayer?.Position,
                DateTime.UtcNow);

            var mechanicWhisperGuardActive = this.ShouldApplyMechanicWhisperGuard();
            if (mechanicWhisperGuardActive)
            {
                activeContributions = this.FilterMechanicWhispers(activeContributions);
            }
            else
            {
                this.mechanicWhisperStates.Clear();
                this.lastMechanicWhisperStatus = "<none>";
            }

            if (activeContributions.Length == 0)
            {
                this.lastGoalPriority = $"{FormatGoalPrioritySummary(allContributions)} (guarded)";
                this.lastGoalSources = "mechanic whisper stabilizing";
                return;
            }

            var prioritySummary = FormatGoalPrioritySummary(activeContributions);
            this.lastGoalPriority = prioritySummary + FormatContributionStateSuffix(
                mechanicWhisperGuardActive,
                castingSuppressed,
                mechanicSafetyIsolated);
            this.lastGoalSources = string.Join(", ", activeContributions.Select(c => c.Label).Distinct(StringComparer.Ordinal));

            var advisoryContributions = SelectAdvisoryGoalContributions(activeContributions);
            if (advisoryContributions.Length > 0)
            {
                goalZones.Add(CreateAdvisoryGoalDelegate(advisoryContributions));
            }

            foreach (var rawContribution in SelectRawGoalContributions(activeContributions))
            {
                goalZones.Add(rawContribution.Goal);
            }
        }

        private void TryAddMechanicEscapeMarginGoal(ICollection<BossModGoalContribution> contributions)
        {
            if (!this.config.Enabled ||
                !this.config.ManageMovement ||
                this.services.Condition[ConditionFlag.Unconscious])
            {
                return;
            }

            var player = this.services.ObjectTable.LocalPlayer;
            if (player == null || player.IsDead || player.CurrentHp == 0)
            {
                return;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var forbiddenZonesActive = CountField(this.hints, "ForbiddenZones", Flags) > 0;
            var forcedMovementActive = XzLengthSquared(ReadVector3(ReadField(this.hints, "ForcedMovement", Flags))) > 0.01f;
            var desiredMovement = ReadVector3(this.movementDesiredDirectionField?.GetValue(this.movementOverride));
            if (!BossModGoalZoneHook.TryResolveMechanicEscapeMarginCandidate(
                    new Vector2(player.Position.X, player.Position.Z),
                    desiredMovement,
                    forbiddenZonesActive,
                    forcedMovementActive,
                    this.currentMoveRequested,
                    this.currentMoveImminent,
                    out var candidate))
            {
                return;
            }

            if (!this.TryEnsureMechanicEscapeMarginWPosFields())
            {
                return;
            }

            if (this.lastMechanicEscapeMarginGoalDelegate == null ||
                Vector2.Distance(this.lastMechanicEscapeMarginCandidate, candidate) > MechanicEscapeMarginCandidateResetDistance)
            {
                this.lastMechanicEscapeMarginGoalDelegate = this.CreateMechanicEscapeMarginGoalDelegate(candidate);
                this.lastMechanicEscapeMarginCandidate = candidate;
            }

            contributions.Add(new(
                this.lastMechanicEscapeMarginGoalDelegate,
                BossModGoalPriority.DefensiveMechanic,
                "Mechanic exit margin",
                candidate,
                MechanicWhisperConfidence.Confident,
                ScoreMode: BossModGoalScoreMode.Raw));
        }

        private bool ShouldApplyMechanicWhisperGuard()
        {
            if (!this.currentMoveRequested && !this.currentMoveImminent)
            {
                return false;
            }

            return this.ShouldIsolateMechanicSafetyGoals();
        }

        private bool ShouldIsolateMechanicSafetyGoals()
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var forcedMovementActive = XzLengthSquared(ReadVector3(ReadField(this.hints, "ForcedMovement", Flags))) > 0.01f;
            return BossModGoalZoneHook.ShouldIsolateMechanicSafetyGoals(
                CountField(this.hints, "ForbiddenZones", Flags),
                CountField(this.hints, "TemporaryObstacles", Flags),
                CountField(this.hints, "ForbiddenDirections", Flags),
                ReadField(this.hints, "ImminentSpecialMode", Flags)?.ToString(),
                forcedMovementActive);
        }

        private static BossModGoalContribution[] SelectAdvisoryGoalContributions(IReadOnlyList<BossModGoalContribution> contributions)
        {
            return contributions
                .Where(c => c.ScoreMode == BossModGoalScoreMode.Advisory)
                .ToArray();
        }

        private bool ShouldSuppressAdvisoryMovementForCasting()
        {
            var player = this.services.ObjectTable.LocalPlayer;
            return CasterMovementPolicy.ShouldSuppressAdvisoryMovement(player);
        }

        private static BossModGoalContribution[] SuppressAdvisoryMovementForCasting(IReadOnlyList<BossModGoalContribution> contributions)
        {
            return contributions
                .Where(c => c.ScoreMode == BossModGoalScoreMode.Raw)
                .ToArray();
        }

        private static BossModGoalContribution[] SelectRawGoalContributions(IReadOnlyList<BossModGoalContribution> contributions)
        {
            var rawContributions = contributions
                .Where(c => c.ScoreMode == BossModGoalScoreMode.Raw)
                .ToArray();
            if (rawContributions.Length == 0)
            {
                return [];
            }

            var highestPriority = rawContributions.Max(c => c.Priority);
            return rawContributions
                .Where(c => c.Priority == highestPriority)
                .ToArray();
        }

        private static string FormatGoalPrioritySummary(IReadOnlyList<BossModGoalContribution> contributions)
        {
            if (contributions.Count == 0)
            {
                return "None";
            }

            var rawContributions = SelectRawGoalContributions(contributions);
            var advisoryContributions = SelectAdvisoryGoalContributions(contributions);
            if (rawContributions.Length > 0 && advisoryContributions.Length > 0)
            {
                return $"{FormatPriorityRange(advisoryContributions)} advisory + {rawContributions[0].Priority} raw";
            }

            if (rawContributions.Length > 0)
            {
                return $"{rawContributions[0].Priority} raw";
            }

            return $"{FormatPriorityRange(advisoryContributions)} advisory";
        }

        private static string FormatPriorityRange(IReadOnlyList<BossModGoalContribution> contributions)
        {
            if (contributions.Count == 0)
            {
                return "None";
            }

            var min = contributions.Min(c => c.Priority);
            var max = contributions.Max(c => c.Priority);
            return min == max ? min.ToString() : $"{min}-{max}";
        }

        private static string FormatContributionStateSuffix(bool mechanicWhisperGuardActive, bool castingSuppressed, bool mechanicSafetyIsolated)
        {
            var states = new List<string>(3);
            if (mechanicWhisperGuardActive)
            {
                states.Add("guarded");
            }

            if (castingSuppressed)
            {
                states.Add("casting suppressed");
            }

            if (mechanicSafetyIsolated)
            {
                states.Add("mechanic isolated");
            }

            return states.Count == 0 ? string.Empty : $" ({string.Join(", ", states)})";
        }

        private BossModGoalContribution[] FilterMechanicWhispers(BossModGoalContribution[] contributions)
        {
            var now = DateTime.UtcNow;
            var bossModDestination = this.ReadCurrentBossModDestination();
            var playerPosition = this.ReadCurrentPlayerPosition();
            var filtered = new List<BossModGoalContribution>(contributions.Length);
            var decisions = new List<string>(contributions.Length);
            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var contribution in contributions)
            {
                var key = MechanicWhisperKey(contribution);
                activeKeys.Add(key);
                if (!contribution.Candidate.HasValue)
                {
                    decisions.Add($"{contribution.Label}:accepted/no-candidate");
                    filtered.Add(contribution);
                    continue;
                }

                var allowed = this.ShouldAllowMechanicWhisper(contribution, key, bossModDestination, playerPosition, now, out var decision);
                decisions.Add(this.FormatMechanicWhisperDecision(contribution, decision, bossModDestination, playerPosition));
                if (allowed)
                {
                    filtered.Add(contribution);
                }
            }

            foreach (var staleKey in this.mechanicWhisperStates.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            {
                this.mechanicWhisperStates.Remove(staleKey);
            }

            this.lastMechanicWhisperStatus = decisions.Count == 0
                ? "guard active; no active contributions"
                : $"guard active; {string.Join(" | ", decisions)}";
            return filtered.ToArray();
        }

        private bool ShouldAllowMechanicWhisper(
            BossModGoalContribution contribution,
            string key,
            Vector2? bossModDestination,
            Vector2? playerPosition,
            DateTime now,
            out MechanicWhisperDecision decision)
        {
            var candidate = contribution.Candidate!.Value;
            if (!this.mechanicWhisperStates.TryGetValue(key, out var state) ||
                Vector2.Distance(candidate, state.Candidate) > MechanicWhisperCandidateResetDistance)
            {
                state = new MechanicWhisperState(candidate, now);
                this.mechanicWhisperStates[key] = state;
            }
            else
            {
                state.Candidate = candidate;
            }

            decision = BossModMechanicGoalPolicy.EvaluateMechanicWhisperCandidate(candidate, bossModDestination, playerPosition, now - state.StableSince, contribution.Confidence);
            return decision.Allowed;
        }

        private static string MechanicWhisperKey(BossModGoalContribution contribution) => $"{contribution.Priority}:{contribution.Label}";

        private string FormatMechanicWhisperDecision(
            BossModGoalContribution contribution,
            MechanicWhisperDecision decision,
            Vector2? bossModDestination,
            Vector2? playerPosition)
        {
            var state = decision.Allowed ? "accepted" : "waiting";
            var candidateDistance = playerPosition.HasValue && contribution.Candidate.HasValue
                ? Vector2.Distance(playerPosition.Value, contribution.Candidate.Value)
                : (float?)null;
            var bossModDistance = playerPosition.HasValue && bossModDestination.HasValue
                ? Vector2.Distance(playerPosition.Value, bossModDestination.Value)
                : (float?)null;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{contribution.Label}:{state}/{decision.Reason}/conf={contribution.Confidence}/stable={decision.StableFor.TotalMilliseconds:0}ms/cand={FormatNullableVector(contribution.Candidate)}/bmr={FormatNullableVector(bossModDestination)}/dist={FormatNullableNumber(candidateDistance)}->{FormatNullableNumber(bossModDistance)}");
        }

        private Vector2? ReadCurrentBossModDestination()
        {
            var controller = this.aiControllerField?.GetValue(this.ai);
            return ReadWPos(this.controllerNaviTargetPosField?.GetValue(controller));
        }

        private Vector2? ReadCurrentPlayerPosition()
        {
            var player = this.services.ObjectTable.LocalPlayer;
            return player == null ? null : new Vector2(player.Position.X, player.Position.Z);
        }

        private void SetContributorBossModMovementState(bool moveRequested, bool moveImminent)
        {
            foreach (var contributor in this.contributors)
            {
                contributor.SetBossModMovementState(moveRequested, moveImminent);
            }
        }

        private void SetContributorBossModEncounterState(bool encounterActive)
        {
            foreach (var contributor in this.contributors)
            {
                contributor.SetBossModEncounterState(encounterActive);
            }
        }

        private void CaptureMovementDiagnosticsIfDue(object? activeModule)
        {
            var now = DateTime.UtcNow;
            if (now < this.nextMovementDiagnosticsCapture)
            {
                return;
            }

            this.nextMovementDiagnosticsCapture = now.Add(MovementDiagnosticsCaptureInterval);
            this.CaptureMovementDiagnostics(activeModule);
        }

        private void CaptureMovementDiagnostics(object? activeModule)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var aiType = this.ai.GetType();
                var beh = aiType.GetField("Beh", flags)?.GetValue(this.ai);
                var controller = aiType.GetField("Controller", flags)?.GetValue(this.ai);
                var naviDecision = beh?.GetType().GetField("_naviDecision", flags)?.GetValue(beh);
                var forceMovementIn = beh?.GetType().GetField("ForceMovementIn", flags)?.GetValue(beh);

                var destination = ReadField(naviDecision, "Destination", flags);
                var nextWaypoint = ReadField(naviDecision, "NextWaypoint", flags);
                var leeway = ReadField(naviDecision, "LeewaySeconds", flags);
                var timeToGoal = ReadField(naviDecision, "TimeToGoal", flags);
                var pathfindTime = ReadField(naviDecision, "PathfindTime", flags);
                var rasterizeTime = ReadField(naviDecision, "RasterizeTime", flags);

                var naviTarget = ReadField(controller, "NaviTargetPos", flags);
                var allowCastMove = ReadField(controller, "AllowInterruptingCastByMovement", flags);
                var forceCancelCast = ReadField(controller, "ForceCancelCast", flags);
                var desiredDirection = ReadField(this.movementOverride, "DesiredDirection", flags);
                var userMove = ReadField(this.movementOverride, "UserMove", flags);
                var actualMove = ReadField(this.movementOverride, "ActualMove", flags);
                var movementBlocked = this.movementOverride.GetType().GetProperty("MovementBlocked", flags)?.GetValue(this.movementOverride);
                var hintDetails = this.BuildHintDetails(flags);
                var safetyRaster = this.BuildSafetyRaster(beh, destination, nextWaypoint, flags);

                this.movementDiagnostics = new(
                    FormatTypeName(activeModule),
                    FormatTypeName(this.activeZoneModuleField.GetValue(this.zoneModuleManager)),
                    FormatWPos(destination),
                    FormatWPos(nextWaypoint),
                    string.Join(
                        ",",
                        $"Leeway={FormatNumber(leeway)}",
                        $"TTG={FormatNumber(timeToGoal)}",
                        $"PathMs={FormatTimeMs(pathfindTime)}",
                        $"RasterMs={FormatTimeMs(rasterizeTime)}",
                        $"ForceMoveIn={FormatNumber(forceMovementIn)}",
                        $"PlannerSteer={LegacyDirectMovementStatus}",
                        $"MechanicWhisper={this.lastMechanicWhisperStatus}",
                        $"ForwardBrake={LegacyForwardBrakeStatus}"),
                    LegacyDirectMovementStatus,
                    this.lastMechanicWhisperStatus,
                    string.Join(
                        ",",
                        $"Navi={FormatWPos(naviTarget)}",
                        $"AllowCastMove={allowCastMove ?? "<none>"}",
                        $"ForceCancelCast={forceCancelCast ?? "<none>"}"),
                    string.Join(
                        ",",
                        $"Desired={FormatVector(desiredDirection)}",
                        $"User={FormatWDir(userMove)}",
                        $"Actual={FormatWDir(actualMove)}",
                        $"Blocked={movementBlocked ?? "<none>"}"),
                    BuildHintSummary(hintDetails),
                    ReadWPos(destination),
                    ReadWPos(nextWaypoint),
                    new BossModNavigationDiagnostics(
                        ReadFloat(leeway),
                        ReadFloat(timeToGoal),
                        ReadTimeMilliseconds(pathfindTime),
                        ReadTimeMilliseconds(rasterizeTime),
                        ReadFloat(forceMovementIn)),
                    new BossModControllerDiagnostics(
                        ReadWPos(naviTarget),
                        ReadBool(allowCastMove),
                        ReadBool(forceCancelCast)),
                    new BossModMovementOverrideDiagnostics(
                        ReadVector3(desiredDirection),
                        ReadWDir(userMove),
                        ReadWDir(actualMove),
                        ReadBool(movementBlocked)),
                    hintDetails,
                    safetyRaster);
            }
            catch (Exception ex)
            {
                this.movementDiagnostics = BossModMovementDiagnostics.Empty with { NavigationStats = $"diagnostics failed: {ex.Message}" };
                this.LogDiagnosticsFailure(ex, "Could not capture BossMod movement diagnostics.");
            }
        }

        private BossModHintDiagnostics BuildHintDetails(BindingFlags flags)
        {
            var bounds = ReadField(this.hints, "PathfindMapBounds", flags);
            return new BossModHintDiagnostics(
                CountField(this.hints, "GoalZones", flags),
                CountField(this.hints, "ForbiddenZones", flags),
                CountField(this.hints, "TemporaryObstacles", flags),
                CountField(this.hints, "Teleporters", flags),
                CountField(this.hints, "ForbiddenDirections", flags),
                CountField(this.hints, "PredictedDamage", flags),
                CountField(this.hints, "PotentialTargets", flags),
                ReadField(this.hints, "ImminentSpecialMode", flags)?.ToString() ?? "<none>",
                ReadVector3(ReadField(this.hints, "ForcedMovement", flags)),
                ReadFloat(ReadField(this.hints, "MaxCastTime", flags)),
                ReadBool(ReadField(this.hints, "ForceCancelCast", flags)),
                BuildBoundsDiagnostics(bounds, flags),
                ReadWPos(ReadField(this.hints, "PathfindMapCenter", flags)));
        }

        private static string BuildHintSummary(BossModHintDiagnostics details)
        {
            return string.Join(
                ",",
                $"Goals={details.GoalZones?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Forbidden={details.ForbiddenZones?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"TempObs={details.TemporaryObstacles?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Teleporters={details.Teleporters?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Directions={details.ForbiddenDirections?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Damage={details.PredictedDamage?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Potential={details.PotentialTargets?.ToString(CultureInfo.InvariantCulture) ?? "-1"}",
                $"Special={details.ImminentSpecialMode}",
                $"ForcedMovement={FormatNullableVector(details.ForcedMovement)}",
                $"MaxCast={FormatNumber(details.MaxCastTime)}",
                $"ForceCancel={details.ForceCancelCast?.ToString() ?? "<none>"}",
                $"Bounds={details.PathfindMapBounds.Text}",
                $"Center={FormatNullableVector(details.PathfindMapCenter)}");
        }

        private static object? ReadField(object? instance, string name, BindingFlags flags)
        {
            return instance?.GetType().GetField(name, flags)?.GetValue(instance);
        }

        private static int CountField(object instance, string name, BindingFlags flags)
        {
            return ReadField(instance, name, flags) is ICollection collection ? collection.Count : -1;
        }

        private static BossModBoundsDiagnostics BuildBoundsDiagnostics(object? value, BindingFlags flags)
        {
            if (value == null)
            {
                return BossModBoundsDiagnostics.Empty;
            }

            return new BossModBoundsDiagnostics(
                value.GetType().Name,
                value.ToString() ?? "<none>",
                ReadFloat(ReadMember(value, "Radius", flags)),
                ReadFloat(ReadMember(value, "HalfWidth", flags)),
                ReadFloat(ReadMember(value, "HalfHeight", flags)),
                ReadFloat(ReadMember(value, "MapResolution", flags)),
                ReadFloat(ReadMember(value, "PathfindingOffset", flags) ?? ReadMember(value, "Pathfinding offset", flags)),
                ReadInt(ReadMember(value, "Vertices", flags)),
                ReadFloat(ReadMember(value, "ScaleFactor", flags)));
        }

        private BossModSafetyRasterDiagnostics BuildSafetyRaster(object? behaviour, object? destination, object? nextWaypoint, BindingFlags flags)
        {
            if (!this.config.FightReviewLoggingEnabled)
            {
                return BossModSafetyRasterDiagnostics.Unavailable("fight review logging disabled");
            }

            try
            {
                var context = ReadField(behaviour, "_naviCtx", flags);
                if (context == null)
                {
                    var normalMovement = this.ResolveNormalMovement(flags);
                    context = ReadField(normalMovement, "_navCtx", flags);
                    if (destination == null)
                    {
                        destination = ReadField(ReadField(normalMovement, "_lastDecision", flags), "Destination", flags);
                    }
                }

                if (context == null)
                {
                    return BossModSafetyRasterDiagnostics.Unavailable("BMR navigation context unavailable");
                }

                var map = ReadField(context, "Map", flags);
                if (map == null)
                {
                    return BossModSafetyRasterDiagnostics.Unavailable("BMR navigation map unavailable");
                }

                var width = ReadInt(ReadField(map, "Width", flags)).GetValueOrDefault();
                var height = ReadInt(ReadField(map, "Height", flags)).GetValueOrDefault();
                var resolution = ReadFloat(ReadField(map, "Resolution", flags)).GetValueOrDefault();
                var pixelMaxG = ReadField(map, "PixelMaxG", flags) as float[];
                var pixelPriority = ReadField(map, "PixelPriority", flags) as float[];
                var center = ReadWPos(ReadField(map, "Center", flags));
                var rotation = ReadAngleRadians(ReadField(map, "Rotation", flags)).GetValueOrDefault();
                if (width <= 0 || height <= 0 || resolution <= 0f || pixelMaxG == null || pixelPriority == null || center == null)
                {
                    return BossModSafetyRasterDiagnostics.Unavailable("BMR navigation map incomplete");
                }

                var sourceLength = width * height;
                if (pixelMaxG.Length < sourceLength || pixelPriority.Length < sourceLength)
                {
                    return BossModSafetyRasterDiagnostics.Unavailable("BMR navigation map arrays incomplete");
                }

                var cellScale = Math.Max(1, (int)MathF.Ceiling(MathF.Max(width, height) / (float)MaxSafetyRasterDimension));
                var outWidth = (width + cellScale - 1) / cellScale;
                var outHeight = (height + cellScale - 1) / cellScale;
                var cells = new int[outWidth * outHeight];
                for (var oy = 0; oy < outHeight; oy++)
                {
                    var y0 = oy * cellScale;
                    var y1 = Math.Min(height, y0 + cellScale);
                    for (var ox = 0; ox < outWidth; ox++)
                    {
                        var x0 = ox * cellScale;
                        var x1 = Math.Min(width, x0 + cellScale);
                        var state = 0;
                        for (var sy = y0; sy < y1; sy++)
                        {
                            var row = sy * width;
                            for (var sx = x0; sx < x1; sx++)
                            {
                                state = MergeSafetyCells(state, ClassifySafetyCell(pixelMaxG[row + sx], pixelPriority[row + sx]));
                            }
                        }

                        cells[(oy * outWidth) + ox] = state;
                    }
                }

                var player = this.services.ObjectTable.LocalPlayer;
                var target = this.services.TargetManager.Target;
                var center3 = new Vector3(center.Value.X, 0f, center.Value.Y);
                return new BossModSafetyRasterDiagnostics(
                    "captured",
                    "ok",
                    center,
                    rotation,
                    resolution,
                    width,
                    height,
                    cellScale,
                    outWidth,
                    outHeight,
                    FiniteOrNull(ReadFloat(ReadField(map, "MaxG", flags))),
                    FiniteOrNull(ReadFloat(ReadField(map, "MaxPriority", flags))),
                    "rle-v1",
                    EncodeSafetyCellsRle(cells),
                    ClassifySafetyPoint(player?.Position, center3, rotation, resolution, width, height, pixelMaxG, pixelPriority),
                    ClassifySafetyPoint(ReadWPosAsVector3(destination, player?.Position.Y ?? 0f), center3, rotation, resolution, width, height, pixelMaxG, pixelPriority),
                    ClassifySafetyPoint(ReadWPosAsVector3(nextWaypoint, player?.Position.Y ?? 0f), center3, rotation, resolution, width, height, pixelMaxG, pixelPriority),
                    ClassifySafetyPoint(target?.Position, center3, rotation, resolution, width, height, pixelMaxG, pixelPriority));
            }
            catch (Exception ex)
            {
                this.LogDiagnosticsFailure(ex, "Could not capture BossMod safety raster diagnostics.");
                return BossModSafetyRasterDiagnostics.Unavailable($"capture failed: {ex.Message}");
            }
        }

        private void LogDiagnosticsFailure(Exception ex, string message)
        {
            var now = DateTime.UtcNow;
            if (now < this.nextDiagnosticsFailureLog)
            {
                return;
            }

            this.log.Verbose(ex, message);
            this.nextDiagnosticsFailureLog = now.AddSeconds(10);
        }

        private object? ResolveNormalMovement(BindingFlags flags)
        {
            const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var normalMovementType = this.plugin.GetType().Assembly.GetType("BossMod.Autorotation.MiscAI.NormalMovement");
            return normalMovementType?.GetField("Instance", StaticFlags | flags)?.GetValue(null);
        }

        private static int ClassifySafetyCell(float pixelMaxG, float pixelPriority)
        {
            if (pixelMaxG < 0f)
            {
                return 1;
            }

            if (pixelMaxG < float.MaxValue)
            {
                return pixelMaxG <= 1f ? 2 : 3;
            }

            if (pixelPriority < 0f)
            {
                return 4;
            }

            return pixelPriority > 0f ? 5 : 0;
        }

        private static int MergeSafetyCells(int current, int next)
        {
            return SafetySeverity(next) > SafetySeverity(current) ? next : current;
        }

        private static int SafetySeverity(int state)
        {
            return state switch
            {
                1 => 5,
                2 => 4,
                3 => 3,
                4 => 2,
                5 => 1,
                _ => 0
            };
        }

        private static string EncodeSafetyCellsRle(IReadOnlyList<int> cells)
        {
            if (cells.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var current = cells[0];
            var count = 1;
            for (var i = 1; i < cells.Count; i++)
            {
                if (cells[i] == current)
                {
                    count++;
                    continue;
                }

                AppendSafetyRun(builder, current, count);
                current = cells[i];
                count = 1;
            }

            AppendSafetyRun(builder, current, count);
            return builder.ToString();
        }

        private static void AppendSafetyRun(StringBuilder builder, int state, int count)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(state.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(count.ToString(CultureInfo.InvariantCulture));
        }

        private static BossModSafetyPointDiagnostics ClassifySafetyPoint(
            Vector3? position,
            Vector3 center,
            float rotation,
            float resolution,
            int width,
            int height,
            IReadOnlyList<float> pixelMaxG,
            IReadOnlyList<float> pixelPriority)
        {
            if (position == null)
            {
                return BossModSafetyPointDiagnostics.Empty;
            }

            var grid = WorldToGrid(position.Value, center, rotation, resolution, width, height);
            if (grid.x < 0 || grid.x >= width || grid.y < 0 || grid.y >= height)
            {
                return new BossModSafetyPointDiagnostics("blocked", position, grid.x, grid.y, null, null);
            }

            var index = (grid.y * width) + grid.x;
            var maxG = pixelMaxG[index];
            var priority = pixelPriority[index];
            return new BossModSafetyPointDiagnostics(
                SafetyStateName(ClassifySafetyCell(maxG, priority)),
                position,
                grid.x,
                grid.y,
                FiniteOrNull(maxG),
                FiniteOrNull(priority));
        }

        private static (int x, int y) WorldToGrid(Vector3 position, Vector3 center, float rotation, float resolution, int width, int height)
        {
            var dx = position.X - center.X;
            var dz = position.Z - center.Z;
            var sin = MathF.Sin(rotation);
            var cos = MathF.Cos(rotation);
            var gx = (width >> 1) + ((dx * cos) - (dz * sin)) / resolution;
            var gy = (height >> 1) + ((dx * sin) + (dz * cos)) / resolution;
            return ((int)MathF.Floor(gx), (int)MathF.Floor(gy));
        }

        private static string SafetyStateName(int state)
        {
            return state switch
            {
                1 => "blocked",
                2 => "active-danger",
                3 => "future-danger",
                4 => "avoid-buffer",
                5 => "goal",
                _ => "safe"
            };
        }

        private static Vector3? ReadWPosAsVector3(object? value, float y)
        {
            var position = ReadWPos(value);
            return position.HasValue ? new Vector3(position.Value.X, y, position.Value.Y) : null;
        }

        private static float? ReadAngleRadians(object? value)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            return ReadFloatField(value, type, "Rad");
        }

        private static float? FiniteOrNull(float? value)
        {
            return value.HasValue && float.IsFinite(value.Value) ? value.Value : null;
        }

        private static float? FiniteOrNull(float value)
        {
            return float.IsFinite(value) ? value : null;
        }

        private static object? ReadMember(object? instance, string name, BindingFlags flags)
        {
            if (instance == null)
            {
                return null;
            }

            var type = instance.GetType();
            return type.GetField(name, flags)?.GetValue(instance) ??
                   type.GetProperty(name, flags)?.GetValue(instance);
        }

        private static string FormatTypeName(object? instance)
        {
            return instance?.GetType().Name ?? "<none>";
        }

        private static string FormatWPos(object? value)
        {
            if (value == null)
            {
                return "<none>";
            }

            var type = value.GetType();
            var x = ReadFloatField(value, type, "X");
            var z = ReadFloatField(value, type, "Z");
            return x.HasValue && z.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"({x.Value:0.00},{z.Value:0.00})")
                : value.ToString() ?? "<none>";
        }

        private static Vector2? ReadWPos(object? value)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            var x = ReadFloatField(value, type, "X");
            var z = ReadFloatField(value, type, "Z");
            return x.HasValue && z.HasValue ? new Vector2(x.Value, z.Value) : null;
        }

        private static string FormatWDir(object? value)
        {
            if (value == null)
            {
                return "<none>";
            }

            var type = value.GetType();
            var x = ReadFloatField(value, type, "X");
            var z = ReadFloatField(value, type, "Z");
            return x.HasValue && z.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"({x.Value:0.00},{z.Value:0.00})")
                : value.ToString() ?? "<none>";
        }

        private static Vector2? ReadWDir(object? value)
        {
            if (value == null)
            {
                return null;
            }

            var type = value.GetType();
            var x = ReadFloatField(value, type, "X");
            var z = ReadFloatField(value, type, "Z");
            return x.HasValue && z.HasValue ? new Vector2(x.Value, z.Value) : null;
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return MathF.Sqrt((dx * dx) + (dz * dz));
        }

        private static float XzLengthSquared(Vector3? value)
        {
            if (!value.HasValue)
            {
                return 0f;
            }

            return (value.Value.X * value.Value.X) + (value.Value.Z * value.Value.Z);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }

        private static string FormatVector(object? value)
        {
            return value switch
            {
                null => "<none>",
                Vector3 vector => string.Create(CultureInfo.InvariantCulture, $"({vector.X:0.00},{vector.Y:0.00},{vector.Z:0.00})"),
                _ => value.ToString() ?? "<none>"
            };
        }

        private static string FormatNullableVector(Vector2? value)
        {
            return value.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"({value.Value.X:0.00},{value.Value.Y:0.00})")
                : "<none>";
        }

        private static string FormatNullableVector(Vector3? value)
        {
            return value.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"({value.Value.X:0.00},{value.Value.Y:0.00},{value.Value.Z:0.00})")
                : "<none>";
        }

        private static Vector3? ReadVector3(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is Vector3 vector)
            {
                return vector;
            }

            var type = value.GetType();
            var x = ReadFloatField(value, type, "X");
            var y = ReadFloatField(value, type, "Y");
            var z = ReadFloatField(value, type, "Z");
            return x.HasValue && y.HasValue && z.HasValue ? new Vector3(x.Value, y.Value, z.Value) : null;
        }

        private static string FormatNumber(object? value)
        {
            return value switch
            {
                null => "<none>",
                float f => f.ToString("0.00", CultureInfo.InvariantCulture),
                double d => d.ToString("0.00", CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "<none>"
            };
        }

        private static string FormatNullableNumber(float? value)
        {
            return value.HasValue
                ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
                : "<none>";
        }

        private static float? ReadFloat(object? value)
        {
            return value switch
            {
                float f when float.IsFinite(f) => f,
                double d when double.IsFinite(d) => (float)d,
                int i => i,
                uint u => u,
                _ => null
            };
        }

        private static int? ReadInt(object? value)
        {
            return value switch
            {
                int i => i,
                uint u when u <= int.MaxValue => (int)u,
                long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                _ => null
            };
        }

        private static bool? ReadBool(object? value)
        {
            return value is bool b ? b : null;
        }

        private static double? ReadTimeMilliseconds(object? value)
        {
            return value is TimeSpan timeSpan && double.IsFinite(timeSpan.TotalMilliseconds)
                ? timeSpan.TotalMilliseconds
                : null;
        }

        private static string FormatTimeMs(object? value)
        {
            return value is TimeSpan timeSpan
                ? timeSpan.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)
                : "<none>";
        }

        private bool TryEnsureMechanicEscapeMarginWPosFields()
        {
            if (this.mechanicEscapeMarginWPosType != null &&
                this.mechanicEscapeMarginWPosXField != null &&
                this.mechanicEscapeMarginWPosZField != null)
            {
                return true;
            }

            var wposType = this.hints.GetType().Assembly.GetType("BossMod.WPos");
            var xField = wposType?.GetField("X", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var zField = wposType?.GetField("Z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (wposType == null || xField == null || zField == null)
            {
                this.LogDiagnosticsFailure(
                    new MissingMemberException("BossMod.WPos"),
                    "Could not resolve BossMod.WPos for mechanic escape margin.");
                return false;
            }

            this.mechanicEscapeMarginWPosType = wposType;
            this.mechanicEscapeMarginWPosXField = xField;
            this.mechanicEscapeMarginWPosZField = zField;
            return true;
        }

        private Delegate CreateMechanicEscapeMarginGoalDelegate(Vector2 candidate)
        {
            var wposType = this.mechanicEscapeMarginWPosType!;
            var parameter = Expression.Parameter(wposType, "p");
            var x = Expression.Convert(Expression.Field(parameter, this.mechanicEscapeMarginWPosXField!), typeof(float));
            var z = Expression.Convert(Expression.Field(parameter, this.mechanicEscapeMarginWPosZField!), typeof(float));
            var score = Expression.Call(
                ScoreMechanicEscapeMarginMethod,
                x,
                z,
                Expression.Constant(candidate.X),
                Expression.Constant(candidate.Y));
            var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
            return Expression.Lambda(delegateType, score, parameter).Compile();
        }

        private static float ScoreMechanicEscapeMargin(float x, float z, float candidateX, float candidateZ)
        {
            var dx = x - candidateX;
            var dz = z - candidateZ;
            var distanceSquared = (dx * dx) + (dz * dz);
            if (distanceSquared >= MechanicEscapeMarginRadius * MechanicEscapeMarginRadius)
            {
                return 0f;
            }

            var distance = MathF.Sqrt(distanceSquared);
            return GoalZoneScorePolicy.StrongPreference * (1f - (distance / MechanicEscapeMarginRadius));
        }

        private static float? ReadFloatField(object value, Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value) switch
            {
                float f => f,
                double d => (float)d,
                _ => null
            };
        }

        private static Delegate CreateAdvisoryGoalDelegate(IReadOnlyList<BossModGoalContribution> contributions)
        {
            var invoke = Require(contributions[0].Goal.GetType().GetMethod("Invoke"), $"{contributions[0].Goal.GetType().FullName}.Invoke");
            var parameters = invoke.GetParameters();
            if (parameters.Length != 1 || invoke.ReturnType != typeof(float))
            {
                throw new InvalidOperationException($"Unexpected BossMod goal delegate signature: {contributions[0].Goal.GetType().FullName}.");
            }

            var wposType = parameters[0].ParameterType;
            var parameter = Expression.Parameter(wposType, "p");
            Expression sum = Expression.Constant(0f);
            var applyPriorityWeight = typeof(GoalZoneScorePolicy).GetMethod(nameof(GoalZoneScorePolicy.ApplyPriorityWeight), BindingFlags.Static | BindingFlags.Public)!;
            foreach (var contribution in contributions)
            {
                var contributionScore = Expression.Invoke(Expression.Constant(contribution.Goal, contribution.Goal.GetType()), parameter);
                var weightedScore = Expression.Call(
                    applyPriorityWeight,
                    contributionScore,
                    Expression.Constant(contribution.Priority),
                    Expression.Constant(contribution.AdvisoryWeight));
                sum = Expression.Add(sum, weightedScore);
            }

            var clamp = typeof(GoalZoneScorePolicy).GetMethod(nameof(GoalZoneScorePolicy.ClampAdvisoryScore), BindingFlags.Static | BindingFlags.Public)!;
            var score = Expression.Call(clamp, sum);
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
                FieldInfo field => (bool)(field.GetValue(instance) ?? false),
                PropertyInfo property => (bool)(property.GetValue(instance) ?? false),
                _ => false
            };
        }

        private sealed class MechanicWhisperState(Vector2 candidate, DateTime now)
        {
            public Vector2 Candidate = candidate;
            public DateTime StableSince = now;
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
            public FieldInfo? AiControllerField;
            public FieldInfo? ControllerNaviTargetPosField;
            public FieldInfo? MovementDesiredDirectionField;

            public MethodInfo DtrUpdateMethod = null!;
            public MethodInfo WorldStateSyncUpdateMethod = null!;
            public MethodInfo BossModuleUpdateMethod = null!;
            public PropertyInfo? ActiveBossModuleProperty;
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
