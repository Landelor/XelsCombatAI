using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XelsCombatAI.Game;

namespace XelsCombatAI.Runtime;

internal sealed class CombatComposition : IDisposable
{
    private CombatComposition(CombatRuntime runtime, DecisionOverlayController decisionOverlay, JobRangeProvider jobRangeProvider)
    {
        this.Runtime = runtime;
        this.DecisionOverlay = decisionOverlay;
        this.JobRangeProvider = jobRangeProvider;
    }

    public CombatRuntime Runtime { get; }
    public DecisionOverlayController DecisionOverlay { get; }
    public JobRangeProvider JobRangeProvider { get; }

    public static CombatComposition Create(
        Configuration config,
        DalamudServices services,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        string configDirectory,
        Action saveConfig,
        Action updateDtr,
        Action<string> print)
    {
        var bossModGate = new BossModRuntimeGate();
        var bossMod = new BossModIpc(pluginInterface, log, bossModGate);
        var avarice = new AvariceIpc(pluginInterface, log);
        var bossModSafety = new BossModReflectionSafety(pluginInterface, log, bossModGate);
        var vnavmesh = new VNavmeshIpc(pluginInterface);
        var manualMovement = new ManualMovementInputDetector();
        var autoFaceTargetOptionController = new AutoFaceTargetOptionController(config, services);
        var manualCorrectionFeedback = new ManualCorrectionFeedback();
        var rotationSolver = new RotationSolverIpc(pluginInterface, log);
        var rotationSolverActions = new RotationSolverActionReflection(pluginInterface, log);
        var dependencyChecker = new DependencyChecker(config, services, bossMod, avarice, rotationSolver);
        var jobRangeProvider = new JobRangeProvider(services);
        jobRangeProvider.Initialize();
        var targetUptimePlanner = new TargetUptimePlanner(services, bossMod, jobRangeProvider, rotationSolverActions);
        BossModPresetController? presetController = null;
        CombatRuntime? runtime = null;
        var mobilityDecisionEvaluator = new MobilityDecisionEvaluator(bossModSafety, vnavmesh, jobRangeProvider);
        var arenaEdgePositioningController = new ArenaEdgePositioningController(config, services);
        var dashStyleController = new DashStyleController(config, jobRangeProvider, arenaEdgePositioningController);
        var facingController = new FacingController(config, services, bossMod, manualMovement, new LocalPlayerFacingActuator());
        var redMageMeleeComboController = new RedMageMeleeComboController(config, services, rotationSolverActions, bossModSafety, mobilityDecisionEvaluator, facingController, () => targetUptimePlanner.CurrentTargetHasBossModule());
        var aoePackPositioningController = new AoePackPositioningController(config, services, rotationSolverActions, () => runtime?.AutomatedMovementSuppressed == true, rotationSolver, () => targetUptimePlanner.CurrentTargetHasBossModule(), jobRangeProvider);
        targetUptimePlanner.TargetUptimeRangeOverride = () =>
            redMageMeleeComboController.GetTargetUptimeRangeOverride() ??
            aoePackPositioningController.GetTargetUptimeRangeOverride();
        var positionalsController = new PositionalsController(config, services, rotationSolver, positional => presetController!.SetPositional(positional), updateDtr, () => aoePackPositioningController.Status);
        var passageOfArmsPositioningController = new PassageOfArmsPositioningController(config, services, () => runtime?.AutomatedMovementSuppressed == true);
        var healerAoePositioningController = new HealerAoePositioningController(config, services, bossMod, rotationSolverActions, () => runtime?.AutomatedMovementSuppressed == true, () => targetUptimePlanner.CurrentTargetHasBossModule(), mobilityDecisionEvaluator, facingController);
        var survivabilityZonePositioningController = new SurvivabilityZonePositioningController(config, services, () => runtime?.AutomatedMovementSuppressed == true);
        var bossCenterAvoidanceController = new BossCenterAvoidanceController(config, services, () => runtime?.AutomatedMovementSuppressed == true, () => targetUptimePlanner.CurrentTargetHasBossModule());
        var tankBehaviorController = new TankBehaviorController(config, services, () => targetUptimePlanner.CurrentTargetHasBossModule());
        IBossModGoalZoneContributor[] legacyMovementContributors = [aoePackPositioningController, passageOfArmsPositioningController, healerAoePositioningController, survivabilityZonePositioningController, bossCenterAvoidanceController, arenaEdgePositioningController, tankBehaviorController];
        var aoeGoalHook = new BossModGoalZoneHook(config, pluginInterface, services, log, bossModGate, legacyMovementContributors, manualCorrectionFeedback);
        var gapCloserController = new GapCloserController(
            config,
            services,
            bossMod,
            bossModSafety,
            jobRangeProvider,
            mobilityDecisionEvaluator,
            dashStyleController,
            facingController,
            rotationSolverActions,
            () => aoePackPositioningController.RsrHenchedActive,
            () => aoePackPositioningController.Status.TrashPull);
        var escapeGapCloserController = new EscapeGapCloserController(
            config,
            services,
            bossModSafety,
            mobilityDecisionEvaluator,
            gapCloserController,
            dashStyleController,
            facingController,
            () => aoeGoalHook.MovementDiagnostics);
        var combatLogWriter = new CombatLogWriter(Path.Combine(configDirectory, "combat-logs"), log);
        presetController = new BossModPresetController(
            config,
            services,
            bossMod,
            bossModSafety,
            targetUptimePlanner,
            positionalsController,
            gapCloserController,
            escapeGapCloserController,
            redMageMeleeComboController);

        runtime = new CombatRuntime(
            config,
            services,
            bossModGate,
            dependencyChecker,
            presetController,
            positionalsController,
            rotationSolver,
            rotationSolverActions,
            bossModSafety,
            aoeGoalHook,
            aoePackPositioningController,
            passageOfArmsPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            arenaEdgePositioningController,
            tankBehaviorController,
            redMageMeleeComboController,
            combatLogWriter,
            manualMovement,
            autoFaceTargetOptionController,
            manualCorrectionFeedback,
            mobilityDecisionEvaluator,
            gapCloserController,
            escapeGapCloserController,
            dashStyleController,
            facingController,
            jobRangeProvider,
            saveConfig,
            updateDtr,
            print);
        var decisionOverlay = new DecisionOverlayController(
            config,
            services,
            aoePackPositioningController,
            () => targetUptimePlanner.CurrentTargetHasBossModule(),
            passageOfArmsPositioningController,
            healerAoePositioningController,
            survivabilityZonePositioningController,
            bossModSafety,
            mobilityDecisionEvaluator,
            gapCloserController,
            escapeGapCloserController,
            redMageMeleeComboController,
            rotationSolverActions);

        return new CombatComposition(runtime, decisionOverlay, jobRangeProvider);
    }

    public void Dispose()
    {
        this.JobRangeProvider.Dispose();
    }
}
