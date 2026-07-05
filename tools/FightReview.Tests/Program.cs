using System.Globalization;
using System.Numerics;
using System.Text.Json;
using FightReview;
using XelsCombatAI.Combat;
using XelsCombatAI.Config;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;
using XelsCombatAI.Models;
using XelsCombatAI.Runtime;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
BossMod.Service.LogHandlerDebug = message => Console.Error.WriteLine("BMR: " + message);

var skipGameData = args.Any(arg => arg.Equals("--skip-game-data", StringComparison.OrdinalIgnoreCase));
var gameDataTests = new HashSet<string>(StringComparer.Ordinal)
{
    "BMR parser integration",
    "datamining trial duty boss context detector"
};

var tests = new (string Name, Action Body)[]
{
    ("schema v3 parsing", SchemaV3Parsing),
    ("artifact writer emits agent improvement packet", ArtifactWriterEmitsAgentImprovementPacket),
    ("uptime scoring weights melee fallback below melee range", UptimeScoringWeightsMeleeFallbackBelowMeleeRange),
    ("uptime scoring rewards greed pressure uptime", UptimeScoringRewardsGreedPressureUptime),
    ("trash pull diagnostics parsing", TrashPullDiagnosticsParsing),
    ("2D BMR pathfind center parsing", PathfindCenter2dParsing),
    ("BOM-prefixed JSONL", BomPrefixedJsonl),
    ("header metadata falls back to frames", HeaderMetadataFallsBackToFrames),
    ("schema v2 rejection", SchemaV2Rejected),
    ("configuration logging defaults/reset", ConfigurationLoggingDefaultsAndReset),
    ("legacy gap closer distance config loads", LegacyGapCloserDistanceConfigLoads),
    ("target uptime range follows next GCD", TargetUptimeRangeFollowsNextGcd),
    ("boss ring reengage probes local sidesteps first", BossRingReengageProbesLocalSidestepsFirst),
    ("positionals suppress on AoE packs", PositionalsSuppressOnAoePacks),
    ("boss center avoidance stays active for positional goals", BossCenterAvoidanceStaysActiveForPositionalGoals),
    ("positional dash policy matches rear and flank", PositionalDashPolicyMatchesRearAndFlank),
    ("positional landing scorer avoids boss center", PositionalLandingScorerAvoidsBossCenter),
    ("positional landing scorer prefers arc centers", PositionalLandingScorerPrefersArcCenters),
    ("positional true north policy prefers reachable movement", PositionalTrueNorthPolicyPrefersReachableMovement),
    ("positional true north policy samples safe partial arcs", PositionalTrueNorthPolicySamplesSafePartialArcs),
    ("positional true north policy rejects mismatched action", PositionalTrueNorthPolicyRejectsMismatchedAction),
    ("friendly anchor dash requires meaningful gain", FriendlyAnchorDashRequiresMeaningfulGain),
    ("gap closer downtime policy holds early and permits late reentry", GapCloserDowntimePolicyHoldsEarlyAndPermitsLateReentry),
    ("stable ally anchor policy rejects outliers and spread pressure", StableAllyAnchorPolicyRejectsOutliersAndSpreadPressure),
    ("stable ally anchor policy rejects pushed knockback anchors", StableAllyAnchorPolicyRejectsPushedKnockbackAnchors),
    ("melee stack recovery anchor allows blocked stack pockets", MeleeStackRecoveryAnchorAllowsBlockedStackPockets),
    ("gap closer landing policy avoids front and boss center alternatives", GapCloserLandingPolicyAvoidsFrontAndBossCenterAlternatives),
    ("gap closer pressure policy suppresses optional capped dashes", GapCloserPressurePolicySuppressesOptionalCappedDashes),
    ("reengage dash current GCD policy conserves last charge", ReengageDashCurrentGcdPolicyConservesLastCharge),
    ("trash gap closer conservation yields to spare urgent charges", TrashGapCloserConservationYieldsToSpareUrgentCharges),
    ("paired return policy holds walkable returns", PairedReturnPolicyHoldsWalkableReturns),
    ("knockback recovery direction rejects sideways dashes", KnockbackRecoveryDirectionRejectsSidewaysDashes),
    ("manual suppression allows safety gap closer only", ManualSuppressionAllowsSafetyGapCloserOnly),
    ("fixed direction pressure suppresses optional directional dashes", FixedDirectionPressureSuppressesOptionalDirectionalDashes),
    ("dancer en avant uses native dash distance", DancerEnAvantUsesNativeDashDistance),
    ("directional dash facing can pause BMR movement", DirectionalDashFacingCanPauseBmrMovement),
    ("caster immobility policy holds optional dashes", CasterImmobilityPolicyHoldsOptionalDashes),
    ("caster advisory movement holds near GCD ready", CasterAdvisoryMovementHoldsNearGcdReady),
    ("black mage leylines movement policy", BlackMageLeyLinesMovementPolicy),
    ("gap closer follows RSR auto target", GapCloserFollowsRsrAutoTarget),
    ("ranged gap closers skip boss reengage", RangedGapClosersSkipBossReengage),
    ("phantom gap closers inherit base job reengage rules", PhantomGapClosersInheritBaseJobReengageRules),
    ("knockback recovery allows timing bypass", KnockbackRecoveryAllowsTimingBypass),
    ("BMR safety dash requires destination progress", BmrSafetyDashRequiresDestinationProgress),
    ("friendly escape anchor conserves low-value ally dashes", FriendlyEscapeAnchorConservesLowValueAllyDashes),
    ("enemy movement tracker holds moving target dashes", EnemyMovementTrackerHoldsMovingTargetDashes),
    ("late safety dash continues while unsafe and far", LateSafetyDashContinuesWhileUnsafeAndFar),
    ("hostile relay dash requires target momentum", HostileRelayDashRequiresTargetMomentum),
    ("trash gap closer rejects stale pack", TrashGapCloserRejectsStalePack),
    ("samurai escape holds dash for short BMR walk", SamuraiEscapeHoldsDashForShortBmrWalk),
    ("samurai trash escape holds yaten while safe", SamuraiTrashEscapeHoldsYatenWhileSafe),
    ("BMR advisory scoring combines enabled preferences", BmrAdvisoryScoringCombinesEnabledPreferences),
    ("healer coverage ignores single missing member drift", HealerCoverageIgnoresSingleMissingMemberDrift),
    ("healer coverage uses stable natural centers", HealerCoverageUsesStableNaturalCenters),
    ("healer coverage avoids boss-center candidates", HealerCoverageAvoidsBossCenterCandidates),
    ("healer coverage pre-positions for comfort", HealerCoveragePrepositionsForComfort),
    ("healer coverage catches up for party-saving AoE heals", HealerCoverageCatchesUpForPartySavingAoeHeals),
    ("healer coverage catches up from critical party isolation", HealerCoverageCatchesUpFromCriticalPartyIsolation),
    ("healer coverage uses urgent healing awareness", HealerCoverageUsesUrgentHealingAwareness),
    ("healer coverage combines with forbidden zones", HealerCoverageCombinesWithForbiddenZones),
    ("healer coverage respects cast timing", HealerCoverageRespectsCastTiming),
    ("healer boss coverage prefers ranged comfort", HealerBossCoveragePrefersRangedComfort),
    ("party healer range moves DPS for raid damage", PartyHealerRangeMovesDpsForRaidDamage),
    ("defensive zone movement skips tanks", DefensiveZoneMovementSkipsTanks),
    ("pack movement combines with idle BossMod forbidden zones", PackMovementCombinesWithIdleBossModForbiddenZones),
    ("mechanic whisper guard keeps aligned, shorter, or confident goals", MechanicWhisperGuardKeepsAlignedShorterOrConfidentGoals),
    ("mechanic safety isolates top goal contributions", MechanicSafetyIsolatesTopGoalContributions),
    ("mechanic escape margin follows BMR movement", MechanicEscapeMarginFollowsBmrMovement),
    ("trash AoE short one-hit gain is worth moving", TrashAoeShortOneHitGainIsWorthMoving),
    ("local healer AoE fallback keeps Art of War close", LocalHealerAoeFallbackKeepsArtOfWarClose),
    ("trash AoE prep skips good-enough one-hit churn", TrashAoePrepSkipsGoodEnoughOneHitChurn),
    ("trash AoE retains equal-hit candidate", TrashAoeRetainsEqualHitCandidate),
    ("trash target retention yields to tank pack", TrashTargetRetentionYieldsToTankPack),
    ("multi-target large trash remains trash context", MultiTargetLargeTrashRemainsTrashContext),
    ("trash pull tracker phase transitions", TrashPullTrackerPhaseTransitions),
    ("trash pull tracker remote settled pack remains catch-up", TrashPullTrackerRemoteSettledPackRemainsCatchUp),
    ("BMR parser integration", BmrParserIntegration),
    ("explicit multi-encounter match scores nearest encounter", ExplicitMultiEncounterMatchScoresNearestEncounter),
    ("low-confidence auto-match rejection", LowConfidenceAutoMatchRejected),
    ("destination churn detector", DestinationChurnDetector),
    ("indecisive oscillation detector", IndecisiveOscillationDetector),
    ("movement stuck detector", MovementStuckDetector),
    ("vnavmesh detour detector", VnavmeshDetourDetector),
    ("vnavmesh offmesh destination detector", VnavmeshOffmeshDestinationDetector),
    ("vnavmesh offmesh ignored without active destination", VnavmeshOffmeshIgnoredWithoutActiveDestination),
    ("vnavmesh query stall detector", VnavmeshQueryStallDetector),
    ("vnavmesh reachable stuck detector", VnavmeshReachableStuckDetector),
    ("safety raster parsing", SafetyRasterParsing),
    ("safety raster RLE codec", SafetyRasterRleCodec),
    ("safety blocked destination detector", SafetyBlockedDestinationDetector),
    ("safety blocked route detector", SafetyBlockedRouteDetector),
    ("safety boundary stuck detector", SafetyBoundaryStuckDetector),
    ("BMR exit wall risk detector", BmrExitWallRiskDetector),
    ("Greed future danger safety linger is not blocked", GreedFutureDangerSafetyLingerIsNotBlocked),
    ("HTML includes safety raster controls", HtmlIncludesSafetyRasterControls),
    ("slow pack follow detector", SlowPackFollowDetector),
    ("slow safe corridor pack follow detector", SlowSafeCorridorPackFollowDetector),
    ("target range single boss is not trash context", TargetRangeSingleBossIsNotTrashContext),
    ("health outlier boss context detector", HealthOutlierBossContextDetector),
    ("datamining trial duty boss context detector", DataminingTrialDutyBossContextDetector),
    ("inferred boss movement hint is not BMR pressure", InferredBossMovementHintIsNotBmrPressure),
    ("movement jitter detector", MovementJitterDetector),
    ("BMR conflict detector", BmrConflictDetector),
    ("range failure detector", RangeFailureDetector),
    ("range failure ignores disabled range within role range", RangeFailureIgnoresDisabledRangeWithinRoleRange),
    ("range failure uses role range when preset range missing", RangeFailureUsesRoleRangeWhenPresetRangeMissing),
    ("range failure includes BMR pressure context", RangeFailureIncludesBmrPressureContext),
    ("range failure ignores all-pressure Greed linger", RangeFailureIgnoresAllPressureGreedLinger),
    ("range failure keeps Greed recovery failures", RangeFailureKeepsGreedRecoveryFailures),
    ("range failure ignores manual suppression", RangeFailureIgnoresManualSuppression),
    ("low value gap closer detector", LowValueGapCloserDetector),
    ("gap closer missed current GCD detector", GapCloserMissedCurrentGcdDetector),
    ("depleted gap closer opportunity detector", DepletedGapCloserOpportunityDetector),
    ("capped gap closer opportunity detector", CappedGapCloserOpportunityDetector),
    ("trash late pack engagement detector", TrashLatePackEngagementDetector),
    ("trash late pack engagement ignores BMR safety", TrashLatePackEngagementIgnoresBmrSafety),
    ("trash late pack engagement ignores in-range single target", TrashLatePackEngagementIgnoresInRangeSingleTarget),
    ("missed tank lead detector", MissedTankLeadDetector),
    ("missed tank lead ignores legacy direct goal", MissedTankLeadIgnoresLegacyDirectGoal),
    ("tank lead clamp detector", TankLeadClampDetector),
    ("straggler focus during gathering detector", StragglerFocusDuringGatheringDetector),
    ("tank lead corner failure detector", TankLeadCornerFailureDetector),
    ("route memory churn detector", RouteMemoryChurnDetector),
    ("route memory budget detector", RouteMemoryBudgetDetector),
    ("route memory unsafe rejection detector", RouteMemoryUnsafeRejectionDetector),
    ("route memory fallback detector", RouteMemoryFallbackDetector),
    ("trash AoE opportunity detector", TrashAoeOpportunityDetector),
    ("BMR trash module stays trash context", BmrTrashModuleStaysTrashContext),
    ("trash AoE opportunity ignores boss context", TrashAoeOpportunityIgnoresBossContext),
    ("persistent edge hugging detector", PersistentEdgeHuggingDetector),
    ("edge hugging suppresses all-pressure BMR windows", EdgeHuggingSuppressesAllPressureBmrWindows),
    ("edge hugging detector", EdgeHuggingDetector),
    ("trash awkward destinations ignored", TrashAwkwardDestinationsIgnored),
    ("boss center ignored as standalone", BossCenterIgnoredAsStandalone),
    ("trash defensive zone requires objective failure", TrashDefensiveZoneRequiresObjectiveFailure),
    ("defensive zone overcommit detector", DefensiveZoneOvercommitDetector),
    ("manual correction detector", ManualCorrectionDetector),
    ("manual correction golden fixture", ManualCorrectionGoldenFixture),
    ("manual correction feedback lowers advisory goals", ManualCorrectionFeedbackLowersAdvisoryGoals),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        if (skipGameData && gameDataTests.Contains(test.Name))
        {
            Console.WriteLine($"SKIP {test.Name}: game data tests disabled");
            continue;
        }

        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (SkipTestException ex)
    {
        Console.WriteLine($"SKIP {test.Name}: {ex.Message}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
        Console.Error.WriteLine(ex);
    }
}

return failures == 0 ? 0 : 1;

static void SchemaV3Parsing()
{
    var log = XcaiLogReader.Read(FixturePath("schema-v3-minimal.jsonl"));
    AssertEqual(3f, log.Header.DurationSeconds, "duration");
    AssertEqual("combat", log.Header.LogScope, "default log scope");
    AssertEqual(2, log.Frames.Count, "frame count");
    AssertEqual((uint)1234, log.Header.TerritoryType, "territory");
    AssertEqual("Standard", log.Header.CombatStyle, "default combat style");
    AssertEqual((uint)19045, log.Frames[0].TargetBaseId, "target oid");
    AssertEqual(1, log.Frames[0].Actors.Count, "actor count");
    AssertEqual("range", log.Frames[0].Planner.TopCandidates[0].Source, "candidate source");
}

static void BomPrefixedJsonl()
{
    using var temp = TempDirectory.Create();
    var path = Path.Combine(temp.Path, "bom.jsonl");
    File.WriteAllText(path, "\uFEFF" + File.ReadAllText(FixturePath("schema-v3-minimal.jsonl")));

    var log = XcaiLogReader.Read(path);
    AssertEqual(2, log.Frames.Count, "BOM frame count");
}

static void HeaderMetadataFallsBackToFrames()
{
    using var temp = TempDirectory.Create();
    var path = Path.Combine(temp.Path, "header-fallback.jsonl");
    File.WriteAllText(
        path,
        """
        {"Type":"header","SchemaVersion":3,"PluginVersion":"tests","CombatStartUtc":"2026-01-01T00:00:00Z","CombatEndUtc":"2026-01-01T00:00:01Z","DurationSeconds":1,"FrameCount":2,"PlayerClassJobId":0,"TerritoryType":0,"ContentFinderConditionId":0,"BossModActiveModule":"<none>","BossModActiveZoneModule":"<none>","Config":{"CombatStyle":"Greed"}}
        {"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:00Z","T":0,"InCombat":false,"IsDead":false,"PlayerClassJobId":0,"TerritoryType":0,"ContentFinderConditionId":0,"PlayerRotation":0,"TargetBaseId":0,"TargetObjectId":0,"TargetRotation":0,"TargetRadius":0,"TargetUptimeRange":-1,"AutomatedMovementSuppressed":false,"ManualMovementInput":"available","BossModActiveModule":"<none>","BossModActiveZoneModule":"<none>","Reason":"reset","TrashPull":{}}}
        {"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:01Z","T":1,"InCombat":true,"IsDead":false,"PlayerClassJobId":20,"TerritoryType":1314,"ContentFinderConditionId":1064,"PlayerRotation":0,"TargetBaseId":19045,"TargetObjectId":1,"TargetRotation":0,"TargetRadius":4.5,"TargetUptimeRange":2.6,"AutomatedMovementSuppressed":false,"ManualMovementInput":"available","BossModActiveModule":"D121TrenoCatoblepas","BossModActiveZoneModule":"<none>","Reason":"active","TrashPull":{}}}
        """);

    var log = XcaiLogReader.Read(path);
    AssertEqual((uint)20, log.Header.PlayerClassJobId, "fallback job");
    AssertEqual((uint)1314, log.Header.TerritoryType, "fallback territory");
    AssertEqual((uint)1064, log.Header.ContentFinderConditionId, "fallback cfcid");
    AssertEqual("D121TrenoCatoblepas", log.Header.BossModActiveModule, "fallback module");
}

static void ArtifactWriterEmitsAgentImprovementPacket()
{
    using var temp = TempDirectory.Create();
    var log = XcaiLogReader.Read(FixturePath("schema-v3-minimal.jsonl"));
    var bmr = new BmrSummary(
        "minimal-bmr.log",
        log.Header.CombatStartUtc,
        log.Header.CombatEndUtc,
        0,
        0,
        [],
        [],
        []);
    var incident = new Incident(
        "destination-churn-1-00",
        "destination-churn",
        log.Header.CombatStartUtc.AddSeconds(1),
        1f,
        "medium",
        "Planner destination changed repeatedly.",
        "Increase hold time or destination stickiness so non-defensive movement does not disrupt ABC uptime.",
        0,
        1);
    var review = new ReviewBundle(
        log,
        bmr,
        new MatchResult(bmr.Path, 0.9d, ["fixture match"]),
        [incident]);

    ArtifactWriter.Write(review, temp.Path);

    var path = Path.Combine(temp.Path, "agent.improvement.json");
    AssertTrue(File.Exists(path), "agent improvement packet exists");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    AssertEqual("agent.improvement", root.GetProperty("Type").GetString()!, "packet type");
    AssertEqual(1, root.GetProperty("SchemaVersion").GetInt32(), "packet schema");
    AssertEqual(1, root.GetProperty("Scores").GetProperty("IncidentCount").GetInt32(), "incident count");
    AssertTrue(root.GetProperty("Scores").GetProperty("RunScore").GetProperty("Overall").GetSingle() > 0, "run score emitted");
    AssertTrue(root.GetProperty("Scores").GetProperty("RunScore").GetProperty("Uptime").GetSingle() > 0, "uptime score emitted");
    AssertTrue(root.GetProperty("PositiveSignals").GetArrayLength() > 0, "positive uptime signals");
    AssertTrue(root.GetProperty("Scores").GetProperty("Uptime").GetProperty("TargetWeightedUptimePercent").GetSingle() > 0, "uptime metrics emitted");
    AssertEqual("destination-churn", root.GetProperty("NegativeSignals")[0].GetProperty("Category").GetString()!, "negative signal category");
    AssertEqual("medium", root.GetProperty("ImprovementCandidates")[0].GetProperty("Priority").GetString()!, "candidate priority");
    AssertTrue(root.GetProperty("RouteSegments").GetArrayLength() > 0, "route segment summaries");
}

static void UptimeScoringWeightsMeleeFallbackBelowMeleeRange()
{
    var log = Log(
        Frame(0, playerClassJobId: 34, targetSurfaceDistance: 2, engagementRange: 3),
        Frame(1, playerClassJobId: 34, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2, playerClassJobId: 34, targetSurfaceDistance: 20, engagementRange: 3));

    var analysis = UptimeScoring.Analyze(log);
    AssertEqual("melee-dps", analysis.Job.Role, "melee profile");
    AssertTrue(analysis.Metrics.FallbackUptimeSeconds > 0, "fallback uptime counted");
    AssertTrue(analysis.Metrics.TargetWeightedUptimePercent is > 0 and < 100, "fallback weighted below full uptime");
    AssertFalse(analysis.PositiveSignals.Any(signal => signal.Category == "melee-ranged-fallback-uptime"), "fallback is not positive melee uptime");
    AssertTrue(analysis.NegativeSignals.Any(signal => signal.Category == "melee-ranged-fallback-missed-gcd"), "melee fallback missed-GCD signal");
}

static void UptimeScoringRewardsGreedPressureUptime()
{
    var log = LogWithCombatStyle(
        "Greed",
        Frame(0, playerClassJobId: 34, targetSurfaceDistance: 2, engagementRange: 3, bmrForbiddenZones: 1),
        Frame(1, playerClassJobId: 34, targetSurfaceDistance: 2, engagementRange: 3, bmrForbiddenZones: 1),
        Frame(2, playerClassJobId: 34, targetSurfaceDistance: 2, engagementRange: 3, bmrForbiddenZones: 1));

    var analysis = UptimeScoring.Analyze(log);
    AssertTrue(analysis.PositiveSignals.Any(signal => signal.Category == "greed-pressure-uptime"), "greed pressure uptime signal");
    AssertEqual(100f, analysis.Metrics.TargetWeightedUptimePercent, "greed pressure target uptime");
}

static void PathfindCenter2dParsing()
{
    using var temp = TempDirectory.Create();
    var path = Path.Combine(temp.Path, "center2d.jsonl");
    File.WriteAllText(
        path,
        """
{"Type":"header","SchemaVersion":3,"PluginVersion":"tests","CombatStartUtc":"2026-01-01T00:00:00.0000000Z","CombatEndUtc":"2026-01-01T00:00:01.0000000Z","DurationSeconds":1,"FrameCount":1,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"BossModActiveModule":"Boss","BossModActiveZoneModule":"<none>","Config":{"FightReviewLoggingEnabled":true}}
{"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:00.0000000Z","T":0,"InCombat":true,"IsDead":false,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"PlayerPosition":{"X":84,"Y":0,"Z":388},"PlayerRotation":0,"TargetBaseId":1,"TargetObjectId":1,"TargetPosition":{"X":84,"Y":0,"Z":370},"TargetRotation":0,"TargetRadius":4.5,"TargetUptimeRange":25,"BossModActiveModule":"Boss","BossModActiveZoneModule":"<none>","MovementPlanner":{"IntentId":"<none>","ChosenSource":"<none>","Destination":null,"AcceptanceRadius":null,"SwitchReason":"none","SuppressionReason":"none","GeneratedCount":0,"AcceptedCount":0,"RejectedByReason":{},"TopCandidates":[],"ScoreBreakdown":"<none>","PathStatus":"None","BmrForcedMovement":null,"BmrForbiddenZones":0,"BmrMoveRequested":false,"BmrMoveImminent":false},"BossModMovement":{"MovementOverride":"<none>","HintSummary":"none","PlannerSteer":"legacy direct contributors","MechanicWhisper":"guard active; Target uptime:accepted/confident-redirect","HintDetails":{"PathfindMapCenter":{"X":84,"Y":370},"PathfindMapBounds":{"Radius":19.5,"HalfWidth":19.5,"HalfHeight":19.5},"GoalZones":0,"ForbiddenZones":0,"ImminentSpecialMode":"<none>"}},"Actors":[]},"Motion":{"TargetDistance":18,"TargetSurfaceDistance":13.5}}
""");

    var log = XcaiLogReader.Read(path);
    AssertEqual(new Vec3(84, 0, 370), log.Frames[0].BossMod.PathfindMapCenter!, "2D center");
    AssertContains("confident-redirect", log.Frames[0].BossMod.MechanicWhisper, "mechanic whisper parse");
}

static void TrashPullDiagnosticsParsing()
{
    using var temp = TempDirectory.Create();
    var path = Path.Combine(temp.Path, "trash-pull.jsonl");
    File.WriteAllText(
        path,
        """
{"Type":"header","SchemaVersion":3,"PluginVersion":"tests","CombatStartUtc":"2026-01-01T00:00:00.0000000Z","CombatEndUtc":"2026-01-01T00:00:01.0000000Z","DurationSeconds":1,"FrameCount":1,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"BossModActiveModule":"<none>","BossModActiveZoneModule":"<none>","Config":{"FightReviewLoggingEnabled":true,"LeadTrashPullsWithTank":true}}
{"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:00.0000000Z","T":0,"InCombat":true,"IsDead":false,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"PlayerPosition":{"X":0,"Y":0,"Z":0},"PlayerRotation":0,"TargetBaseId":1,"TargetObjectId":2,"TargetPosition":{"X":10,"Y":0,"Z":0},"TargetRotation":0,"TargetRadius":1,"TargetUptimeRange":3,"BossModActiveModule":"<none>","BossModActiveZoneModule":"<none>","Reason":"test","TrashPull":{"Phase":"Gathering","Confidence":0.8,"Reason":"tank dragging","TankObjectId":10,"TankPosition":{"X":8,"Y":0,"Z":0},"TankVelocity":{"X":4,"Y":0,"Z":0},"TankSpeed":4,"ProjectedTankPosition":{"X":10,"Y":0,"Z":0},"LeadDestination":{"X":6,"Y":0,"Z":0},"LeadCandidateActive":true,"LeadClampApplied":true,"BehindDistance":8,"PackCentroid":{"X":9,"Y":0,"Z":0},"PackVelocity":{"X":2,"Y":0,"Z":0},"PackSpeed":2,"PartyMedianSpeed":5,"DominantTargetCount":3,"StragglerTargetCount":1,"DominantTargetIds":[2,3,4],"StragglerTargetIds":[5],"LeadRejectionReason":"clamped behind tank"},"MovementPlanner":{"IntentId":"<none>","ChosenSource":"<none>","Destination":null,"AcceptanceRadius":null,"SwitchReason":"none","SuppressionReason":"none","GeneratedCount":0,"AcceptedCount":0,"RejectedByReason":{},"TopCandidates":[],"ScoreBreakdown":"<none>","PathStatus":"None","BmrForcedMovement":null,"BmrForbiddenZones":0,"BmrMoveRequested":false,"BmrMoveImminent":false,"RouteMemory":{"Active":true,"State":"active","Source":"tank-trail","Reason":"following tank trail","RouteGoal":{"X":5,"Y":0,"Z":0},"LocalDestination":{"X":4,"Y":0,"Z":1.5},"NextWaypoint":{"X":3,"Y":0,"Z":1},"OffsetSide":1,"OffsetDistance":1.5,"RouteAgeMilliseconds":750,"WaypointIndex":1,"WaypointCount":3,"VnavStatus":"Reachable","QueryBudgetUsed":1,"QueryBudgetLimit":3,"InvalidationReason":"<none>","TankTrail":[{"X":0,"Y":0,"Z":0},{"X":5,"Y":0,"Z":0}]}},"BossModMovement":{"MovementOverride":"<none>","HintSummary":"none","HintDetails":{"PathfindMapCenter":{"X":0,"Y":0},"PathfindMapBounds":{"Radius":20},"GoalZones":0,"ForbiddenZones":0,"ImminentSpecialMode":"<none>"}},"Actors":[]},"Motion":{"TargetDistance":10,"TargetSurfaceDistance":9}}
""");

    var log = XcaiLogReader.Read(path);
    AssertEqual("Gathering", log.Frames[0].TrashPull.Phase, "trash phase");
    AssertEqual((ulong)10, log.Frames[0].TrashPull.TankObjectId, "tank object id");
    AssertTrue(log.Frames[0].TrashPull.LeadClampApplied, "lead clamp");
    AssertEqual((ulong)5, log.Frames[0].TrashPull.StragglerTargetIds[0], "straggler id");
    AssertTrue(log.Frames[0].Planner.RouteMemory.Active, "route memory active");
    AssertEqual("tank-trail", log.Frames[0].Planner.RouteMemory.Source, "route memory source");
    AssertEqual(2, log.Frames[0].Planner.RouteMemory.TankTrail.Count, "route memory trail count");
}

static void SchemaV2Rejected()
{
    var ex = AssertThrows<InvalidDataException>(() => XcaiLogReader.Read(FixturePath("schema-v2.jsonl")));
    AssertContains("schema version 2", ex.Message, "schema rejection");
}

static void ConfigurationLoggingDefaultsAndReset()
{
    var config = new Configuration();
    var currentVersion = config.Version;
    AssertFalse(config.FightReviewLoggingEnabled, "default logging disabled");
    AssertTrue(config.UseGapCloser, "default gap closers enabled");
    AssertFalse(config.UsePhantomGapClosers, "default Phantom dashes disabled");
    AssertFalse(config.IsGapCloserJobEnabled(31), "MCH has no native gap closer allow-list entry");
    AssertTrue(config.IsPhantomGapCloserJobEnabled(31), "MCH Phantom dashes follow physical ranged archetype");
    AssertTrue(config.IsPhantomGapCloserJobEnabled(33), "AST Phantom dashes follow healer archetype");
    AssertTrue(config.IsPhantomGapCloserJobEnabled(27), "SMN Phantom dashes follow magic ranged archetype");
    AssertFalse(config.IsPhantomGapCloserJobEnabled(0), "unknown job has no Phantom dash archetype");

    config.GapCloserBRD = false;
    config.GapCloserDNC = false;
    AssertFalse(config.IsPhantomGapCloserJobEnabled(31), "physical ranged Phantom archetype respects physical ranged dash toggles");
    config.GapCloserBRD = true;
    config.GapCloserDNC = true;

    config.FightReviewLoggingEnabled = true;
    config.UseGapCloser = false;
    config.UsePhantomGapClosers = true;
    config.ResetAll();
    AssertFalse(config.FightReviewLoggingEnabled, "reset disables logging");
    AssertTrue(config.UseGapCloser, "reset enables gap closers");
    AssertFalse(config.UsePhantomGapClosers, "reset disables Phantom dashes");

    config.Version = 18;
    config.FightReviewLoggingEnabled = true;
    config.UseGapCloser = false;
    config.UsePhantomGapClosers = true;
    config.Migrate();
    AssertEqual(currentVersion, config.Version, "migrated version");
    AssertTrue(config.UseGapCloser, "migration enables gap closers by default");
    AssertFalse(config.UsePhantomGapClosers, "migration disables Phantom dashes");

    var oldLoggingConfig = new Configuration
    {
        Version = 16,
        FightReviewLoggingEnabled = true,
    };
    oldLoggingConfig.Migrate();
    AssertEqual(currentVersion, oldLoggingConfig.Version, "old logging config migrated version");
    AssertFalse(oldLoggingConfig.FightReviewLoggingEnabled, "migration disables logging");
    AssertTrue(oldLoggingConfig.ManageSocialTurning, "migration enables social turning");
    AssertTrue(oldLoggingConfig.ManageSocialSpacing, "migration enables social spacing");
}

static void LegacyGapCloserDistanceConfigLoads()
{
    var json = """
        {
          "Version": 16,
          "UseGapCloser": true,
          "MinimumGapCloserDistance": 19
        }
        """;
    var config = JsonSerializer.Deserialize<Configuration>(json)!;
    config.Migrate();
    config.Clamp();

    AssertTrue(config.UseGapCloser, "legacy gap closer enablement still migrates");
    AssertEqual(new Configuration().Version, config.Version, "legacy distance config migrates to current version");
}

static void TargetUptimeRangeFollowsNextGcd()
{
    AssertEqual(
        Configuration.InternalMeleeUptimeRange,
        TargetUptimePlanner.ResolveTargetUptimeRange(RangeRole.Melee, Configuration.InternalMeleeUptimeRange, 25f),
        "melee jobs should still close to melee range even when the next action has long range");

    AssertEqual(
        25f,
        TargetUptimePlanner.ResolveTargetUptimeRange(RangeRole.MagicRanged, Configuration.InternalRangedUptimeRange, 25f),
        "ranged jobs should use the upcoming offensive GCD range");

    AssertEqual(
        3f,
        TargetUptimePlanner.ResolveTargetUptimeRange(RangeRole.PhysicalRanged, Configuration.InternalRangedUptimeRange, 1f),
        "invalid tiny action ranges clamp to minimum action range");

    AssertEqual(
        15f,
        TargetUptimePlanner.ResolveTargetUptimeRange(RangeRole.PhysicalRanged, Configuration.InternalRangedUptimeRange, 0f, 38, 15997, 15997),
        "DNC dance setup should close to dance finish range instead of ranged fallback");
}

static void BossRingReengageProbesLocalSidestepsFirst()
{
    var player = new Vector3(0f, 0f, 10f);
    var target = Vector3.Zero;
    var candidates = BossModPresetController.EnumerateBossRingCandidates(player, target, 10f).ToArray();

    AssertEqual(24, candidates.Length, "boss ring candidate count");
    AssertApproximately(0f, candidates[0].X, 0.001f, "first candidate preserves current radial x");
    AssertApproximately(10f, candidates[0].Z, 0.001f, "first candidate preserves current radial z");
    AssertApproximately(-2.588f, candidates[1].X, 0.001f, "second candidate samples left sidestep");
    AssertApproximately(9.659f, candidates[1].Z, 0.001f, "second candidate stays near player radial");
    AssertApproximately(2.588f, candidates[2].X, 0.001f, "third candidate samples right sidestep");
    AssertApproximately(9.659f, candidates[2].Z, 0.001f, "third candidate stays near player radial");

    AssertTrue(
        Vector3.Distance(player, candidates[1]) < Vector3.Distance(player, candidates[^1]),
        "near sidesteps should be considered before cross-boss reengage points");
}

static void PositionalsSuppressOnAoePacks()
{
    AssertTrue(
        PositionalsController.ShouldSuppressPositionalsForAoePack(AoePackStatus(priorityTargetCount: 2, trashContext: true)),
        "multiple priority targets should suppress positional chasing");

    AssertTrue(
        PositionalsController.ShouldSuppressPositionalsForAoePack(AoePackStatus(trashPull: TrashDiagnostics(dominantTargetCount: 2), trashContext: true)),
        "trash pack diagnostics should suppress positional chasing");

    AssertFalse(
        PositionalsController.ShouldSuppressPositionalsForAoePack(AoePackStatus(priorityTargetCount: 2, bossModuleContext: true)),
        "real boss module context should keep positional handling available even with multiple priority targets");

    AssertTrue(
        PositionalsController.ShouldSuppressPositionalsForAoePack(AoePackStatus(priorityTargetCount: 2, bossModuleContext: true, trashContext: true)),
        "hallway trash context should still suppress positionals even when a BMR module is active");

    AssertFalse(
        PositionalsController.ShouldSuppressPositionalsForAoePack(AoePackStatus(priorityTargetCount: 1)),
        "single-target context should keep positional handling available");
}

static void BossCenterAvoidanceStaysActiveForPositionalGoals()
{
    var leyLinesContribution = new BossModGoalContribution(
        new Func<int, float>(_ => 0f),
        BossModGoalPriority.Uptime,
        BlackMageLeyLinesPositioningController.GoalLabel);
    var positionalContribution = new BossModGoalContribution(
        new Func<int, float>(_ => 0f),
        BossModGoalPriority.Uptime,
        "Positional landing");

    AssertTrue(
        BossCenterAvoidanceController.HasActiveLeyLinesGoal([leyLinesContribution]),
        "Ley Lines movement should suppress boss center comfort avoidance");

    AssertFalse(
        BossCenterAvoidanceController.HasActiveLeyLinesGoal([positionalContribution]),
        "normal positional goals should keep boss center comfort avoidance available");

    AssertTrue(
        BossCenterAvoidanceController.ShouldSuppressForBossModGoalZone(goalZoneActive: true, recommendedPositionalActive: false),
        "non-positional BossMod goal zones should suppress center comfort movement");

    AssertFalse(
        BossCenterAvoidanceController.ShouldSuppressForBossModGoalZone(goalZoneActive: true, recommendedPositionalActive: true),
        "positional BossMod goal zones should allow center comfort movement");

    AssertFalse(
        BossCenterAvoidanceController.ShouldSuppressForBossModGoalZone(goalZoneActive: false, recommendedPositionalActive: false),
        "missing BossMod goal zone should not suppress center comfort movement");
}

static void PositionalDashPolicyMatchesRearAndFlank()
{
    var targetPosition = Vector3.Zero;
    const float targetRotation = 0f;

    AssertTrue(
        PositionalDashPolicy.IsSatisfied(Positional.Rear, new Vector3(0f, 0f, -5f), targetPosition, targetRotation),
        "rear candidate should satisfy rear positional");
    AssertFalse(
        PositionalDashPolicy.IsSatisfied(Positional.Rear, new Vector3(5f, 0f, 0f), targetPosition, targetRotation),
        "flank candidate should not satisfy rear positional");
    AssertTrue(
        PositionalDashPolicy.IsSatisfied(Positional.Flank, new Vector3(5f, 0f, 0f), targetPosition, targetRotation),
        "side candidate should satisfy flank positional");
    AssertFalse(
        PositionalDashPolicy.IsSatisfied(Positional.Flank, new Vector3(0f, 0f, -5f), targetPosition, targetRotation),
        "rear candidate should not satisfy flank positional");

    var flankLandings = PositionalDashPolicy.EnumeratePreferredLandings(
        new Vector3(4f, 0f, 1f),
        targetPosition,
        targetRotation,
        5f,
        Positional.Flank).ToArray();
    AssertEqual(2, flankLandings.Length, "flank should offer both side landings");
    AssertApproximately(5f, flankLandings[0].X, 0.001f, "nearest flank side should be first");
    AssertApproximately(-5f, flankLandings[1].X, 0.001f, "far flank side should remain available");
}

static void PositionalLandingScorerAvoidsBossCenter()
{
    const float targetRotation = 0f;
    const float targetRadius = 5f;
    const float playerRadius = 0.5f;
    var flankCenterDistance = targetRadius + playerRadius + 1.2f;

    AssertTrue(
        PositionalsController.ScoreLanding(
            x: flankCenterDistance,
            z: 0f,
            targetX: 0f,
            targetZ: 0f,
            targetRotation,
            targetRadius,
            playerRadius,
            Positional.Flank) > 0f,
        "comfortable flank landing should score");

    AssertEqual(
        0f,
        PositionalsController.ScoreLanding(
            x: 0f,
            z: 0f,
            targetX: 0f,
            targetZ: 0f,
            targetRotation,
            targetRadius,
            playerRadius,
            Positional.Flank),
        "boss center should not score as a final positional landing");

    AssertTrue(
        PositionalsController.ScoreLanding(
            x: targetRadius + playerRadius + 0.2f,
            z: 0f,
            targetX: 0f,
            targetZ: 0f,
            targetRotation,
            targetRadius,
            playerRadius,
            Positional.Flank) > 0f,
        "tight but usable flank landing should still score");

    AssertTrue(
        PositionalsController.ScoreLanding(
            x: targetRadius * 0.5f,
            z: 0f,
            targetX: 0f,
            targetZ: 0f,
            targetRotation,
            targetRadius,
            playerRadius,
            Positional.Flank) > 0f,
        "inside-hitbox flank landing should remain available when BMR safety leaves it as the best safe option");

    AssertEqual(
        0f,
        PositionalsController.ScoreLanding(
            x: 0f,
            z: -flankCenterDistance,
            targetX: 0f,
            targetZ: 0f,
            targetRotation,
            targetRadius,
            playerRadius,
            Positional.Flank),
        "wrong positional arc should not score");
}

static void PositionalLandingScorerPrefersArcCenters()
{
    const float targetRotation = 0f;
    const float targetRadius = 5f;
    const float playerRadius = 0.5f;
    var radius = targetRadius + playerRadius + 1.2f;

    var flankCenter = PositionalsController.ScoreLanding(
        x: radius,
        z: 0f,
        targetX: 0f,
        targetZ: 0f,
        targetRotation,
        targetRadius,
        playerRadius,
        Positional.Flank);

    var flankEdge = PositionalsController.ScoreLanding(
        x: radius * 0.72f,
        z: radius * 0.69f,
        targetX: 0f,
        targetZ: 0f,
        targetRotation,
        targetRadius,
        playerRadius,
        Positional.Flank);

    AssertTrue(flankCenter > flankEdge, "flank center should score above a valid edge landing");
    AssertTrue(flankEdge > 0f, "valid flank edge landing should remain available for mechanics");

    var rearCenter = PositionalsController.ScoreLanding(
        x: 0f,
        z: -radius,
        targetX: 0f,
        targetZ: 0f,
        targetRotation,
        targetRadius,
        playerRadius,
        Positional.Rear);

    var rearEdge = PositionalsController.ScoreLanding(
        x: radius * 0.69f,
        z: -radius * 0.72f,
        targetX: 0f,
        targetZ: 0f,
        targetRotation,
        targetRadius,
        playerRadius,
        Positional.Rear);

    AssertTrue(rearCenter > rearEdge, "rear center should score above a valid edge landing");
    AssertTrue(rearEdge > 0f, "valid rear edge landing should remain available for mechanics");
}

static void PositionalTrueNorthPolicyPrefersReachableMovement()
{
    var action = CreatePositionalAction(
        adjustedActionId: 7481,
        actionName: "Gekko",
        gcdRemaining: 2.3f,
        gcdElapsed: 0.2f,
        gcdTotal: 2.5f,
        gcdActionAhead: 0.35f);

    AssertTrue(
        PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(Positional.Rear, action, moveDistance: 6f, out var reason),
        $"reachable rear movement should preserve True North: {reason}");

    AssertFalse(
        PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(Positional.Rear, action, moveDistance: 12f, out _),
        "late rear movement should fall back to True North");
}

static void PositionalTrueNorthPolicySamplesSafePartialArcs()
{
    var playerPosition = new Vector3(3f, 0f, 0f);
    var targetPosition = Vector3.Zero;
    const float targetRotation = 0f;

    AssertTrue(
        PositionalTrueNorthPolicy.TryEstimateWalkDistance(
            playerPosition,
            playerHitboxRadius: 0f,
            targetPosition,
            targetHitboxRadius: 0f,
            targetRotation,
            Positional.Rear,
            candidateAllowed: candidate => candidate.X > 1f,
            out var moveDistance,
            out var reason),
        $"partially safe rear arc should still produce a walk candidate: {reason}");

    AssertTrue(
        moveDistance < 3f,
        $"nearest safe rear edge should be weighed before True North; got {moveDistance:0.00}y");
}

static void PositionalTrueNorthPolicyRejectsMismatchedAction()
{
    var action = CreatePositionalAction(
        adjustedActionId: 7482,
        actionName: "Kasha",
        gcdRemaining: 2.3f,
        gcdElapsed: 0.2f,
        gcdTotal: 2.5f,
        gcdActionAhead: 0.35f);

    AssertFalse(
        PositionalTrueNorthPolicy.ShouldWalkInsteadOfTrueNorth(Positional.Rear, action, moveDistance: 1f, out _),
        "rear intent should not use a flank RSR action timing snapshot");
}

static RsrGcdActionTimingSnapshot CreatePositionalAction(uint adjustedActionId, string actionName, float gcdRemaining, float gcdElapsed, float gcdTotal, float gcdActionAhead)
{
    return new RsrGcdActionTimingSnapshot(
        ActionId: adjustedActionId,
        AdjustedActionId: adjustedActionId,
        ActionName: actionName,
        Source: "test",
        PrimaryTargetId: 0,
        GcdRemaining: gcdRemaining,
        GcdElapsed: gcdElapsed,
        GcdTotal: gcdTotal,
        GcdActionAhead: gcdActionAhead);
}

static void FriendlyAnchorDashRequiresMeaningfulGain()
{
    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 3.1f,
            safetyGain: 0f,
            uptimeGain: 2.4f,
            pathGain: 0f,
            out var closeReason),
        "nearby ally anchor should not spend a healer dash");
    AssertContains("too close", closeReason, "nearby ally rejection reason");

    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 8f,
            safetyGain: 0f,
            uptimeGain: 2f,
            pathGain: 0f,
            out var lowGainReason),
        "distant ally anchor should still need meaningful uptime gain");
    AssertContains("low uptime", lowGainReason, "low uptime ally rejection reason");

    AssertTrue(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 8f,
            safetyGain: 0f,
            uptimeGain: 5f,
            pathGain: 0f,
            out _),
        "meaningful uptime anchor is allowed");

    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 3f,
            safetyGain: 1f,
            uptimeGain: 0f,
            pathGain: 0f,
            out _),
        "short ally safety anchor should not spend a dash");

    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 3f,
            safetyGain: 0f,
            uptimeGain: 0f,
            pathGain: 1f,
            out _),
        "short ally path recovery anchor should not spend a dash");

    AssertTrue(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 6.5f,
            safetyGain: 1f,
            uptimeGain: 0f,
            pathGain: 0f,
            out _),
        "meaningful-distance safety anchor is allowed");

    AssertTrue(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            moveDistance: 6.5f,
            safetyGain: 0f,
            uptimeGain: 0f,
            pathGain: 1f,
            out _),
        "meaningful-distance path recovery anchor is allowed");

    var player = new Vector3(0f, 0f, 0f);
    var boss = new Vector3(0f, 0f, 40f);
    var sideAnchor = new Vector3(20f, 0f, 12f);
    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            playerPosition: player,
            playerRadius: 0f,
            anchorPosition: sideAnchor,
            targetPosition: boss,
            targetRadius: 0f,
            moveDistance: Geometry.Distance2D(player, sideAnchor),
            safetyGain: 0f,
            uptimeGain: 6f,
            pathGain: 0f,
            out var sideReason),
        "far-side ally anchor should not win from marginal boss progress");
    AssertContains("sideways", sideReason, "side ally rejection reason");

    var marginalAnchor = new Vector3(12f, 0f, 4.5f);
    AssertFalse(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            playerPosition: player,
            playerRadius: 0f,
            anchorPosition: marginalAnchor,
            targetPosition: boss,
            targetRadius: 0f,
            moveDistance: Geometry.Distance2D(player, marginalAnchor),
            safetyGain: 0f,
            uptimeGain: 5f,
            pathGain: 0f,
            out var marginalReason),
        "ally anchor should need real target progress, not only a range bonus");
    AssertContains("low target progress", marginalReason, "marginal ally rejection reason");

    var forwardAnchor = new Vector3(8f, 0f, 12f);
    AssertTrue(
        GapCloserController.ShouldUseFriendlyAnchorDash(
            playerPosition: player,
            playerRadius: 0f,
            anchorPosition: forwardAnchor,
            targetPosition: boss,
            targetRadius: 0f,
            moveDistance: Geometry.Distance2D(player, forwardAnchor),
            safetyGain: 0f,
            uptimeGain: 8f,
            pathGain: 0f,
            out _),
        "forward-diagonal ally anchor remains valid for meaningful boss reengage");
}

static void GapCloserDowntimePolicyHoldsEarlyAndPermitsLateReentry()
{
    var downtimeSoon = BossModMechanicPressure.None with { BMRDowntimeIn = 1.2f };
    AssertTrue(
        GapCloserDecisionPolicy.ShouldHoldReengageForMechanicPressure(
            downtimeSoon,
            walkingWouldMissUsefulGcd: true,
            safetyDash: false,
            strongStyleGainRequired: false,
            out var downtimeReason),
        "pre-downtime reengage should hold even when a GCD could be forced");
    AssertContains("downtime", downtimeReason, "pre-downtime hold reason");

    var vulnerableSoon = BossModMechanicPressure.None with { BMRDowntimeEndIn = 0.8f, BMRVulnerableIn = 0.8f };
    AssertFalse(
        GapCloserDecisionPolicy.ShouldUsePostDowntimeReengage(
            targetAttackable: false,
            walkingWouldMissUsefulGcd: true,
            vulnerableSoon,
            out var untargetableReason),
        "post-downtime reentry should not dash before target validation");
    AssertContains("not attackable", untargetableReason, "untargetable downtime reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUsePostDowntimeReengage(
            targetAttackable: true,
            walkingWouldMissUsefulGcd: false,
            vulnerableSoon,
            out var walkReason),
        "post-downtime reentry should walk when it can still make the offensive GCD");
    AssertContains("walking", walkReason, "walkable downtime reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUsePostDowntimeReengage(
            targetAttackable: true,
            walkingWouldMissUsefulGcd: true,
            vulnerableSoon,
            out _),
        "post-downtime reentry should allow a safe dash when walking would miss the next useful GCD");
}

static void StableAllyAnchorPolicyRejectsOutliersAndSpreadPressure()
{
    var player = Vector3.Zero;
    var target = new Vector3(0f, 0f, 30f);
    var stableAnchor = new Vector3(0f, 0f, 12f);
    var party = new[]
    {
        new Vector3(0f, 0f, 10f),
        new Vector3(1f, 0f, 11f),
        new Vector3(-1f, 0f, 9f)
    };

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            stableAnchor,
            target,
            targetRadius: 0f,
            safeDestination: new Vector3(0f, 0f, 14f),
            party,
            BossModMechanicPressure.None,
            currentPositionUnsafe: false,
            moveDistance: 12f,
            safetyGain: 0f,
            uptimeGain: 8f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out _),
        "stable ally near the party and safe destination should remain a valid anchor");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            anchorPosition: new Vector3(20f, 0f, 12f),
            target,
            targetRadius: 0f,
            safeDestination: new Vector3(0f, 0f, 14f),
            party,
            BossModMechanicPressure.None,
            currentPositionUnsafe: false,
            moveDistance: 23f,
            safetyGain: 0f,
            uptimeGain: 8f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out var outlierReason),
        "far party outliers should not become stack anchors");
    AssertContains("sideways", outlierReason, "outlier anchor reason");

    var sharedDamage = BossModMechanicPressure.None with
    {
        BMRDamageIn = 1f,
        BMRNextDamageType = (int)BossModPredictedDamageType.Shared
    };
    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            stableAnchor,
            target,
            targetRadius: 0f,
            safeDestination: null,
            party,
            sharedDamage,
            currentPositionUnsafe: false,
            moveDistance: 12f,
            safetyGain: 0f,
            uptimeGain: 8f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out var spreadReason),
        "optional ally-target dashes should hold during spread/shared-damage pressure");
    AssertContains("shared damage", spreadReason, "shared damage anchor reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            stableAnchor,
            target,
            targetRadius: 0f,
            safeDestination: null,
            party,
            sharedDamage,
            currentPositionUnsafe: true,
            moveDistance: 12f,
            safetyGain: 2f,
            uptimeGain: 0f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out _),
        "ally-target safety dashes may still proceed when the current position is unsafe");
}

static void StableAllyAnchorPolicyRejectsPushedKnockbackAnchors()
{
    var player = new Vector3(0f, 0f, -20f);
    var target = Vector3.Zero;
    var pushedAnchor = new Vector3(0f, 0f, -10f);
    var stableMeleeAnchor = new Vector3(0f, 0f, -3f);
    var knockbackRecovery = BossModMechanicPressure.None.WithKnockbackRecoveryUntil(DateTime.UtcNow.AddSeconds(1));

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            pushedAnchor,
            target,
            targetRadius: 0f,
            safeDestination: null,
            partyPositions: [pushedAnchor, new Vector3(0.5f, 0f, -10.5f), new Vector3(-0.5f, 0f, -9.5f)],
            knockbackRecovery,
            currentPositionUnsafe: false,
            moveDistance: Geometry.Distance2D(player, pushedAnchor),
            safetyGain: 0f,
            uptimeGain: 10f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out var pushedReason),
        "knockback recovery should not Thunderclap to a party member who is still far from the boss");
    AssertContains("knockback recovery", pushedReason, "pushed knockback anchor rejection reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            stableMeleeAnchor,
            target,
            targetRadius: 0f,
            safeDestination: null,
            partyPositions: [stableMeleeAnchor, new Vector3(0.5f, 0f, -3.2f), new Vector3(-0.5f, 0f, -2.8f)],
            knockbackRecovery,
            currentPositionUnsafe: false,
            moveDistance: Geometry.Distance2D(player, stableMeleeAnchor),
            safetyGain: 0f,
            uptimeGain: 17f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: false,
            out _),
        "knockback recovery should still allow an ally anchor that is already a useful melee landing");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseStableFriendlyAnchorDash(
            player,
            playerRadius: 0f,
            pushedAnchor,
            target,
            targetRadius: 0f,
            safeDestination: null,
            partyPositions: [pushedAnchor, new Vector3(0.5f, 0f, -10.5f), new Vector3(-0.5f, 0f, -9.5f)],
            knockbackRecovery,
            currentPositionUnsafe: false,
            moveDistance: Geometry.Distance2D(player, pushedAnchor),
            safetyGain: 0f,
            uptimeGain: 10f,
            pathGain: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            anchorAntiKnockbackActive: true,
            out _),
        "knockback recovery should allow a pushed-line ally anchor when anti-knockback visibly held them in place");
}

static void MeleeStackRecoveryAnchorAllowsBlockedStackPockets()
{
    var target = Vector3.Zero;
    var anchorInMeleeStack = new Vector3(2.4f, 0f, 0.4f);
    var partyStack = new[]
    {
        anchorInMeleeStack,
        new Vector3(2.2f, 0f, 0.6f),
        new Vector3(2.7f, 0f, 0.2f)
    };
    var sharedDamage = BossModMechanicPressure.None with
    {
        BMRDamageIn = 1f,
        BMRNextDamageType = (int)BossModPredictedDamageType.Shared
    };

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
            playerIsMeleeRangeRole: true,
            reengageWalkBlocked: true,
            anchorPosition: anchorInMeleeStack,
            targetPosition: target,
            playerRadius: 0f,
            targetRadius: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            partyPositions: partyStack,
            pressure: sharedDamage,
            moveDistance: 12f,
            out var stackReason),
        "blocked melee reengage should allow a safe ally stack pocket even during shared-damage pressure");
    AssertContains("safe melee pocket", stackReason, "melee stack recovery reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
            playerIsMeleeRangeRole: true,
            reengageWalkBlocked: false,
            anchorPosition: anchorInMeleeStack,
            targetPosition: target,
            playerRadius: 0f,
            targetRadius: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            partyPositions: partyStack,
            pressure: BossModMechanicPressure.None,
            moveDistance: 12f,
            out var walkReason),
        "walkable reengage should keep using the normal ally-anchor policy");
    AssertContains("not blocked", walkReason, "walkable melee stack recovery reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
            playerIsMeleeRangeRole: true,
            reengageWalkBlocked: true,
            anchorPosition: new Vector3(6f, 0f, 0f),
            targetPosition: target,
            playerRadius: 0f,
            targetRadius: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            partyPositions: partyStack,
            pressure: BossModMechanicPressure.None,
            moveDistance: 12f,
            out var rangeReason),
        "blocked melee recovery should still require an ally inside melee range");
    AssertContains("outside melee", rangeReason, "range rejection reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseMeleeStackRecoveryAnchorDash(
            playerIsMeleeRangeRole: true,
            reengageWalkBlocked: true,
            anchorPosition: anchorInMeleeStack,
            targetPosition: target,
            playerRadius: 0f,
            targetRadius: 0f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            partyPositions: [anchorInMeleeStack],
            pressure: BossModMechanicPressure.None,
            moveDistance: 12f,
            out var soloReason),
        "blocked melee recovery should not treat one isolated player as a mechanic stack");
    AssertContains("party stack", soloReason, "solo anchor rejection reason");
}

static void GapCloserLandingPolicyAvoidsFrontAndBossCenterAlternatives()
{
    var target = Vector3.Zero;
    var front = new Vector3(0f, 0f, 4f);
    var center = new Vector3(0.2f, 0f, 0.2f);
    var flank = new Vector3(4f, 0f, 0f);

    AssertTrue(
        GapCloserDecisionPolicy.ShouldRejectNonTankFrontOrCenterLanding(
            playerIsTank: false,
            front,
            target,
            targetRotation: 0f,
            playerRadius: 0f,
            targetRadius: 0f,
            alternativeExists: true,
            out var frontReason),
        "non-tank front landings should be rejected when a side/rear alternative exists");
    AssertContains("front", frontReason, "front landing reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldRejectNonTankFrontOrCenterLanding(
            playerIsTank: false,
            center,
            target,
            targetRotation: 0f,
            playerRadius: 0f,
            targetRadius: 0f,
            alternativeExists: true,
            out var centerReason),
        "inside-hitbox-looking landings should be rejected when alternatives exist");
    AssertContains("boss center", centerReason, "center landing reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldRejectNonTankFrontOrCenterLanding(
            playerIsTank: false,
            flank,
            target,
            targetRotation: 0f,
            playerRadius: 0f,
            targetRadius: 0f,
            alternativeExists: true,
            out _),
        "flank max-melee landing should remain valid");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldRejectNonTankFrontOrCenterLanding(
            playerIsTank: true,
            front,
            target,
            targetRotation: 0f,
            playerRadius: 0f,
            targetRadius: 0f,
            alternativeExists: true,
            out _),
        "tanks may use safe front-adjacent landings");
}

static void GapCloserPressurePolicySuppressesOptionalCappedDashes()
{
    var raidwide = BossModMechanicPressure.None with { BMRRaidwideIn = 1f };
    AssertTrue(
        GapCloserDecisionPolicy.ShouldHoldReengageForMechanicPressure(
            raidwide,
            walkingWouldMissUsefulGcd: false,
            safetyDash: false,
            strongStyleGainRequired: false,
            out var raidwideReason),
        "capped-charge or style-only reengage should hold under raidwide pressure");
    AssertContains("raidwide", raidwideReason, "raidwide hold reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldHoldReengageForMechanicPressure(
            raidwide,
            walkingWouldMissUsefulGcd: true,
            safetyDash: false,
            strongStyleGainRequired: false,
            out _),
        "strong uptime gain can still proceed to landing validation under raidwide pressure");
}

static void ReengageDashCurrentGcdPolicyConservesLastCharge()
{
    var action = new RsrGcdActionTimingSnapshot(0, 0, "melee GCD", "test", 0, 0.5f, 2f, 2.5f, 0.35f);
    var engagementRange = Configuration.InternalMeleeUptimeRange;

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: engagementRange,
            engagementRange: engagementRange,
            action: action,
            currentCharges: 1,
            currentPositionUnsafe: false,
            safetyGain: 0f,
            uptimeGain: 5f,
            pathGain: 0f,
            out var rangeReason),
        "last charge may be used when the landing restores useful range");
    AssertContains("restores useful range", rangeReason, "range-restoring dash reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: engagementRange + 0.5f,
            engagementRange: engagementRange,
            action: action with { GcdRemaining = 2.5f, GcdElapsed = 0f },
            currentCharges: 1,
            currentPositionUnsafe: false,
            safetyGain: 0f,
            uptimeGain: 4f,
            pathGain: 0f,
            out var walkReason),
        "last charge may be used when the landing plus short walk still reaches the current GCD");
    AssertContains("landing plus walk", walkReason, "post-landing walk reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: 8f,
            engagementRange: engagementRange,
            action: action,
            currentCharges: 1,
            currentPositionUnsafe: false,
            safetyGain: 0f,
            uptimeGain: 5f,
            pathGain: 0f,
            out var lastChargeReason),
        "last charge should be held when the landing still misses the current GCD");
    AssertContains("last charge held", lastChargeReason, "last-charge hold reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: 8f,
            engagementRange: engagementRange,
            action: action,
            currentCharges: 1,
            currentPositionUnsafe: true,
            safetyGain: 4f,
            uptimeGain: 1f,
            pathGain: 0f,
            out var safetyReason),
        "last charge should still be available for meaningful BossMod safety recovery");
    AssertContains("safety dash", safetyReason, "safety exception reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: 8f,
            engagementRange: engagementRange,
            action: action,
            currentCharges: 2,
            currentPositionUnsafe: false,
            safetyGain: 0f,
            uptimeGain: 4.5f,
            pathGain: 0f,
            out var spareReason),
        "spare charges may be used for strong range progress even if the current GCD is already lost");
    AssertContains("spare-charge", spareReason, "spare-charge progress reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseReengageDashForCurrentGcd(
            landingDistanceToHitbox: 8f,
            engagementRange: engagementRange,
            action: action,
            currentCharges: 2,
            currentPositionUnsafe: false,
            safetyGain: 0f,
            uptimeGain: 1f,
            pathGain: 0f,
            out var weakReason),
        "spare charges should still reject weak partial progress");
    AssertContains("misses", weakReason, "weak spare-charge rejection reason");
}

static void TrashGapCloserConservationYieldsToSpareUrgentCharges()
{
    AssertTrue(
        GapCloserDecisionPolicy.ShouldConserveTrashPullGapCloser(
            triggerDistance: 16f,
            useThreshold: 17.5f,
            currentCharges: 1,
            currentPositionUnsafe: false,
            walkingWouldMissUsefulGcd: true,
            out var lastChargeReason),
        "trash conservation should still preserve the last charge below the use threshold");
    AssertContains("conserving", lastChargeReason, "last-charge conservation reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldConserveTrashPullGapCloser(
            triggerDistance: 16f,
            useThreshold: 17.5f,
            currentCharges: 2,
            currentPositionUnsafe: false,
            walkingWouldMissUsefulGcd: true,
            out _),
        "spare charges should be usable when walking misses the current useful GCD");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldConserveTrashPullGapCloser(
            triggerDistance: 16f,
            useThreshold: 17.5f,
            currentCharges: 2,
            currentPositionUnsafe: false,
            walkingWouldMissUsefulGcd: false,
            out var walkableReason),
        "spare charges should still hold when walking can make the useful GCD");
    AssertContains("conserving", walkableReason, "walkable spare-charge conservation reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldConserveTrashPullGapCloser(
            triggerDistance: 16f,
            useThreshold: 17.5f,
            currentCharges: 1,
            currentPositionUnsafe: true,
            walkingWouldMissUsefulGcd: false,
            out _),
        "BossMod-unsafe positions should not be blocked by trash conservation");
}

static void PairedReturnPolicyHoldsWalkableReturns()
{
    var action = new RsrGcdActionTimingSnapshot(0, 0, "melee GCD", "test", 0, 2.5f, 0f, 2.5f, 0.35f);
    AssertFalse(
        GapCloserDecisionPolicy.ShouldUsePairedReturnDash(
            distanceToHitbox: 5f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            action,
            currentPositionUnsafe: false,
            out var walkReason),
        "paired return should hold when walking can make the next GCD");
    AssertContains("walking", walkReason, "walkable paired return reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUsePairedReturnDash(
            distanceToHitbox: 8f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            action with { GcdRemaining = 0.4f },
            currentPositionUnsafe: false,
            out _),
        "paired return should be available when walking would miss useful uptime");
}

static void KnockbackRecoveryDirectionRejectsSidewaysDashes()
{
    AssertTrue(
        GapCloserDecisionPolicy.ShouldAllowKnockbackRecoveryDashDirection(
            playerPosition: Vector3.Zero,
            destination: new Vector3(8f, 0f, 0f),
            targetPosition: new Vector3(20f, 0f, 0f),
            safeDestination: null,
            onlyValidatedSafetyImprovement: false,
            out _),
        "knockback recovery dash toward the target should be allowed");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldAllowKnockbackRecoveryDashDirection(
            playerPosition: Vector3.Zero,
            destination: new Vector3(0f, 0f, 8f),
            targetPosition: new Vector3(20f, 0f, 0f),
            safeDestination: null,
            onlyValidatedSafetyImprovement: false,
            out var sidewaysReason),
        "sideways knockback recovery should be rejected unless it is the only safety improvement");
    AssertContains("sideways", sidewaysReason, "sideways knockback reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldAllowKnockbackRecoveryDashDirection(
            playerPosition: Vector3.Zero,
            destination: new Vector3(0f, 0f, 8f),
            targetPosition: new Vector3(20f, 0f, 0f),
            safeDestination: null,
            onlyValidatedSafetyImprovement: true,
            out _),
        "sideways recovery may still be used when the landing is a validated safety improvement");
}

static void ManualSuppressionAllowsSafetyGapCloserOnly()
{
    AssertTrue(
        GapCloserDecisionPolicy.ShouldRunSafetyGapCloserDuringManualSuppression(
            suppressAutomatedMovement: true,
            gapClosersEnabled: true),
        "manual suppression should still let the safety gap closer controller check unsafe BossMod movement");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldRunSafetyGapCloserDuringManualSuppression(
            suppressAutomatedMovement: false,
            gapClosersEnabled: true),
        "normal updates should use the normal gap closer sequence");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldRunSafetyGapCloserDuringManualSuppression(
            suppressAutomatedMovement: true,
            gapClosersEnabled: false),
        "manual suppression should not enable disabled gap closers");
}

static void FixedDirectionPressureSuppressesOptionalDirectionalDashes()
{
    var misdirection = BossModMechanicPressure.None with
    {
        BMRSpecialModeIn = 0f,
        BMRSpecialModeType = (int)BossModSpecialMode.Misdirection
    };
    AssertTrue(
        GapCloserDecisionPolicy.ShouldBlockAllOptionalDashesForPressure(misdirection, out var reason),
        "misdirection should block optional fixed-direction dash setup");
    AssertContains("misdirection", reason, "misdirection hold reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldHoldReengageForMechanicPressure(
            misdirection with { KnockbackRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(1) },
            walkingWouldMissUsefulGcd: true,
            safetyDash: true,
            strongStyleGainRequired: false,
            out _),
        "required safety movement may still proceed to BossMod landing validation");
}

static void DirectionalDashFacingCanPauseBmrMovement()
{
    var now = DateTime.UtcNow;
    var validatedDash = new FacingRequest(
        FacingRequestSource.DirectionalDash,
        DesiredRotation: 0f,
        ToleranceRadians: FacingController.DirectionalDashToleranceRadians,
        MaxCorrectionRadians: MathF.PI,
        ExpiresUtc: now.AddMilliseconds(250),
        Priority: 50,
        Reason: "turn for dash")
    {
        BossModPolicy = FacingBossModPolicy.AssistValidatedDash,
        DashDestination = new Vector3(5f, 0f, 0f)
    };

    AssertTrue(
        FacingController.ShouldPauseBossModMovementForDirectionalDash(
            validatedDash,
            BossModMechanicPressure.None,
            now,
            out var pauseReason),
        "validated directional dash setup should pause BMR movement briefly");
    AssertContains("paused", pauseReason, "directional dash pause reason");

    AssertTrue(
        FacingController.ShouldPauseBossModMovementForDirectionalDash(
            validatedDash with { BossModPolicy = FacingBossModPolicy.AssistBmrMovementDash },
            BossModMechanicPressure.None,
            now,
            out _),
        "BMR-assist directional dash setup can also pause BMR movement for the turn");

    AssertFalse(
        FacingController.ShouldPauseBossModMovementForDirectionalDash(
            validatedDash with { BossModPolicy = FacingBossModPolicy.Conservative },
            BossModMechanicPressure.None,
            now,
            out _),
        "conservative facing requests should not pause BMR movement");

    AssertFalse(
        FacingController.ShouldPauseBossModMovementForDirectionalDash(
            validatedDash with { Source = FacingRequestSource.SocialTurning },
            BossModMechanicPressure.None,
            now,
            out _),
        "social facing should not pause BMR movement");

    var noMovement = BossModMechanicPressure.None with
    {
        BMRSpecialModeIn = 0f,
        BMRSpecialModeType = (int)BossModSpecialMode.NoMovement
    };
    AssertFalse(
        FacingController.ShouldPauseBossModMovementForDirectionalDash(
            validatedDash,
            noMovement,
            now,
            out var specialReason),
        "BossMod special movement modes should still block dash-turn pauses");
    AssertContains("no-movement", specialReason, "special-mode pause rejection reason");
}

static void CasterImmobilityPolicyHoldsOptionalDashes()
{
    AssertTrue(
        GapCloserDecisionPolicy.ShouldHoldCasterImmobilityWindow(
            classJobId: 25,
            playerCasting: false,
            stationaryBuffActive: true,
            currentPositionUnsafe: false,
            out var leyReason),
        "BLM should hold optional dashes while committed to Ley Lines");
    AssertContains("stationary", leyReason, "stationary caster reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldHoldCasterImmobilityWindow(
            classJobId: 42,
            playerCasting: true,
            stationaryBuffActive: false,
            currentPositionUnsafe: false,
            out var castReason),
        "caster-like jobs should not dash during hard casts");
    AssertContains("casting", castReason, "casting hold reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldHoldCasterImmobilityWindow(
            classJobId: 34,
            playerCasting: true,
            stationaryBuffActive: false,
            currentPositionUnsafe: false,
            out _),
        "existing all-job casting suppression should remain intact");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldHoldCasterImmobilityWindow(
            classJobId: 42,
            playerCasting: false,
            stationaryBuffActive: true,
            currentPositionUnsafe: true,
            out _),
        "unsafe current position should still proceed to safety validation");
}

static void CasterAdvisoryMovementHoldsNearGcdReady()
{
    AssertFalse(
        CasterMovementPolicy.IsCasterSlidecastWindow(
            playerCasting: false,
            totalCastTime: 2.5f,
            currentCastTime: 2.3f),
        "stale cast timing should not be treated as an active slidecast window");

    AssertTrue(
        CasterMovementPolicy.IsCasterSlidecastWindow(
            playerCasting: true,
            totalCastTime: 2.5f,
            currentCastTime: 2.3f),
        "active casts near completion should count as slidecast windows");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForActiveCast(
            classJobId: 42,
            playerCasting: true,
            slidecastWindow: true,
            moveDistance: 1.5f),
        "magic DPS should allow small planned slidecast movement");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForActiveCast(
            classJobId: 42,
            playerCasting: true,
            slidecastWindow: true,
            moveDistance: 4f),
        "magic DPS should not use slidecast windows for larger advisory corrections");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForActiveCast(
            classJobId: 24,
            playerCasting: true,
            slidecastWindow: true,
            moveDistance: 1f),
        "healer advisory movement should not slidecast step");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForActiveCast(
            classJobId: 42,
            playerCasting: true,
            slidecastWindow: false,
            moveDistance: 1f),
        "magic DPS advisory movement should still hold outside the slidecast window");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForActiveCast(
            classJobId: 42,
            playerCasting: true,
            slidecastWindow: true,
            moveDistance: null),
        "slidecast advisory movement should need a bounded candidate distance");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 24,
            gcdRemaining: 0.2f,
            gcdElapsed: 2.3f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f),
        "WHM advisory movement should hold when the next GCD is about to be ready");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 42,
            gcdRemaining: 0.6f,
            gcdElapsed: 1.9f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f),
        "magic ranged advisory movement should use the caster GCD hold window");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 24,
            gcdRemaining: 1.2f,
            gcdElapsed: 1.3f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f),
        "healer advisory movement should remain available early in the recast");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 42,
            gcdRemaining: 0.8f,
            gcdElapsed: 1.7f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f,
            moveDistance: 1f),
        "small caster advisory moves should be allowed when they fit before the RSR action window");

    AssertTrue(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 42,
            gcdRemaining: 1.2f,
            gcdElapsed: 1.3f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f,
            moveDistance: 5f),
        "larger caster advisory moves should hold when walking would miss the next RSR action window");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 42,
            gcdRemaining: 1.2f,
            gcdElapsed: 1.3f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f,
            moveDistance: 2f),
        "bounded caster advisory moves should remain available when walking fits the recast budget");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 31,
            gcdRemaining: 0.2f,
            gcdElapsed: 2.3f,
            gcdTotal: 2.5f,
            gcdActionAhead: 0.35f),
        "physical ranged jobs should not use hardcast advisory movement holds");

    AssertFalse(
        CasterMovementPolicy.ShouldSuppressAdvisoryMovementForGcd(
            classJobId: 24,
            gcdRemaining: -1f,
            gcdElapsed: -1f,
            gcdTotal: -1f,
            gcdActionAhead: -1f),
        "unavailable timing should fall back to active-cast suppression only");
}

static void BlackMageLeyLinesMovementPolicy()
{
    AssertTrue(
        BlackMageLeyLinesPositioningController.ShouldAllowNarrowSlidecastReturn(1.5f),
        "narrow Ley Lines returns should be handled by movement/slidecast");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldAllowNarrowSlidecastReturn(2.5f),
        "wider Ley Lines returns should not be treated as narrow slidecast corrections");

    AssertTrue(
        BlackMageLeyLinesPositioningController.ShouldHoldActionForReturnMovement(
            returnMovementEnabled: true,
            canWalkBack: false,
            distanceToPreferred: 1.5f),
        "narrow return movement should hold teleport actions when walking/slidecast return is enabled");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldHoldActionForReturnMovement(
            returnMovementEnabled: false,
            canWalkBack: true,
            distanceToPreferred: 1.5f),
        "disabled walking/slidecast return should not suppress configured teleport actions");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldUseBetweenTheLines(
            enabled: true,
            actionReady: true,
            badForOptionalMovement: false,
            distanceToPreferred: 1.5f),
        "Between the Lines should not replace a narrow slidecast return");

    AssertTrue(
        BlackMageLeyLinesPositioningController.ShouldUseBetweenTheLines(
            enabled: true,
            actionReady: true,
            badForOptionalMovement: false,
            distanceToPreferred: 4f),
        "Between the Lines should bridge wider Ley Lines returns");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldUseBetweenTheLines(
            enabled: true,
            actionReady: true,
            badForOptionalMovement: true,
            distanceToPreferred: 4f),
        "Between the Lines should hold during optional-movement pressure");

    AssertTrue(
        BlackMageLeyLinesPositioningController.ShouldUseRetrace(
            enabled: true,
            actionReady: true,
            badForOptionalMovement: false,
            distanceToPreferred: 5f),
        "Retrace should be available for larger Ley Lines corrections");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldUseRetrace(
            enabled: true,
            actionReady: true,
            badForOptionalMovement: false,
            distanceToPreferred: 2f),
        "Retrace should not replace a short walk or slidecast return");

    AssertTrue(
        BlackMageLeyLinesPositioningController.ShouldUseRetraceForBlockedReturn(
            enabled: true,
            actionReady: true,
            returnMovementEnabled: true,
            canWalkBack: false,
            distanceToPreferred: 5f),
        "Retrace should move Ley Lines to the player when walking back will miss the GCD window");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldUseRetraceForBlockedReturn(
            enabled: true,
            actionReady: true,
            returnMovementEnabled: true,
            canWalkBack: false,
            distanceToPreferred: 1.5f),
        "blocked-return Retrace should not replace a narrow slidecast return");

    AssertFalse(
        BlackMageLeyLinesPositioningController.ShouldUseRetraceForBlockedReturn(
            enabled: true,
            actionReady: true,
            returnMovementEnabled: false,
            canWalkBack: false,
            distanceToPreferred: 5f),
        "blocked-return Retrace should only apply when walking return is enabled");
}

static void GapCloserFollowsRsrAutoTarget()
{
    AssertTrue(
        GapCloserController.ShouldUseRsrActionTarget(
            rsrHenchedActive: false,
            actionAvailable: true,
            actionIsFriendly: false,
            actionPrimaryTargetId: 0x1234,
            currentTargetMatchesAction: false),
        "RSR Auto enemy target mismatch should realign the gap closer target");

    AssertFalse(
        GapCloserController.ShouldUseRsrActionTarget(
            rsrHenchedActive: true,
            actionAvailable: true,
            actionIsFriendly: false,
            actionPrimaryTargetId: 0x1234,
            currentTargetMatchesAction: false),
        "Henched target control remains authoritative");

    AssertFalse(
        GapCloserController.ShouldUseRsrActionTarget(
            rsrHenchedActive: false,
            actionAvailable: true,
            actionIsFriendly: true,
            actionPrimaryTargetId: 0x1234,
            currentTargetMatchesAction: false),
        "friendly RSR actions do not retarget offensive gap closers");

    AssertFalse(
        GapCloserController.ShouldUseRsrActionTarget(
            rsrHenchedActive: false,
            actionAvailable: true,
            actionIsFriendly: false,
            actionPrimaryTargetId: 0x1234,
            currentTargetMatchesAction: true),
        "matching selected target does not need RSR realignment");
}

static void RangedGapClosersSkipBossReengage()
{
    AssertTrue(
        GapCloserController.ShouldBlockRangedReengageGapCloser(24),
        "WHM Aetherial Shift should be reserved for safety movement instead of boss reengage");
    AssertTrue(
        GapCloserController.ShouldBlockRangedReengageGapCloser(40),
        "SGE Icarus should be reserved for safety or party repositioning instead of boss reengage");
    AssertTrue(
        GapCloserController.ShouldBlockRangedReengageGapCloser(35),
        "RDM Corps-a-corps should be reserved for explicit melee combo movement instead of generic boss reengage");
    AssertTrue(
        GapCloserController.ShouldBlockRangedReengageGapCloser(38),
        "DNC En Avant should be reserved for safety movement instead of boss reengage");
    AssertTrue(
        GapCloserController.ShouldBlockRangedReengageGapCloser(42),
        "PCT Smudge should be reserved for safety movement instead of boss reengage");
    AssertFalse(
        GapCloserController.ShouldBlockRangedReengageGapCloser(34),
        "SAM Gyoten remains a real melee reengage tool");
}

static void PhantomGapClosersInheritBaseJobReengageRules()
{
    AssertFalse(
        GapCloserController.ShouldAllowPhantomTargetReengageGapCloser(24, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "WHM with Phantom Kick should still reserve target reengage dashes");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomTargetReengageGapCloser(40, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "SGE with Phantom Kick should still reserve target reengage dashes");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomReengageGapCloser(34, phantomGapClosersEnabled: false, currentJobGapCloserEnabled: true),
        "Phantom reengage requires the Phantom dash opt-in");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomReengageGapCloser(34, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: false),
        "Phantom reengage requires the current job or archetype dash gate");
    AssertTrue(
        GapCloserController.ShouldAllowPhantomTargetReengageGapCloser(34, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "SAM with Phantom Kick may use the normal melee reengage policy");
    AssertTrue(
        GapCloserController.ShouldAllowPhantomTargetAoeReengageGapCloser(40, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true, inAoePackContext: true, packAoeRange: 5f),
        "SGE with Phantom Kick may use target dash rules for close-range AoE pack movement");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomTargetAoeReengageGapCloser(40, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true, inAoePackContext: false, packAoeRange: 5f),
        "SGE Phantom Kick AoE movement requires an AoE pack context");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomTargetAoeReengageGapCloser(33, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true, inAoePackContext: true, packAoeRange: 25f),
        "AST with long-range AoE should not borrow close-range Phantom Kick movement");
    AssertTrue(
        GapCloserController.ShouldAllowPhantomTargetAoeReengageGapCloser(28, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true, inAoePackContext: true, packAoeRange: 5f),
        "SCH with no native dash may use healer close-range AoE Phantom Kick movement");
    AssertTrue(
        GapCloserController.ShouldAllowPhantomTargetReengageGapCloser(30, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "NIN with Phantom Kick follows the melee target-dash archetype");
    AssertTrue(
        GapCloserController.ShouldAllowPhantomForwardReengageGapCloser(34, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "SAM with Occult Featherfoot follows the melee forward-dash archetype");
    AssertFalse(
        GapCloserController.ShouldAllowPhantomForwardReengageGapCloser(19, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "tank archetype does not borrow fixed-forward reengage dashes");
    AssertTrue(
        EscapeGapCloserController.ShouldAllowPhantomTargetEscapeGapCloser(40, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "SGE with Phantom Kick may use target safety dashes when BMR safety validation proves progress");
    AssertTrue(
        EscapeGapCloserController.ShouldAllowPhantomForwardEscapeGapCloser(40, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "SGE with Occult Featherfoot follows the healer forward safety archetype");
    AssertTrue(
        EscapeGapCloserController.ShouldAllowPhantomForwardEscapeGapCloser(23, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "BRD with Occult Featherfoot follows the ranged forward safety archetype");
    AssertTrue(
        EscapeGapCloserController.ShouldAllowPhantomForwardEscapeGapCloser(31, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "MCH with no native dash follows the physical ranged forward safety archetype");
    AssertFalse(
        EscapeGapCloserController.ShouldAllowPhantomForwardEscapeGapCloser(19, phantomGapClosersEnabled: true, currentJobGapCloserEnabled: true),
        "tank archetype does not borrow fixed-forward safety dashes");
}

static void KnockbackRecoveryAllowsTimingBypass()
{
    AssertTrue(
        GapCloserController.ShouldAllowKnockbackRecoveryReengage(
            knockbackRecoveryActive: true,
            playerIsMeleeRangeRole: true,
            targetHasBossModule: true,
            antiKnockbackActive: false,
            distanceToHitbox: 4.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "melee boss knockback recovery without anti-knockback should ignore the timing policy");

    AssertFalse(
        GapCloserController.ShouldAllowKnockbackRecoveryReengage(
            knockbackRecoveryActive: true,
            playerIsMeleeRangeRole: true,
            targetHasBossModule: true,
            antiKnockbackActive: true,
            distanceToHitbox: 4.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "active anti-knockback should keep the normal timing gate");

    AssertFalse(
        GapCloserController.ShouldAllowKnockbackRecoveryReengage(
            knockbackRecoveryActive: true,
            playerIsMeleeRangeRole: false,
            targetHasBossModule: true,
            antiKnockbackActive: false,
            distanceToHitbox: 4.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "ranged jobs should keep the normal timing gate");

    AssertFalse(
        GapCloserController.ShouldAllowKnockbackRecoveryReengage(
            knockbackRecoveryActive: true,
            playerIsMeleeRangeRole: true,
            targetHasBossModule: false,
            antiKnockbackActive: false,
            distanceToHitbox: 4.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "non-boss targets should keep the normal timing gate");

    AssertFalse(
        GapCloserController.ShouldAllowKnockbackRecoveryReengage(
            knockbackRecoveryActive: true,
            playerIsMeleeRangeRole: true,
            targetHasBossModule: true,
            antiKnockbackActive: false,
            distanceToHitbox: 2.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "players already inside melee engagement range should not dash");
}

static void BmrSafetyDashRequiresDestinationProgress()
{
    AssertFalse(
        MobilityDecisionEvaluator.HasMeaningfulBmrSafetyDestinationProgress(
            Vector3.Zero,
            new Vector3(1f, 0f, 0f),
            new Vector3(20f, 0f, 0f),
            out var nearbyReason),
        "BMR safety dash should reject tiny path savings when the destination is far away");
    AssertContains("only saves 1.0y", nearbyReason, "nearby safety dash rejection reason");

    AssertTrue(
        MobilityDecisionEvaluator.HasMeaningfulBmrSafetyDestinationProgress(
            Vector3.Zero,
            new Vector3(5f, 0f, 0f),
            new Vector3(20f, 0f, 0f),
            out _),
        "BMR safety dash should accept meaningful progress toward the movement destination");

    AssertTrue(
        MobilityDecisionEvaluator.HasMeaningfulBmrSafetyDestinationProgress(
            Vector3.Zero,
            new Vector3(1f, 0f, 0f),
            new Vector3(4f, 0f, 0f),
            out _),
        "BMR safety dash should scale down the required gain when already close to the destination");
}

static void DancerEnAvantUsesNativeDashDistance()
{
    AssertEqual(10f, CombatConstants.DancerEnAvantRange, "DNC En Avant distance");
    AssertEqual(
        CombatConstants.DancerEnAvantRange,
        GapCloserDecisionPolicy.ResolveConservativeTrashGapCloserRange(38),
        "DNC conservative dash range");
    AssertEqual(
        CombatConstants.DancerEnAvantRange - 2.5f,
        GapCloserDecisionPolicy.ResolveConservativeTrashGapCloserUseThreshold(38),
        "DNC conservative dash use threshold");
    AssertEqual(
        CombatConstants.FixedForwardGapCloserRange,
        GapCloserDecisionPolicy.ResolveConservativeTrashGapCloserRange(42),
        "PCT fixed-forward dash range");

    AssertTrue(
        EscapeGapCloserController.ShouldAllowUnsafeChainedForwardEscapeDash(
            safeMovementDistance: 18f,
            currentCharges: 2,
            dashDistance: CombatConstants.DancerEnAvantRange,
            out var chainReason),
        "DNC should allow a two-charge unsafe chain when the BMR target is reachable");
    AssertContains("2 charges", chainReason, "DNC chained dash reason");

    AssertFalse(
        EscapeGapCloserController.ShouldAllowUnsafeChainedForwardEscapeDash(
            safeMovementDistance: 18f,
            currentCharges: 1,
            dashDistance: CombatConstants.DancerEnAvantRange,
            out var singleChargeReason),
        "DNC should not intentionally land unsafe with only one charge");
    AssertContains("spare", singleChargeReason, "DNC single-charge chain rejection");

    AssertFalse(
        EscapeGapCloserController.ShouldAllowUnsafeChainedForwardEscapeDash(
            safeMovementDistance: 25f,
            currentCharges: 2,
            dashDistance: CombatConstants.DancerEnAvantRange,
            out var outOfReachReason),
        "DNC should not start an unsafe chain when charges cannot cover the BMR target");
    AssertContains("exceeds", outOfReachReason, "DNC out-of-reach chain rejection");
}

static void FriendlyEscapeAnchorConservesLowValueAllyDashes()
{
    var urgentMovement = BossModMovementDiagnostics.Empty with
    {
        NavigationDetails = BossModNavigationDiagnostics.Empty with { ForceMovementIn = 1f }
    };
    var walkableMovement = BossModMovementDiagnostics.Empty with
    {
        NavigationDetails = BossModNavigationDiagnostics.Empty with { ForceMovementIn = 4f }
    };

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 4f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 4f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: false,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out var shortReason),
        "short ally safety anchors should not spend a dash");
    AssertContains("configured dash distance", shortReason, "short ally escape rejection reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 9f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 3f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: false,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out var lowProgressReason),
        "ally safety anchors should need meaningful progress toward the BMR destination");
    AssertContains("low safety progress", lowProgressReason, "low-progress ally escape rejection reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 9f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 5f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: false,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out _),
        "multi-charge ally safety anchors may spend a dash for useful urgent progress");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 9f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 6f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: true,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out var conservativeReason),
        "single-charge ally safety anchors should need stronger progress");
    AssertContains("low safety progress", conservativeReason, "single-charge progress rejection reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 12f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 9f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: true,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            walkableMovement,
            out var walkReason),
        "single-charge ally safety anchors should hold when walking still reaches BMR safety");
    AssertContains("single-charge", walkReason, "single-charge walkable safety rejection reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 12f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 9f,
            uptimeRelayAvailable: false,
            uptimeRelayReason: string.Empty,
            conservativeSingleCharge: true,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out _),
        "single-charge ally safety anchors may still fire when walking would miss urgent BMR safety");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeUptimeRelay(
            classJobId: 20,
            currentCharges: 2,
            finalTargetMoving: false,
            finalTargetMovementReason: string.Empty,
            currentTargetDistance: 24f,
            anchorTargetDistance: 18f,
            targetDashRange: CombatConstants.GapCloserMaxRange,
            out var relayRangeReason),
        "multi-charge MNK should be allowed to Thunderclap to an ally who puts the boss in target dash range");
    AssertContains("dash range", relayRangeReason, "relay dash-range reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeAnchorDash(
            moveDistance: 4f,
            configuredMinimumMoveDistance: 8f,
            pathGain: 1f,
            uptimeRelayAvailable: true,
            uptimeRelayReason: relayRangeReason,
            conservativeSingleCharge: false,
            currentPositionUnsafe: true,
            safeMovementDistance: 12f,
            urgentMovement,
            out var relayAnchorReason),
        "multi-charge melee ally relay should bypass low-value safety thresholds when it creates boss dash access");
    AssertContains("dash range", relayAnchorReason, "relay anchor reason");

    AssertTrue(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeUptimeRelay(
            classJobId: 20,
            currentCharges: 2,
            finalTargetMoving: false,
            finalTargetMovementReason: string.Empty,
            currentTargetDistance: 10f,
            anchorTargetDistance: CombatConstants.MeleeActionRange,
            targetDashRange: CombatConstants.GapCloserMaxRange,
            out var relayMeleeReason),
        "multi-charge MNK should be allowed to Thunderclap to an ally already in a melee pocket");
    AssertContains("melee", relayMeleeReason, "relay melee reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeUptimeRelay(
            classJobId: 40,
            currentCharges: 1,
            finalTargetMoving: false,
            finalTargetMovementReason: string.Empty,
            currentTargetDistance: 24f,
            anchorTargetDistance: 18f,
            targetDashRange: 25f,
            out var sageRelayReason),
        "SGE Icarus should not use the melee/tank uptime relay exception");
    AssertContains("melee or tank", sageRelayReason, "sage relay rejection reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeUptimeRelay(
            classJobId: 20,
            currentCharges: 1,
            finalTargetMoving: false,
            finalTargetMovementReason: string.Empty,
            currentTargetDistance: 24f,
            anchorTargetDistance: 18f,
            targetDashRange: CombatConstants.GapCloserMaxRange,
            out var oneChargeReason),
        "MNK should keep the conservation gate when only one Thunderclap charge remains");
    AssertContains("spare dash charge", oneChargeReason, "one-charge relay rejection reason");

    AssertFalse(
        GapCloserDecisionPolicy.ShouldUseFriendlyEscapeUptimeRelay(
            classJobId: 20,
            currentCharges: 2,
            finalTargetMoving: true,
            finalTargetMovementReason: "enemy target still moving",
            currentTargetDistance: 24f,
            anchorTargetDistance: 18f,
            targetDashRange: CombatConstants.GapCloserMaxRange,
            out var movingRelayReason),
        "multi-charge MNK should not use an ally hop for uptime if the final boss dash target is still moving");
    AssertContains("still moving", movingRelayReason, "moving final target relay rejection reason");
}

static void EnemyMovementTrackerHoldsMovingTargetDashes()
{
    var tracker = new EnemyMovementTracker();
    var now = DateTime.UtcNow;
    var targetId = 0xABCul;

    AssertFalse(
        tracker.ObserveMoving(targetId, Vector3.Zero, now, out _),
        "first enemy movement observation should not hold a dash");

    AssertFalse(
        tracker.ObserveMoving(targetId, new Vector3(0.1f, 0f, 0f), now.AddMilliseconds(250), out _),
        "small position jitter should not hold a dash");

    AssertTrue(
        tracker.ObserveMoving(targetId, new Vector3(0.7f, 0f, 0f), now.AddMilliseconds(500), out var movingReason),
        "meaningful enemy movement should hold target dashes");
    AssertContains("still moving", movingReason, "moving target hold reason");

    AssertFalse(
        tracker.ObserveMoving(targetId, new Vector3(0.7f, 0f, 0f), now.AddMilliseconds(800), out _),
        "recent enemy movement should be allowed once the target is stable on the current sample");

    AssertFalse(
        tracker.ObserveMoving(targetId, new Vector3(0.7f, 0f, 0f), now.AddMilliseconds(1300), out _),
        "enemy target dashes should stay allowed after the target settles");
}

static void LateSafetyDashContinuesWhileUnsafeAndFar()
{
    AssertFalse(
        EscapeGapCloserController.ShouldSuppressLateEscapeGapCloser(
            currentSafe: false,
            canAssistSafeMovement: true,
            dangerElapsedMilliseconds: CombatConstants.EscapeGapCloserDangerWindowMilliseconds + 1d),
        "late safety dash should still evaluate while unsafe and far from the BMR safe destination");
    AssertTrue(
        EscapeGapCloserController.ShouldSuppressLateEscapeGapCloser(
            currentSafe: true,
            canAssistSafeMovement: false,
            dangerElapsedMilliseconds: CombatConstants.EscapeGapCloserDangerWindowMilliseconds + 1d),
        "late safety dash should suppress when already safe");
    AssertFalse(
        EscapeGapCloserController.ShouldSuppressLateEscapeGapCloser(
            currentSafe: true,
            canAssistSafeMovement: true,
            dangerElapsedMilliseconds: CombatConstants.EscapeGapCloserDangerWindowMilliseconds + 1d),
        "late safety dash should keep evaluating while safe when far movement assist is allowed");
    AssertFalse(
        EscapeGapCloserController.ShouldSuppressLateEscapeGapCloser(
            currentSafe: false,
            canAssistSafeMovement: true,
            dangerElapsedMilliseconds: CombatConstants.EscapeGapCloserDangerWindowMilliseconds + 1d),
        "late safety dash should still evaluate while unsafe even when the nearest BMR safe destination is below the dash threshold");
    AssertFalse(
        EscapeGapCloserController.ShouldSuppressLateEscapeGapCloser(
            currentSafe: false,
            canAssistSafeMovement: false,
            dangerElapsedMilliseconds: CombatConstants.EscapeGapCloserDangerWindowMilliseconds - 1d),
        "early safety dash should still evaluate");

    var farMovement = BossModMovementDiagnostics.Empty with
    {
        NavigationDetails = BossModNavigationDiagnostics.Empty with { ForceMovementIn = 2.5f }
    };
    AssertTrue(
        EscapeGapCloserController.ShouldAssistSafeBossModMovement(
            CombatStyle.Normal,
            farMovement,
            safeMovementDistance: 18f,
            out _),
        "safe-first timing should assist when a far safe zone is close to walking time");

    AssertFalse(
        EscapeGapCloserController.ShouldAssistSafeBossModMovement(
            CombatStyle.Normal,
            farMovement,
            safeMovementDistance: 8f,
            out _),
        "safe-first timing should still walk for nearby safe movement");

    var distantWindow = BossModMovementDiagnostics.Empty with
    {
        NavigationDetails = BossModNavigationDiagnostics.Empty with { ForceMovementIn = 6f }
    };
    AssertFalse(
        EscapeGapCloserController.ShouldAssistSafeBossModMovement(
            CombatStyle.GreedGCD,
            distantWindow,
            safeMovementDistance: 18f,
            out _),
        "far safe-zone assist should not dash when walking has comfortable time");
}

static void HostileRelayDashRequiresTargetMomentum()
{
    AssertTrue(
        GapCloserController.ShouldTryHostileRelay(
            classJobId: 41,
            intendedDistanceToHitbox: 8f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "VPR should look for hostile relay targets as soon as the chosen target is outside melee engagement range");

    AssertFalse(
        GapCloserController.ShouldTryHostileRelay(
            classJobId: 41,
            intendedDistanceToHitbox: 2.5f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "VPR should not relay when already in melee engagement range");

    AssertTrue(
        GapCloserController.ShouldTreatGcdReengageAsUrgent(
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            gcdRemaining: 0.5f),
        "short reengage dashes should be allowed when walking cannot make the next GCD");

    AssertFalse(
        GapCloserController.ShouldTreatGcdReengageAsUrgent(
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            gcdRemaining: 2.5f),
        "reengage dash should stay suppressed when there is time to walk");

    var reachableAction = new RsrGcdActionTimingSnapshot(0, 0, "melee GCD", "test", 0, 2.5f, 0f, 2.5f, 0.35f);
    AssertTrue(
        GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            reachableAction,
            out _),
        "walking that reaches melee before the next GCD should suppress reengage dashes");

    var lateAction = reachableAction with { GcdRemaining = 0.5f };
    AssertFalse(
        GapCloserDecisionPolicy.CanWalkToRangeBeforeGcd(
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange,
            lateAction,
            out _),
        "walking that misses melee before the next GCD should allow safe direct dashes");

    AssertTrue(
        GapCloserController.ShouldTreatMissingRsrTimingAsMissedMelee(
            playerIsMeleeRangeRole: true,
            hasReengageTiming: false,
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "melee jobs with no RSR GCD recommendation while outside melee should recover range immediately");

    AssertFalse(
        GapCloserController.ShouldTreatMissingRsrTimingAsMissedMelee(
            playerIsMeleeRangeRole: false,
            hasReengageTiming: false,
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "ranged jobs should not use melee recovery policy when RSR timing is unavailable");

    AssertFalse(
        GapCloserController.ShouldTreatMissingRsrTimingAsMissedMelee(
            playerIsMeleeRangeRole: true,
            hasReengageTiming: true,
            distanceToHitbox: 7f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "reliable RSR timing should use the walk-versus-GCD policy");

    AssertFalse(
        GapCloserController.ShouldTreatMissingRsrTimingAsMissedMelee(
            playerIsMeleeRangeRole: true,
            hasReengageTiming: false,
            distanceToHitbox: 2f,
            engagementRange: Configuration.InternalMeleeUptimeRange),
        "melee jobs already in range should not recover with a dash");

    AssertTrue(
        GapCloserController.ShouldUseHostileRelayDash(
            playerPosition: Vector3.Zero,
            playerRadius: 0f,
            landingPosition: new Vector3(15f, 0f, 0f),
            intendedTargetPosition: new Vector3(30f, 0f, 0f),
            intendedTargetRadius: 0f,
            out _),
        "relay landing between player and intended target should be allowed");

    AssertFalse(
        GapCloserController.ShouldUseHostileRelayDash(
            playerPosition: Vector3.Zero,
            playerRadius: 0f,
            landingPosition: new Vector3(0f, 0f, 15f),
            intendedTargetPosition: new Vector3(30f, 0f, 0f),
            intendedTargetRadius: 0f,
            out var wrongDirectionReason),
        "relay landing sideways should not be allowed");
    AssertContains("gain", wrongDirectionReason, "sideways relay rejection reason");

    AssertFalse(
        GapCloserController.ShouldUseHostileRelayDash(
            playerPosition: Vector3.Zero,
            playerRadius: 0f,
            landingPosition: new Vector3(2f, 0f, 0f),
            intendedTargetPosition: new Vector3(30f, 0f, 0f),
            intendedTargetRadius: 0f,
            out var lowGainReason),
        "relay landing needs meaningful progress toward the intended target");
    AssertContains("low target gain", lowGainReason, "low-gain relay rejection reason");
}

static void TrashGapCloserRejectsStalePack()
{
    AssertFalse(
        GapCloserController.ShouldAllowTrashPullGapCloserTarget(
            playerPosition: new Vector3(0f, 0f, 0f),
            targetPosition: new Vector3(-8f, 0f, 0f),
            anchorPosition: new Vector3(12f, 0f, 0f),
            out var reason),
        "trash gap closer should not dash away from tank pack");
    AssertContains("away from tank pack", reason, "trash gap closer rejection reason");

    AssertTrue(
        GapCloserController.ShouldAllowTrashPullGapCloserTarget(
            playerPosition: new Vector3(0f, 0f, 0f),
            targetPosition: new Vector3(10f, 0f, 0f),
            anchorPosition: new Vector3(12f, 0f, 0f),
            out _),
        "trash gap closer can move toward tank pack");
}

static void SamuraiTrashEscapeHoldsYatenWhileSafe()
{
    var trash = TrashPullDiagnostics.Empty with
    {
        Phase = TrashPullPhase.Burning,
        Confidence = 0.8f,
        DominantTargetCount = 3
    };

    AssertTrue(
        EscapeGapCloserController.ShouldHoldSamuraiBackstepEscapeInTrash(
            currentSafe: true,
            trash,
            out var safeReason),
        "SAM should hold Yaten during confident trash when already safe");
    AssertContains("Yaten held", safeReason, "safe trash Yaten hold reason");

    AssertFalse(
        EscapeGapCloserController.ShouldHoldSamuraiBackstepEscapeInTrash(
            currentSafe: false,
            trash,
            out _),
        "SAM should keep Yaten available when BMR says the current position is unsafe");

    AssertFalse(
        EscapeGapCloserController.ShouldHoldSamuraiBackstepEscapeInTrash(
            currentSafe: true,
            TrashPullDiagnostics.Empty with
            {
                Phase = TrashPullPhase.Burning,
                Confidence = 0.8f,
                DominantTargetCount = 1
            },
            out _),
        "single-target contexts should not use the trash Yaten hold");
}

static void SamuraiEscapeHoldsDashForShortBmrWalk()
{
    AssertTrue(
        EscapeGapCloserController.ShouldHoldSamuraiEscapeForWalkableSafety(
            currentSafe: false,
            safeMovementDistance: 3.5f,
            pathKnown: true,
            pathClear: true,
            out var unsafeShortWalkReason),
        "SAM should sidestep short BMR safety movement instead of dashing out");
    AssertContains("3.5y walk", unsafeShortWalkReason, "short walk hold reason");

    AssertTrue(
        EscapeGapCloserController.ShouldHoldSamuraiEscapeForWalkableSafety(
            currentSafe: true,
            safeMovementDistance: 5f,
            pathKnown: false,
            pathClear: true,
            out _),
        "SAM should not spend a dash for a short optional safety assist while already safe");

    AssertFalse(
        EscapeGapCloserController.ShouldHoldSamuraiEscapeForWalkableSafety(
            currentSafe: false,
            safeMovementDistance: 5f,
            pathKnown: true,
            pathClear: false,
            out _),
        "SAM may still dash when the short walk path is not clear");

    AssertFalse(
        EscapeGapCloserController.ShouldHoldSamuraiEscapeForWalkableSafety(
            currentSafe: false,
            safeMovementDistance: 9f,
            pathKnown: true,
            pathClear: true,
            out _),
        "larger BMR safety movement may still use a dash");
}

static void HealerCoverageIgnoresSingleMissingMemberDrift()
{
    AssertTrue(
        HealerAoePositioningController.IsSingleMissingCoverageRestore(
            currentCoveredCount: 6,
            bestCoveredCount: 7,
            totalMembers: 7),
        "single missing member should be recognized as drift, not movement intent");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 6,
            bestCoveredCount: 7,
            totalMembers: 7,
            currentCoverageComfortSlack: 4f,
            bestCoverageComfortSlack: 4f,
            distanceToCenter: 5.5f,
            downtimeLikely: false,
            mechanicPositioningActive: false),
        "routine movement should not chase one party member who stepped out of healer range");

    AssertTrue(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 4,
            bestCoveredCount: 6,
            totalMembers: 7,
            currentCoverageComfortSlack: 4f,
            bestCoverageComfortSlack: 4f,
            distanceToCenter: 7.5f,
            downtimeLikely: false,
            mechanicPositioningActive: false),
        "routine movement may still respond when multiple party members can be brought back into coverage");
}

static void BmrAdvisoryScoringCombinesEnabledPreferences()
{
    var comfort = GoalZoneScorePolicy.ApplyPriorityWeight(
        GoalZoneScorePolicy.StrongPreference,
        BossModGoalPriority.Convenience,
        contributionWeight: 1f);
    var uptime = GoalZoneScorePolicy.ApplyPriorityWeight(
        GoalZoneScorePolicy.StrongPreference,
        BossModGoalPriority.Uptime,
        contributionWeight: 1f);
    var party = GoalZoneScorePolicy.ApplyPriorityWeight(
        GoalZoneScorePolicy.StrongPreference,
        BossModGoalPriority.PartyUtility,
        contributionWeight: 1f);
    var defensive = GoalZoneScorePolicy.ApplyPriorityWeight(
        GoalZoneScorePolicy.StrongPreference,
        BossModGoalPriority.DefensiveMechanic,
        contributionWeight: 1f);

    AssertTrue(comfort < uptime && uptime < party && party < defensive, "advisory priority order should be comfort < uptime < party < defensive");

    var uptimeOnly = GoalZoneScorePolicy.ClampAdvisoryScore(uptime);
    var combined = GoalZoneScorePolicy.ClampAdvisoryScore(uptime + comfort);
    AssertTrue(combined > uptimeOnly, "compatible advisory preferences should add instead of lower priority being discarded");

    AssertTrue(
        GoalZoneScorePolicy.ClampAdvisoryScore(999f) <= GoalZoneScorePolicy.TotalAdvisoryBudget,
        "combined advisory scoring should remain inside the small BMR tie-break budget");
}

static void HealerCoverageUsesStableNaturalCenters()
{
    var player = Vector2.Zero;
    var members = new[]
    {
        new Vector2(-100f, 0f),
        new Vector2(30f, 0f),
        new Vector2(34f, 0f),
        new Vector2(36f, 0f)
    };

    var center = HealerAoePositioningController.SelectBestCenter(player, members, tankPosition: null);
    AssertTrue(
        center.X > 18f && center.X < 22f,
        "coverage center should move just far enough to cover the group instead of taking the exact centroid");

    var previous = center;
    var shiftedMembers = new[]
    {
        new Vector2(-100f, 0f),
        new Vector2(31f, 0f),
        new Vector2(35f, 0f),
        new Vector2(37f, 0f)
    };

    var retained = HealerAoePositioningController.SelectBestCenter(player, shiftedMembers, tankPosition: null, previous);
    AssertTrue(
        Vector2.Distance(retained, previous) < 0.01f,
        "equivalent coverage should retain the previous center instead of jumping to another party member");
}

static void HealerCoverageAvoidsBossCenterCandidates()
{
    var player = new Vector2(26f, 0f);
    var bossCenter = Vector2.Zero;
    var members = new[]
    {
        new Vector2(-1f, 0f),
        new Vector2(0f, 0f),
        new Vector2(1f, 0f)
    };

    var unrestricted = HealerAoePositioningController.SelectBestCenter(player, members, tankPosition: null);
    AssertTrue(
        unrestricted.X > 14f && unrestricted.X < player.X,
        "unrestricted coverage center should choose a player-side healing point instead of the stacked party middle");

    var restricted = HealerAoePositioningController.SelectBestCenter(
        player,
        members,
        tankPosition: null,
        previousCenter: null,
        candidateAllowed: candidate => Vector2.DistanceSquared(candidate, bossCenter) >= 400f);
    AssertTrue(
        Vector2.Distance(restricted, player) < 0.01f,
        "healer coverage should hold current position when better coverage candidates are inside boss center");
}

static void HealerCoveragePrepositionsForComfort()
{
    var player = new Vector2(18f, 0f);
    var members = new[]
    {
        new Vector2(0f, 0f),
        new Vector2(5f, 0f),
        new Vector2(15f, 0f)
    };

    AssertTrue(
        HealerAoePositioningController.TrySelectComfortCoverageCenter(
            player,
            members,
            tankPosition: null,
            previousCenter: null,
            out var center),
        "healer should find a more central point without dropping covered members");
    AssertTrue(center.X > 14f && center.X < player.X, "comfort point should move just enough toward the party, not to the party center");

    AssertTrue(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 3,
            bestCoveredCount: 3,
            totalMembers: 3,
            currentCoverageComfortSlack: 2f,
            bestCoverageComfortSlack: 11f,
            distanceToCenter: 11.5f,
            downtimeLikely: true,
            mechanicPositioningActive: false),
        "downtime should allow a bounded comfort move before healing is urgent");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 3,
            bestCoveredCount: 3,
            totalMembers: 3,
            currentCoverageComfortSlack: 2f,
            bestCoverageComfortSlack: 11f,
            distanceToCenter: 11.5f,
            downtimeLikely: false,
            mechanicPositioningActive: false),
        "routine comfort movement should stay tighter when uptime is active");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 5,
            bestCoveredCount: 6,
            totalMembers: 7,
            currentCoverageComfortSlack: 6f,
            bestCoverageComfortSlack: 6f,
            distanceToCenter: 7.5f,
            downtimeLikely: false,
            mechanicPositioningActive: false),
        "routine one-person coverage gains should not cause healer drift");

    AssertTrue(
        HealerAoePositioningController.ShouldImproveCoverageComfort(
            currentCoveredCount: 4,
            bestCoveredCount: 6,
            totalMembers: 7,
            currentCoverageComfortSlack: 6f,
            bestCoverageComfortSlack: 6f,
            distanceToCenter: 7.5f,
            downtimeLikely: false,
            mechanicPositioningActive: false),
        "routine coverage movement should require a meaningful multi-member gain");
}

static void HealerCoverageCatchesUpForPartySavingAoeHeals()
{
    var action = new RsrAoeActionSnapshot(
        ActionId: 0,
        AdjustedActionId: 0,
        ActionName: "Eukrasian Prognosis II",
        Source: "test",
        Shape: RsrAoeShape.Circle,
        Range: 1f,
        EffectRange: 20f,
        HalfWidth: 0f,
        AoeCount: 1,
        PrimaryTargetId: 0,
        PrimaryTargetPosition: Vector3.Zero,
        PrimaryTargetRadius: 0f,
        AffectedTargetCount: 0,
        IsFriendly: true,
        IsTargetArea: false,
        IsTargetCenteredCircle: false);

    AssertTrue(
        HealerAoePositioningController.IsPartyAoeHealAction(action),
        "friendly self-centered AoE heal should be recognized");

    AssertFalse(
        HealerAoePositioningController.ShouldCatchUpForPartyAoeHeal(
            currentCoveredCount: 4,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 38f,
            partyAoeHealPending: true),
        "routine party AoE heal catch-up should not chase cross-arena");

    AssertTrue(
        HealerAoePositioningController.ShouldCatchUpForPartyAoeHeal(
            currentCoveredCount: 4,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 24f,
            partyAoeHealPending: true),
        "party-saving AoE heals can take a bounded catch-up move");

    AssertFalse(
        HealerAoePositioningController.ShouldCatchUpForPartyAoeHeal(
            currentCoveredCount: 4,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 38f,
            partyAoeHealPending: false),
        "long catch-up should require an actual party AoE heal cue unless coverage is critical");
}

static void HealerCoverageCatchesUpFromCriticalPartyIsolation()
{
    AssertTrue(
        HealerAoePositioningController.ShouldCatchUpCriticalCoverage(
            currentCoveredCount: 0,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 38f),
        "critical party isolation should allow safe cross-arena recovery");

    AssertFalse(
        HealerAoePositioningController.ShouldCatchUpCriticalCoverage(
            currentCoveredCount: 4,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 38f),
        "non-critical long movement should still require a party AoE heal cue");

    AssertFalse(
        HealerAoePositioningController.ShouldCatchUpCriticalCoverage(
            currentCoveredCount: 0,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 70f),
        "critical recovery keeps a sanity bound for impossible or stale party positions");
}

static void HealerCoverageUsesUrgentHealingAwareness()
{
    AssertTrue(
        HealerAoePositioningController.ShouldImproveUrgentHealingCoverage(
            currentCoveredCount: 3,
            bestCoveredCount: 7,
            totalMembers: 8,
            currentCoversTank: true,
            bestCoversTank: true,
            partyAoeHealPending: true,
            sharedDamageSoon: false,
            tankbusterHealCoveragePending: false),
        "incoming party damage should promote a large coverage gain to urgent healing coverage");

    AssertTrue(
        HealerAoePositioningController.ShouldImproveUrgentHealingCoverage(
            currentCoveredCount: 5,
            bestCoveredCount: 5,
            totalMembers: 8,
            currentCoversTank: false,
            bestCoversTank: true,
            partyAoeHealPending: false,
            sharedDamageSoon: false,
            tankbusterHealCoveragePending: true),
        "incoming tankbusters should promote restoring tank healing coverage even without raw party-count gain");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveUrgentHealingCoverage(
            currentCoveredCount: 6,
            bestCoveredCount: 7,
            totalMembers: 8,
            currentCoversTank: true,
            bestCoversTank: true,
            partyAoeHealPending: false,
            sharedDamageSoon: false,
            tankbusterHealCoveragePending: false),
        "routine coverage gains should not be treated as urgent without incoming damage pressure");

    AssertTrue(
        HealerAoePositioningController.ShouldUseCoverageDashLanding(
            currentCoveredCount: 3,
            bestCoveredCount: 7,
            totalMembers: 8,
            distanceGain: 8f,
            landingCoveredCount: 5,
            urgent: true,
            tankbusterHealCoveragePending: false,
            currentCoversTank: false,
            landingCoversTank: false),
        "urgent party coverage dash should be allowed when it reaches several more party members");

    AssertTrue(
        HealerAoePositioningController.ShouldUseCoverageDashLanding(
            currentCoveredCount: 5,
            bestCoveredCount: 5,
            totalMembers: 8,
            distanceGain: 8f,
            landingCoveredCount: 5,
            urgent: true,
            tankbusterHealCoveragePending: true,
            currentCoversTank: false,
            landingCoversTank: true),
        "urgent tankbuster dash should be allowed when it restores tank healing coverage");

    AssertFalse(
        HealerAoePositioningController.ShouldUseCoverageDashLanding(
            currentCoveredCount: 3,
            bestCoveredCount: 7,
            totalMembers: 8,
            distanceGain: 8f,
            landingCoveredCount: 4,
            urgent: true,
            tankbusterHealCoveragePending: false,
            currentCoversTank: false,
            landingCoversTank: false),
        "urgent party coverage dash should require a meaningful coverage gain");
}

static void HealerCoverageCombinesWithForbiddenZones()
{
    AssertFalse(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            bossModGoalZoneActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false),
        "healer coverage should bias safe mechanic gaps instead of yielding to forbidden zones");

    AssertFalse(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            bossModGoalZoneActive: false,
            bmrMoveRequested: true,
            bmrMoveImminent: true),
        "existing BMR movement without mechanic hints should not suppress coverage scoring");

    AssertFalse(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            bossModGoalZoneActive: true,
            bmrMoveRequested: false,
            bmrMoveImminent: false),
        "BMR goal zones should combine with healer coverage scoring");

    AssertTrue(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: true,
            forbiddenSafetyActive: true,
            bossModGoalZoneActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false),
        "forced mechanic movement remains authoritative");
}

static void HealerCoverageRespectsCastTiming()
{
    AssertTrue(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 7f,
            gcdRemaining: 0.8f,
            gcdElapsed: 1.7f,
            gcdTotal: 2.5f,
            slidecastWindow: false,
            bossModSafetyMovementActive: false,
            intentfulCoverageMove: false,
            out var lateReason),
        "healer coverage should not start a move that would clip the next cast");
    AssertContains("too late", lateReason, "late healer coverage timing reason");

    AssertFalse(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 3f,
            gcdRemaining: 0.8f,
            gcdElapsed: 1.7f,
            gcdTotal: 2.5f,
            slidecastWindow: false,
            bossModSafetyMovementActive: false,
            intentfulCoverageMove: false,
            out _),
        "short healer coverage moves should be allowed inside the non-interrupt window");

    AssertFalse(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 7f,
            gcdRemaining: 0.8f,
            gcdElapsed: 1.7f,
            gcdTotal: 2.5f,
            slidecastWindow: false,
            bossModSafetyMovementActive: true,
            intentfulCoverageMove: false,
            out _),
        "BossMod safety movement should be allowed to take precedence over cast clipping concerns");

    AssertTrue(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 7f,
            gcdRemaining: 0.2f,
            gcdElapsed: 2.3f,
            gcdTotal: 2.5f,
            slidecastWindow: true,
            bossModSafetyMovementActive: false,
            intentfulCoverageMove: false,
            out var slideReason),
        "routine slidecast window should hold healer coverage movement instead of stepping");
    AssertContains("instant movement", slideReason, "slidecast healer coverage timing reason");

    AssertFalse(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 7f,
            gcdRemaining: 0.2f,
            gcdElapsed: 2.3f,
            gcdTotal: 2.5f,
            slidecastWindow: true,
            bossModSafetyMovementActive: false,
            intentfulCoverageMove: true,
            out _),
        "intentful healer coverage movement should not be suppressed by stale slidecast timing");

    AssertFalse(
        HealerAoePositioningController.ShouldSkipCoverageMoveForGcdTiming(
            moveDistance: 7f,
            gcdRemaining: 0.2f,
            gcdElapsed: 2.3f,
            gcdTotal: 2.5f,
            slidecastWindow: false,
            bossModSafetyMovementActive: false,
            intentfulCoverageMove: true,
            out _),
        "intentful healer coverage movement should trust RSR movement-safe casts near GCD ready");

    AssertTrue(
        HealerAoePositioningController.ShouldSuppressSlideWindowCoverage(
            slidecastWindow: true,
            intentfulCoverageMove: false),
        "routine healer coverage should not step just because a slidecast window opened");

    AssertFalse(
        HealerAoePositioningController.ShouldSuppressSlideWindowCoverage(
            slidecastWindow: true,
            intentfulCoverageMove: true),
        "urgent healing coverage should be allowed after it becomes intentional movement");

    AssertTrue(
        HealerAoePositioningController.ShouldSuppressSlideWindowCoverage(
            slidecastWindow: true,
            intentfulCoverageMove: false),
        "trash and non-boss contexts should also require healer coverage intent before slide movement");

    AssertFalse(
        HealerAoePositioningController.ShouldSuppressSlideWindowCoverage(
            slidecastWindow: true,
            intentfulCoverageMove: true),
        "many missing party members should count as healer coverage intent");

    AssertTrue(
        HealerAoePositioningController.ShouldSuppressSlideWindowCoverage(
            slidecastWindow: true,
            intentfulCoverageMove: false),
        "mechanic positioning without coverage intent should not allow slidecast stepping");

    AssertTrue(
        HealerAoePositioningController.ShouldAllowCoverageDashDuringBossModMovement(
            urgentHealingCoverage: false,
            partyAoeHealCatchUp: false,
            criticalCoverageCatchUp: false,
            tankbusterHealCoveragePending: false,
            mechanicPositioningActive: true,
            bossCoverageMove: true,
            proactiveCoverageComfort: false,
            strongCoverageGain: false),
        "mechanic coverage can use a validated dash while BossMod movement is active");

    AssertFalse(
        HealerAoePositioningController.ShouldAllowCoverageDashDuringBossModMovement(
            urgentHealingCoverage: false,
            partyAoeHealCatchUp: false,
            criticalCoverageCatchUp: false,
            tankbusterHealCoveragePending: false,
            mechanicPositioningActive: false,
            bossCoverageMove: true,
            proactiveCoverageComfort: false,
            strongCoverageGain: false),
        "routine boss coverage should not dash over active BossMod movement");

    AssertTrue(
        HealerAoePositioningController.IsBossModSafetyMovementActive(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            bossModGoalZoneActive: false,
            bmrMoveImminent: true),
        "imminent BossMod movement with safety hints should count as BossMod taking control");

    AssertFalse(
        HealerAoePositioningController.IsBossModSafetyMovementActive(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            bossModGoalZoneActive: false,
            bmrMoveImminent: false),
        "passive forbidden zones alone should not bypass cast timing");
}

static void HealerBossCoveragePrefersRangedComfort()
{
    AssertTrue(
        HealerAoePositioningController.ShouldImproveBossCoveragePosition(
            currentCoveredCount: 7,
            bestCoveredCount: 7,
            totalMembers: 8,
            currentBossRangeScore: 0.45f,
            bestBossRangeScore: 0.75f,
            bossModuleContext: true),
        "boss healer coverage should move for a better ranged position when party coverage is preserved");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveBossCoveragePosition(
            currentCoveredCount: 7,
            bestCoveredCount: 7,
            totalMembers: 8,
            currentBossRangeScore: 0.45f,
            bestBossRangeScore: 0.75f,
            bossModuleContext: false),
        "non-boss contexts should not use boss ranged comfort");

    AssertFalse(
        HealerAoePositioningController.ShouldImproveBossCoveragePosition(
            currentCoveredCount: 7,
            bestCoveredCount: 6,
            totalMembers: 8,
            currentBossRangeScore: 0.45f,
            bestBossRangeScore: 0.95f,
            bossModuleContext: true),
        "boss ranged comfort should not trade away existing healer coverage");
}

static void PartyHealerRangeMovesDpsForRaidDamage()
{
    AssertFalse(
        PartyHealerRangePositioningController.ShouldSkipPartyHealerRangeForRole(25),
        "DPS jobs should be eligible for party healer range movement");

    AssertTrue(
        PartyHealerRangePositioningController.ShouldSkipPartyHealerRangeForRole(19),
        "tank jobs should not move toward healers for raid damage");

    AssertTrue(
        PartyHealerRangePositioningController.ShouldSkipPartyHealerRangeForRole(24),
        "healer jobs use the healer coverage controller instead");

    AssertTrue(
        PartyHealerRangePositioningController.ShouldMoveForPartyHealerRange(BossModMechanicPressure.None with { BMRRaidwideIn = 2f }),
        "raidwide pressure should trigger party healer range movement");

    AssertTrue(
        PartyHealerRangePositioningController.ShouldMoveForPartyHealerRange(BossModMechanicPressure.None with
        {
            BMRDamageIn = 2f,
            BMRNextDamageType = (int)BossModPredictedDamageType.Shared
        }),
        "shared raid damage should trigger party healer range movement");

    AssertFalse(
        PartyHealerRangePositioningController.ShouldMoveForPartyHealerRange(BossModMechanicPressure.None with
        {
            BMRDamageIn = 2f,
            BMRNextDamageType = (int)BossModPredictedDamageType.Tankbuster
        }),
        "tankbusters should not trigger DPS healer-range movement");

    var preferred = PartyHealerRangePositioningController.FindPreferredEntryPosition(
        new Vector2(25f, 0f),
        Vector2.Zero,
        25f);
    AssertApproximately(18.5f, preferred.X, 0.001f, "preferred entry x");
    AssertApproximately(0f, preferred.Y, 0.001f, "preferred entry y");
}

static void DefensiveZoneMovementSkipsTanks()
{
    AssertTrue(
        SurvivabilityZonePositioningController.ShouldSkipDefensiveZoneMovementForRole(19),
        "tank jobs should not move toward defensive or healing ground zones");

    AssertFalse(
        SurvivabilityZonePositioningController.ShouldSkipDefensiveZoneMovementForRole(24),
        "healer jobs may still use defensive ground zones");

    AssertFalse(
        SurvivabilityZonePositioningController.ShouldSkipDefensiveZoneMovementForRole(25),
        "non-tank DPS jobs may still use defensive ground zones");

    AssertTrue(
        SurvivabilityZonePositioningController.ResolveDefensiveGroundZoneMoveMode(
            classJobId: 23,
            pressure: BossModMechanicPressure.None,
            playerHasPersonalThreat: false,
            playerLowHealth: false) == DefensiveGroundZoneMoveMode.PhysicalRangedComfort,
        "physical ranged jobs may use friendly defensive zones as a no-pressure comfort preference");

    AssertTrue(
        SurvivabilityZonePositioningController.ResolveDefensiveGroundZoneMoveMode(
            classJobId: 25,
            pressure: BossModMechanicPressure.None,
            playerHasPersonalThreat: false,
            playerLowHealth: false) == DefensiveGroundZoneMoveMode.None,
        "magic ranged jobs should still need damage pressure or low health for defensive ground zones");

    AssertTrue(
        SurvivabilityZonePositioningController.ResolveDefensiveGroundZoneMoveMode(
            classJobId: 23,
            pressure: BossModMechanicPressure.None with { BMRRaidwideIn = 2f },
            playerHasPersonalThreat: false,
            playerLowHealth: false) == DefensiveGroundZoneMoveMode.DamagePressure,
        "damage pressure should keep the stronger defensive movement mode even for physical ranged");
}

static void PackMovementCombinesWithIdleBossModForbiddenZones()
{
    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: true),
        "boss-context pack movement should bias safe mechanic gaps instead of yielding to forbidden zones");

    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "trash pack movement should bias safe mechanic gaps instead of yielding to forbidden zones");

    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: true,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "temporary obstacles should stay BMR pathfinding constraints rather than suppressing scoring");

    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: true,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "forbidden directions should stay BMR facing constraints rather than suppressing scoring");

    AssertTrue(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: true,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "trash pack movement should yield to special movement modes");

    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: true,
            bmrMoveImminent: true,
            bossModuleContext: false),
        "existing BMR movement should not suppress pack scoring");

    AssertTrue(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: true,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "active BMR forbidden-zone movement should suppress trash pack scoring");

    AssertTrue(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: true,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: true,
            bossModuleContext: false),
        "imminent BMR obstacle movement should suppress trash pack scoring");

    AssertTrue(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: true,
            forbiddenSafetyActive: false,
            temporaryObstacleSafetyActive: false,
            forbiddenDirectionSafetyActive: false,
            specialModeSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "forced mechanic movement remains authoritative");
}

static void MechanicWhisperGuardKeepsAlignedShorterOrConfidentGoals()
{
    AssertTrue(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(11f, 0f),
            bossModDestination: new Vector2(12f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.Zero),
        "aligned mechanic whispers should pass immediately");

    AssertFalse(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(10f, 0f),
            bossModDestination: new Vector2(30f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.FromMilliseconds(100)),
        "shorter but unstable mechanic whispers should wait");

    AssertTrue(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(10f, 0f),
            bossModDestination: new Vector2(30f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.FromMilliseconds(300)),
        "stable shorter mechanic whispers should pass");

    AssertFalse(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(25f, 0f),
            bossModDestination: new Vector2(30f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.FromMilliseconds(300)),
        "non-shorter side whispers need explicit confidence");

    AssertFalse(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(25f, 0f),
            bossModDestination: new Vector2(30f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.FromMilliseconds(700)),
        "routine side whispers should not redirect BMR just because they lingered");

    AssertTrue(
        BossModGoalZoneHook.ShouldAllowMechanicWhisperCandidate(
            candidate: new Vector2(25f, 0f),
            bossModDestination: new Vector2(30f, 0f),
            playerPosition: Vector2.Zero,
            stableFor: TimeSpan.FromMilliseconds(500),
            confidence: MechanicWhisperConfidence.Confident),
        "confident stable side whispers should redirect BMR");
}

static void MechanicSafetyIsolatesTopGoalContributions()
{
    static float Goal(object _) => 0f;

    var contributions = new[]
    {
        new BossModGoalContribution((Func<object, float>)Goal, BossModGoalPriority.Convenience, "Boss center avoidance"),
        new BossModGoalContribution((Func<object, float>)Goal, BossModGoalPriority.Uptime, "Target uptime"),
        new BossModGoalContribution((Func<object, float>)Goal, BossModGoalPriority.Uptime, "Pack engagement"),
    };

    var selected = BossModGoalZoneHook.SelectMechanicSafetyGoalContributions(contributions);
    AssertEqual(2, selected.Length, "mechanic safety selected count");
    AssertTrue(selected.All(c => c.Priority == BossModGoalPriority.Uptime), "mechanic safety selected top priority only");

    AssertTrue(
        BossModGoalZoneHook.ShouldIsolateMechanicSafetyGoals(
            forbiddenZones: 1,
            temporaryObstacles: 0,
            forbiddenDirections: 0,
            imminentSpecialMode: "<none>",
            forcedMovementActive: false),
        "forbidden zones isolate advisory goals");

    AssertFalse(
        BossModGoalZoneHook.ShouldIsolateMechanicSafetyGoals(
            forbiddenZones: 0,
            temporaryObstacles: 0,
            forbiddenDirections: 0,
            imminentSpecialMode: "<none>",
            forcedMovementActive: false),
        "normal movement can combine advisory goals");
}

static void MechanicEscapeMarginFollowsBmrMovement()
{
    AssertTrue(
        BossModGoalZoneHook.TryResolveMechanicEscapeMarginCandidate(
            playerPosition: new Vector2(140.57f, -443.75f),
            desiredMovement: new Vector3(6.18f, -28f, 0f),
            forbiddenZonesActive: true,
            forcedMovementActive: false,
            moveRequested: true,
            moveImminent: true,
            out var firstCandidate),
        "mechanic margin should follow BMR's direct safety movement");
    AssertTrue(
        MathF.Abs(firstCandidate.X - 146.75f) < 0.02f &&
        MathF.Abs(firstCandidate.Y + 443.75f) < 0.02f,
        "mechanic margin candidate should extend along BMR movement");

    AssertTrue(
        BossModGoalZoneHook.TryResolveMechanicEscapeMarginCandidate(
            playerPosition: new Vector2(145.07f, -443.75f),
            desiredMovement: new Vector3(1.56f, -28f, 0f),
            forbiddenZonesActive: true,
            forcedMovementActive: false,
            moveRequested: true,
            moveImminent: true,
            out var lateCandidate),
        "late mechanic margin should remain active while BMR still asks for movement");
    AssertTrue(
        Vector2.Distance(firstCandidate, lateCandidate) < 0.25f,
        "mechanic margin candidate should remain stable as the player approaches it");

    AssertFalse(
        BossModGoalZoneHook.TryResolveMechanicEscapeMarginCandidate(
            playerPosition: Vector2.Zero,
            desiredMovement: new Vector3(4f, 0f, 0f),
            forbiddenZonesActive: true,
            forcedMovementActive: true,
            moveRequested: true,
            moveImminent: true,
            out _),
        "forced mechanic movement remains authoritative");

    AssertFalse(
        BossModGoalZoneHook.TryResolveMechanicEscapeMarginCandidate(
            playerPosition: Vector2.Zero,
            desiredMovement: new Vector3(4f, 0f, 0f),
            forbiddenZonesActive: false,
            forcedMovementActive: false,
            moveRequested: true,
            moveImminent: true,
            out _),
        "mechanic margin requires active forbidden zones");
}

static void TrashAoeShortOneHitGainIsWorthMoving()
{
    AssertFalse(
        AoePackPositioningController.ShouldSkipMarginalAoeReposition(
            currentHits: 5,
            bestHits: 6,
            targetCount: 6,
            moveDistance: 4f,
            out _),
        "short movement for one extra trash AoE hit should be allowed");

    AssertTrue(
        AoePackPositioningController.ShouldSkipMarginalAoeReposition(
            currentHits: 5,
            bestHits: 6,
            targetCount: 6,
            moveDistance: 6f,
            out var reason),
        "long movement for one extra trash AoE hit should remain bounded");
    AssertContains("skipped", reason, "marginal AoE skip reason");
}

static void LocalHealerAoeFallbackKeepsArtOfWarClose()
{
    AssertFalse(
        AoePackPositioningController.ShouldUseMeleeAoeHealerFallback(
            classJobId: 24,
            packAoeRange: 8f,
            inAoeSituation: true,
            targetCount: 2),
        "WHM should not be forced into melee for only two pack targets");

    AssertTrue(
        AoePackPositioningController.ShouldUseMeleeAoeHealerFallback(
            classJobId: 24,
            packAoeRange: 8f,
            inAoeSituation: true,
            targetCount: 3),
        "WHM should run in for Holy on 3+ target packs when healing pressure is not active");

    AssertTrue(
        AoePackPositioningController.ShouldUseMeleeAoeHealerFallback(
            classJobId: 28,
            packAoeRange: 5f,
            inAoeSituation: true,
            targetCount: 2),
        "SCH Art of War should use local melee AoE fallback on 2+ target packs");

    AssertEqual(
        Configuration.InternalMeleeUptimeRange,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 40,
            packAoeRange: 5f,
            inAoeSituation: true,
            targetCount: 3),
        "SGE should run in for Dyskrasia on 3+ target packs when healing pressure is not active");

    AssertEqual(
        Configuration.InternalMeleeUptimeRange,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 28,
            packAoeRange: 5f,
            inAoeSituation: true,
            targetCount: 2),
        "SCH Art of War fallback should use melee engagement range");

    AssertEqual(
        5f,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 40,
            packAoeRange: 5f,
            inAoeSituation: true,
            targetCount: 3,
            urgentHealingPressure: true),
        "SGE should hold its normal range instead of forcing melee when healing pressure is active");

    AssertEqual(
        25f,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 33,
            packAoeRange: 25f,
            inAoeSituation: true),
        "AST Gravity should remain a ranged AoE");

    AssertEqual(
        24f,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 24,
            packAoeRange: 24f,
            inAoeSituation: true),
        "healers below their self-centered AoE level should remain ranged");

    AssertEqual(
        8f,
        AoePackPositioningController.ResolveEffectivePackAoeRange(
            classJobId: 24,
            packAoeRange: 8f,
            inAoeSituation: false),
        "single-target contexts should not force WHM into melee");

    AssertTrue(
        AoePackPositioningController.ShouldSuppressHealerPackAoeFallback(BossModMechanicPressure.None with { BMRTankbusterIn = 2f }),
        "incoming tankbuster healing pressure should suppress healer pack AoE fallback");
}

static void TrashAoePrepSkipsGoodEnoughOneHitChurn()
{
    AssertTrue(
        AoePackPositioningController.ShouldSkipProactiveAoeReposition(
            currentHits: 5,
            bestHits: 6,
            targetCount: 6,
            out var reason),
        "proactive AoE prep should not chase a single extra hit once coverage is good enough");
    AssertContains("good AoE prep coverage", reason, "proactive AoE prep skip reason");

    AssertFalse(
        AoePackPositioningController.ShouldSkipProactiveAoeReposition(
            currentHits: 4,
            bestHits: 6,
            targetCount: 6,
            out _),
        "proactive AoE prep should still allow meaningful two-hit improvements");
}

static void TrashAoeRetainsEqualHitCandidate()
{
    var targets = new[]
    {
        new TargetSnapshot(1, new Vector2(0f, 0f), 0.5f),
        new TargetSnapshot(2, new Vector2(1f, 0f), 0.5f),
        new TargetSnapshot(3, new Vector2(0f, 1f), 0.5f)
    };
    var plan = new GoalPlan(
        RsrAoeShape.Circle,
        targets,
        targets[0],
        radius: 5f,
        range: 5f,
        halfWidth: 0f,
        minHits: 2);
    var retained = new Vector2(-2f, -2f);
    var best = plan.FindBestCandidate(
        playerPosition: new Vector2(10f, 10f),
        candidateAllowed: null,
        retainedPosition: retained);

    AssertEqual(retained, best.Position, "equal-hit retained AoE candidate");
    AssertEqual(3, best.Hits, "retained candidate hits");
}

static void TrashTargetRetentionYieldsToTankPack()
{
    AssertFalse(
        AoePackPositioningController.ShouldRetainLocalTrashTarget(
            playerAnchorDistance: 24f,
            currentSurfaceDistance: 2f,
            currentAnchorDistance: 28f,
            remoteAnchorDistance: 4f),
        "local trash target should not be retained when tank-side pack is clearly better");

    AssertTrue(
        AoePackPositioningController.ShouldRetainLocalTrashTarget(
            playerAnchorDistance: 6f,
            currentSurfaceDistance: 2f,
            currentAnchorDistance: 8f,
            remoteAnchorDistance: 4f),
        "nearby local trash target can be retained while still near tank");

    AssertFalse(
        AoePackPositioningController.ShouldRetainLocalTrashTarget(
            playerAnchorDistance: 6f,
            currentSurfaceDistance: 8f,
            currentAnchorDistance: 8f,
            remoteAnchorDistance: 4f),
        "distant current target should not be retained");
}

static void MultiTargetLargeTrashRemainsTrashContext()
{
    AssertTrue(
        AoePackPositioningController.IsPackLikeTrashContext(
            bossModEncounterActive: false,
            targetHasBossModule: true,
            effectivePackTargetCount: 2),
        "two visible enemies remain trash even when the current target has a BMR module");

    AssertFalse(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: false,
            targetHasBossModule: true,
            packLikeTrashContext: true,
            hitboxBossLikeContext: true,
            previousBossLikeCombatActive: true),
        "multi-target BMR-tagged trash clears sticky boss context");

    AssertTrue(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: false,
            targetHasBossModule: true,
            packLikeTrashContext: false,
            hitboxBossLikeContext: false,
            previousBossLikeCombatActive: false),
        "single target with a BMR module remains boss-like");

    AssertTrue(
        AoePackPositioningController.IsPackLikeTrashContext(
            bossModEncounterActive: false,
            targetHasBossModule: false,
            effectivePackTargetCount: 2),
        "two visible non-module enemies are a trash pack");

    AssertFalse(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: false,
            targetHasBossModule: false,
            packLikeTrashContext: true,
            hitboxBossLikeContext: true,
            previousBossLikeCombatActive: true),
        "multi-target trash clears sticky hitbox-only boss context");

    AssertFalse(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: true,
            targetHasBossModule: false,
            packLikeTrashContext: true,
            hitboxBossLikeContext: false,
            previousBossLikeCombatActive: false),
        "multi-target hallway BMR module remains trash context");

    AssertFalse(
        AoePackPositioningController.IsPackLikeTrashContext(
            bossModEncounterActive: true,
            targetHasBossModule: false,
            effectivePackTargetCount: 2,
            hitboxBossLikeContext: true),
        "multi-target real boss module with a boss-sized target is not trash context");

    AssertTrue(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: true,
            targetHasBossModule: false,
            packLikeTrashContext: false,
            hitboxBossLikeContext: true,
            previousBossLikeCombatActive: false),
        "real BossMod encounter context remains boss-like");

    AssertTrue(
        AoePackPositioningController.IsPackLikeTrashContext(
            bossModEncounterActive: true,
            targetHasBossModule: false,
            effectivePackTargetCount: 2),
        "multi-target hallway BMR module without a boss-sized target is trash context");

    AssertTrue(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: false,
            targetHasBossModule: false,
            packLikeTrashContext: false,
            hitboxBossLikeContext: true,
            previousBossLikeCombatActive: false),
        "single large hitbox target still behaves boss-like");
}

static void TrashPullTrackerPhaseTransitions()
{
    var tracker = new TrashPullStateTracker();
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    tracker.Update(TrashObservation(start, player: new Vector3(0, 0, 0), tank: new Vector3(5, 0, 0), pack: 5));
    tracker.Update(TrashObservation(start.AddMilliseconds(300), player: new Vector3(0, 0, 0), tank: new Vector3(6.5f, 0, 0), pack: 6));
    var gathering = tracker.Update(TrashObservation(start.AddMilliseconds(900), player: new Vector3(0, 0, 0), tank: new Vector3(9.5f, 0, 0), pack: 8));
    AssertTrue(gathering.Phase == TrashPullPhase.Gathering, "gathering phase");
    AssertTrue(gathering.Confidence >= 0.65f, "gathering confidence");

    tracker.Update(TrashObservation(start.AddSeconds(1.2), player: new Vector3(6, 0, 0), tank: new Vector3(9.5f, 0, 0), pack: 8));
    var burning = tracker.Update(TrashObservation(start.AddSeconds(3.2), player: new Vector3(7, 0, 0), tank: new Vector3(9.5f, 0, 0), pack: 8));
    AssertTrue(burning.Phase == TrashPullPhase.Burning, "burning phase");
}

static void TrashPullTrackerRemoteSettledPackRemainsCatchUp()
{
    var tracker = new TrashPullStateTracker();
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    tracker.Update(TrashObservation(start, player: new Vector3(0, 0, 0), tank: new Vector3(60, 0, 0), pack: 62));
    var status = tracker.Update(TrashObservation(start.AddSeconds(2.5), player: new Vector3(0, 0, 0), tank: new Vector3(60, 0, 0), pack: 62));

    AssertTrue(status.Phase == TrashPullPhase.Gathering, "remote settled pack stays in catch-up/gathering");
}

static void BmrParserIntegration()
{
    BmrReplayData replay;
    try
    {
        replay = BmrReplayReader.Read(FixturePath("minimal-bmr.log"));
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("game data", StringComparison.OrdinalIgnoreCase))
    {
        throw new SkipTestException(ex.Message);
    }

    AssertTrue(replay.Summary.OperationCount > 0, "BMR operation count");
    AssertTrue(replay.Summary.Start.HasValue, "BMR start timestamp");
}

static void LowConfidenceAutoMatchRejected()
{
    var bmr = new BmrReplayData(
        "low-confidence.log",
        new BossMod.Replay(),
        new BmrSummary("low-confidence.log", null, null, 0, 0, [], [], []));
    var match = new MatchResult(bmr.Path, 0.1d, ["fixture"]);

    var ex = AssertThrows<InvalidOperationException>(() => BmrReplayReader.ValidateAutoMatchConfidence(bmr, match, "fixtures"));
    AssertContains("confidence", ex.Message, "low confidence rejection");
}

static void ExplicitMultiEncounterMatchScoresNearestEncounter()
{
    var startUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    const ulong targetObjectId = 0xABCUL;
    const uint targetOid = 15728;
    var frames = new[]
    {
        Frame(0, activeModule: "A24Menphina", targetBaseId: targetOid, targetObjectId: targetObjectId)
    };
    var log = Log(frames);
    log = log with
    {
        Header = log.Header with
        {
            CombatStartUtc = startUtc,
            CombatEndUtc = startUtc.AddSeconds(60),
            TerritoryType = 1118,
            ContentFinderConditionId = 911,
            BossModActiveModule = "A24Menphina"
        }
    };
    var localStart = startUtc.ToLocalTime();
    var bmr = new BmrReplayData(
        "multi-encounter.log",
        new BossMod.Replay(),
        new BmrSummary(
            "multi-encounter.log",
            localStart.AddMinutes(-25),
            localStart.AddMinutes(2),
            0,
            2,
            [
                Encounter(0, 0x111UL, 99999, 1118, localStart.AddMinutes(-20), localStart.AddMinutes(-17)),
                Encounter(1, targetObjectId, targetOid, 1118, localStart.AddSeconds(-5), localStart.AddSeconds(70))
            ],
            [
                new BmrParticipantSummary(
                    targetObjectId,
                    targetOid,
                    "Enemy-15728",
                    "Enemy",
                    1118,
                    911,
                    localStart.AddSeconds(-5),
                    localStart.AddSeconds(70),
                    1f,
                    12f,
                    false,
                    true,
                    false,
                    true,
                    [])
            ],
            []));

    var match = BmrReplayReader.ScoreExplicitMatch(log, bmr);
    AssertTrue(match.Confidence >= 0.75d, $"expected high multi-encounter match confidence, got {match.Confidence:0.00}");
    AssertContains("overlaps BMR encounter #1", string.Join('\n', match.Evidence), "nearest encounter evidence");
}

static void DestinationChurnDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(0, 0, 0), chosenSource: "comfort"),
        Frame(0.8f, destination: new Vec3(2, 0, 0), chosenSource: "comfort"),
        Frame(1.6f, destination: new Vec3(0, 0, 2), chosenSource: "comfort"),
        Frame(2.4f, destination: new Vec3(2, 0, 2), chosenSource: "comfort"),
        Frame(3.2f, destination: new Vec3(-2, 0, 2), chosenSource: "comfort"),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "destination-churn");
}

static void IndecisiveOscillationDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(0, 0, 0), chosenSource: "comfort"),
        Frame(0.7f, destination: new Vec3(4, 0, 0), chosenSource: "comfort"),
        Frame(1.4f, destination: new Vec3(0.2f, 0, 0), chosenSource: "comfort"),
        Frame(2.1f, destination: new Vec3(4.1f, 0, 0), chosenSource: "comfort"),
        Frame(2.8f, destination: new Vec3(0.1f, 0, 0), chosenSource: "comfort"),
        Frame(3.5f, destination: new Vec3(4.2f, 0, 0), chosenSource: "comfort"),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "indecisive-oscillation");
}

static void MovementStuckDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "range", playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(0.6f, destination: new Vec3(8, 0, 0), chosenSource: "range", playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(1.2f, destination: new Vec3(8, 0, 0), chosenSource: "range", playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(1.8f, destination: new Vec3(8, 0, 0), chosenSource: "range", playerStepDistance: 0.01f, playerSpeed: 0.05f),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "movement-stuck");
}

static void VnavmeshDetourDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, 0),
        chosenSource: "Target range",
        pathStatus: "Reachable",
        pathDistance: 28,
        directDistance: 10,
        extraPathDistance: 18,
        pathDetourRatio: 2.8f);

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "vnavmesh-detour");
}

static void VnavmeshOffmeshDestinationDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, 0),
        chosenSource: "Target range",
        vnavmeshDestination: new VnavmeshPointSnapshot("Ready", null, null, null, 2.2f, null, null));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "vnavmesh-offmesh-destination");
}

static void VnavmeshOffmeshIgnoredWithoutActiveDestination()
{
    var frame = Frame(
        0,
        destination: null,
        chosenSource: "<none>",
        generatedCount: 1,
        vnavmeshDestination: new VnavmeshPointSnapshot("Ready", null, null, null, 2.2f, null, null));

    AssertNoIncident(IncidentDetector.Detect(Log(frame)), "vnavmesh-offmesh-destination");
}

static void VnavmeshQueryStallDetector()
{
    var frames = new[]
    {
        Frame(0, pathStatus: "Pending", vnavmesh: new VnavmeshRuntimeSnapshot(true, true, true, 4)),
        Frame(0.5f, pathStatus: "Pending", vnavmesh: new VnavmeshRuntimeSnapshot(true, true, true, 4)),
        Frame(1.0f, pathStatus: "Pending", vnavmesh: new VnavmeshRuntimeSnapshot(true, true, true, 3)),
        Frame(1.5f, pathStatus: "Pending", vnavmesh: new VnavmeshRuntimeSnapshot(true, true, true, 2)),
        Frame(2.0f, pathStatus: "Pending", vnavmesh: new VnavmeshRuntimeSnapshot(true, true, true, 2)),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "vnavmesh-query-stall");
}

static void VnavmeshReachableStuckDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "range", pathStatus: "Reachable", firstWaypointDistance: 2, playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(0.6f, destination: new Vec3(8, 0, 0), chosenSource: "range", pathStatus: "Reachable", firstWaypointDistance: 2, playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(1.2f, destination: new Vec3(8, 0, 0), chosenSource: "range", pathStatus: "Reachable", firstWaypointDistance: 2, playerStepDistance: 0.01f, playerSpeed: 0.05f),
        Frame(1.8f, destination: new Vec3(8, 0, 0), chosenSource: "range", pathStatus: "Reachable", firstWaypointDistance: 2, playerStepDistance: 0.01f, playerSpeed: 0.05f),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "vnavmesh-reachable-stuck");
}

static void SafetyRasterParsing()
{
    using var temp = TempDirectory.Create();
    var path = Path.Combine(temp.Path, "safety-raster.jsonl");
    File.WriteAllText(
        path,
        """
{"Type":"header","SchemaVersion":3,"PluginVersion":"tests","CombatStartUtc":"2026-01-01T00:00:00.0000000Z","CombatEndUtc":"2026-01-01T00:00:01.0000000Z","DurationSeconds":1,"FrameCount":1,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"BossModActiveModule":"Boss","BossModActiveZoneModule":"<none>","Config":{"FightReviewLoggingEnabled":true}}
{"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:00.0000000Z","T":0,"InCombat":true,"IsDead":false,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"PlayerPosition":{"X":84,"Y":0,"Z":388},"PlayerRotation":0,"TargetBaseId":1,"TargetObjectId":1,"TargetPosition":{"X":84,"Y":0,"Z":370},"TargetRotation":0,"TargetRadius":4.5,"TargetUptimeRange":25,"BossModActiveModule":"Boss","BossModActiveZoneModule":"<none>","MovementPlanner":{"IntentId":"<none>","ChosenSource":"<none>","Destination":null,"AcceptanceRadius":null,"SwitchReason":"none","SuppressionReason":"none","GeneratedCount":0,"AcceptedCount":0,"RejectedByReason":{},"TopCandidates":[],"ScoreBreakdown":"<none>","PathStatus":"None","BmrForcedMovement":null,"BmrForbiddenZones":0,"BmrMoveRequested":false,"BmrMoveImminent":false},"BossModMovement":{"MovementOverride":"<none>","HintSummary":"none","HintDetails":{"PathfindMapCenter":{"X":84,"Y":370},"PathfindMapBounds":{"Radius":19.5},"GoalZones":0,"ForbiddenZones":0,"ImminentSpecialMode":"<none>"},"SafetyRaster":{"Status":"captured","Reason":"ok","Center":{"X":84,"Y":370},"RotationRadians":0,"SourceResolution":0.5,"SourceWidth":4,"SourceHeight":4,"CellScale":1,"Width":4,"Height":4,"MaxG":3,"MaxPriority":2,"Encoding":"rle-v1","CellsRle":"1:4,0:8,2:4","Player":{"State":"safe","Position":{"X":84,"Y":0,"Z":388},"GridX":2,"GridY":2,"PixelMaxG":3.4028235E+38,"PixelPriority":0},"Destination":{"State":"blocked","Position":{"X":83,"Y":0,"Z":388},"GridX":0,"GridY":0,"PixelMaxG":-1000,"PixelPriority":-3.4028235E+38},"FirstWaypoint":{"State":"unknown","Position":null,"GridX":null,"GridY":null,"PixelMaxG":null,"PixelPriority":null},"Target":{"State":"safe","Position":{"X":84,"Y":0,"Z":370},"GridX":2,"GridY":1,"PixelMaxG":3.4028235E+38,"PixelPriority":0}}},"Actors":[]},"Motion":{"TargetDistance":18,"TargetSurfaceDistance":13.5}}
""");

    var log = XcaiLogReader.Read(path);
    AssertEqual("captured", log.Frames[0].BossMod.SafetyRaster.Status, "safety status");
    AssertEqual("blocked", log.Frames[0].BossMod.SafetyRaster.Destination.State, "safety destination");
    AssertEqual(new Vec3(84, 0, 370), log.Frames[0].BossMod.SafetyRaster.Center!, "safety center");
}

static void SafetyRasterRleCodec()
{
    var cells = new[] { 1, 1, 1, 0, 0, 2, 3, 3 };
    var rle = SafetyRasterCodec.Encode(cells);
    AssertEqual("1:3,0:2,2:1,3:2", rle, "RLE encode");
    AssertTrue(cells.SequenceEqual(SafetyRasterCodec.Decode(rle, cells.Length)), "RLE decode");
    AssertTrue(new[] { 1, 1, 0, 0 }.SequenceEqual(SafetyRasterCodec.Decode("1:2", 4)), "RLE decode pads safe");
}

static void SafetyBlockedDestinationDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, 0),
        chosenSource: "Target range",
        safetyRaster: Raster(destination: "blocked"));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "safety-blocked-destination");
}

static void SafetyBlockedRouteDetector()
{
    var cells = Enumerable.Repeat(SafetyRasterCodec.Safe, 12 * 3).ToArray();
    cells[(1 * 12) + 6] = SafetyRasterCodec.Blocked;
    var frame = Frame(
        0,
        playerPosition: new Vec3(-4, 0, 0),
        destination: new Vec3(4, 0, 0),
        chosenSource: "Target range",
        safetyRaster: RasterCells(12, 3, cells, sourceResolution: 1f));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "safety-blocked-route");
}

static void SafetyBoundaryStuckDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "Target range", playerStepDistance: 0, playerSpeed: 0, safetyRaster: Raster(destination: "blocked")),
        Frame(0.6f, destination: new Vec3(8, 0, 0), chosenSource: "Target range", playerStepDistance: 0, playerSpeed: 0, safetyRaster: Raster(destination: "blocked")),
        Frame(1.2f, destination: new Vec3(8, 0, 0), chosenSource: "Target range", playerStepDistance: 0, playerSpeed: 0, safetyRaster: Raster(destination: "blocked")),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "safety-boundary-stuck");
}

static void BmrExitWallRiskDetector()
{
    var frame = Frame(
        0,
        chosenSource: "<none>",
        destination: null,
        activeModule: "Boss",
        bmrMoveRequested: true,
        safetyRaster: Raster(destination: "blocked"));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "bmr-exit-wall-risk");
    AssertNoIncident(IncidentDetector.Detect(Log(frame)), "safety-blocked-destination");
}

static void GreedFutureDangerSafetyLingerIsNotBlocked()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", destination: new Vec3(8, 0, 0), chosenSource: "Target range", safetyRaster: Raster(destination: "future-danger")),
        Frame(0.6f, activeModule: "Boss", destination: new Vec3(8, 0, 0), chosenSource: "Target range", safetyRaster: Raster(destination: "future-danger")),
    };

    AssertNoIncident(IncidentDetector.Detect(LogWithCombatStyle("Greed", frames)), "safety-blocked-destination");
}

static void HtmlIncludesSafetyRasterControls()
{
    using var temp = TempDirectory.Create();
    var log = Log(Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "Target range", safetyRaster: Raster(destination: "blocked")));
    var review = new ReviewBundle(
        log,
        new BmrSummary("none.log", null, null, 0, 0, [], [], []),
        new MatchResult("none.log", 1, ["fixture"]),
        IncidentDetector.Detect(log));

    ArtifactWriter.Write(review, temp.Path);
    var html = File.ReadAllText(Path.Combine(temp.Path, "fight.html"));
    AssertContains("layerSafety", html, "safety layer control");
    AssertContains("SafetyRaster", html, "safety raster payload");
    AssertContains("drawSafetyRaster", html, "safety raster renderer");
}

static void SlowPackFollowDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(0, 0, 0), playerSpeed: null, actors: [PartyActor(new Vec3(0, 0, 0))]),
        Frame(1, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(1, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(6, 0, 0))]),
        Frame(2, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(2, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(12, 0, 0))]),
        Frame(3.2f, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(3.2f, 0, 0), playerStepDistance: 1.2f, playerSpeed: 1, actors: [PartyActor(new Vec3(19.2f, 0, 0))]),
        Frame(4.2f, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(4.2f, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(25.2f, 0, 0))]),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "slow-pack-follow");
    AssertContains("party", incident.Evidence, "slow movement party evidence");
}

static void SlowSafeCorridorPackFollowDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(0, 0, 0), playerSpeed: null, actors: [PartyActor(new Vec3(0, 0, 0))], safetyRaster: Raster()),
        Frame(1, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(1, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(6, 0, 0))], safetyRaster: Raster()),
        Frame(2, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(2, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(12, 0, 0))], safetyRaster: Raster()),
        Frame(3.2f, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(3.2f, 0, 0), playerStepDistance: 1.2f, playerSpeed: 1, actors: [PartyActor(new Vec3(19.2f, 0, 0))], safetyRaster: Raster()),
        Frame(4.2f, destination: new Vec3(12, 0, 0), chosenSource: "Pack engagement", playerPosition: new Vec3(4.2f, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(25.2f, 0, 0))], safetyRaster: Raster()),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "slow-pack-follow");
    AssertContains("Safety raster", incident.Evidence, "slow movement safety evidence");
}

static void TargetRangeSingleBossIsNotTrashContext()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(12, 0, 0), chosenSource: "Target range", packTargetCount: 1, playerPosition: new Vec3(0, 0, 0), playerSpeed: null, actors: [PartyActor(new Vec3(0, 0, 0))]),
        Frame(1, destination: new Vec3(12, 0, 0), chosenSource: "Target range", packTargetCount: 1, playerPosition: new Vec3(1, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(6, 0, 0))]),
        Frame(2, destination: new Vec3(12, 0, 0), chosenSource: "Target range", packTargetCount: 1, playerPosition: new Vec3(2, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(12, 0, 0))]),
        Frame(3.2f, destination: new Vec3(12, 0, 0), chosenSource: "Target range", packTargetCount: 1, playerPosition: new Vec3(3.2f, 0, 0), playerStepDistance: 1.2f, playerSpeed: 1, actors: [PartyActor(new Vec3(19.2f, 0, 0))]),
        Frame(4.2f, destination: new Vec3(12, 0, 0), chosenSource: "Target range", packTargetCount: 1, playerPosition: new Vec3(4.2f, 0, 0), playerStepDistance: 1, playerSpeed: 1, actors: [PartyActor(new Vec3(25.2f, 0, 0))]),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "slow-pack-follow");
}

static void HealthOutlierBossContextDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(19, 0, 0),
        chosenSource: "comfort",
        targetBaseId: 16212,
        targetObjectId: 4096,
        targetPosition: new Vec3(10, 0, 0),
        targetRadius: 10,
        actors:
        [
            EnemyActor("target", 4096, 16212, 26_000_000, 26_000_000, new Vec3(10, 0, 0), 10),
            EnemyActor("nearby", 5001, 16214, 70_000, 70_000, new Vec3(12, 0, 0), 1),
            EnemyActor("nearby", 5002, 16214, 70_000, 70_000, new Vec3(8, 0, 0), 1),
        ]);

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "arena-edge");
}

static void DataminingTrialDutyBossContextDetector()
{
    if (!HasFfxivDataminingCheckout())
    {
        throw new SkipTestException("FfxivDatamining checkout unavailable.");
    }

    var frame = Frame(
        0,
        destination: new Vec3(19, 0, 0),
        chosenSource: "comfort",
        contentFinderConditionId: 949,
        targetBaseId: 16212,
        targetObjectId: 4096,
        targetPosition: new Vec3(10, 0, 0),
        targetRadius: 10);

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "arena-edge");
}

static bool HasFfxivDataminingCheckout()
{
    var configured = Environment.GetEnvironmentVariable("FFXIV_DATAMINING_PATH");
    if (!string.IsNullOrWhiteSpace(configured) && HasContentFinderCondition(configured))
    {
        return true;
    }

    var referencesDir = Environment.GetEnvironmentVariable("XCAI_REFERENCES_DIR");
    if (!string.IsNullOrWhiteSpace(referencesDir) &&
        HasContentFinderCondition(Path.Combine(referencesDir, "FfxivDatamining")))
    {
        return true;
    }

    var workspaceDir = Environment.GetEnvironmentVariable("XELS_WORKSPACE_DIR");
    if (!string.IsNullOrWhiteSpace(workspaceDir) &&
        HasContentFinderCondition(Path.Combine(workspaceDir, "XelsCombatAIReferences", "FfxivDatamining")))
    {
        return true;
    }

    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(start);
        while (directory != null)
        {
            if (HasContentFinderCondition(Path.Combine(directory.FullName, "..", "XelsCombatAIReferences", "FfxivDatamining")))
            {
                return true;
            }

            directory = directory.Parent;
        }
    }

    return false;
}

static bool HasContentFinderCondition(string path)
    => File.Exists(Path.Combine(path, "csv", "en", "ContentFinderCondition.csv")) ||
       File.Exists(Path.Combine(path, "ContentFinderCondition.csv"));

static void InferredBossMovementHintIsNotBmrPressure()
{
    var actors = new[]
    {
        EnemyActor("target", 4096, 16212, 26_000_000, 26_000_000, new Vec3(10, 0, 0), 10),
        EnemyActor("nearby", 5001, 16214, 70_000, 70_000, new Vec3(12, 0, 0), 1),
    };
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", bmrMoveRequested: true, bmrMoveImminent: true, targetSurfaceDistance: 10, engagementRange: 3, targetBaseId: 16212, targetObjectId: 4096, actors: actors),
        Frame(1, chosenSource: "<none>", bmrMoveRequested: true, bmrMoveImminent: true, targetSurfaceDistance: 10, engagementRange: 3, targetBaseId: 16212, targetObjectId: 4096, actors: actors),
        Frame(2.2f, chosenSource: "<none>", bmrMoveRequested: true, bmrMoveImminent: true, targetSurfaceDistance: 10, engagementRange: 3, targetBaseId: 16212, targetObjectId: 4096, actors: actors),
        Frame(3.1f, chosenSource: "<none>", bmrMoveRequested: true, bmrMoveImminent: true, targetSurfaceDistance: 10, engagementRange: 3, targetBaseId: 16212, targetObjectId: 4096, actors: actors),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "range-failure");
    AssertContains("BMR safety pressure was not active", incident.Evidence, "inferred boss BMR pressure context");
}

static void MovementJitterDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(0, 0, 5), chosenSource: "comfort"),
        Frame(0.4f, destination: new Vec3(5, 0, 0), chosenSource: "comfort"),
        Frame(0.8f, destination: new Vec3(-5, 0, 0), chosenSource: "comfort"),
        Frame(1.2f, destination: new Vec3(5, 0, 0), chosenSource: "comfort"),
        Frame(1.6f, destination: new Vec3(-5, 0, 0), chosenSource: "comfort"),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "movement-jitter");
}

static void BmrConflictDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(1, 0, 1),
        chosenSource: "comfort",
        bmrForbiddenZones: 1,
        bmrMoveRequested: true);

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "bmr-conflict");
}

static void RangeFailureDetector()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
        Frame(1, chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2.2f, chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
        Frame(3.1f, chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "range-failure");
}

static void RangeFailureIgnoresDisabledRangeWithinRoleRange()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", targetSurfaceDistance: 1.1f, engagementRange: -1, playerClassJobId: 40),
        Frame(1, chosenSource: "<none>", targetSurfaceDistance: 1.1f, engagementRange: -1, playerClassJobId: 40),
        Frame(2.2f, chosenSource: "<none>", targetSurfaceDistance: 1.1f, engagementRange: -1, playerClassJobId: 40),
        Frame(3.1f, chosenSource: "<none>", targetSurfaceDistance: 1.1f, engagementRange: -1, playerClassJobId: 40),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "range-failure");
}

static void RangeFailureUsesRoleRangeWhenPresetRangeMissing()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", targetSurfaceDistance: 28f, engagementRange: -1, playerClassJobId: 40),
        Frame(1, chosenSource: "<none>", targetSurfaceDistance: 28f, engagementRange: -1, playerClassJobId: 40),
        Frame(2.2f, chosenSource: "<none>", targetSurfaceDistance: 28f, engagementRange: -1, playerClassJobId: 40),
        Frame(3.1f, chosenSource: "<none>", targetSurfaceDistance: 28f, engagementRange: -1, playerClassJobId: 40),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "range-failure");
}

static void RangeFailureIncludesBmrPressureContext()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", chosenSource: "<none>", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(1, activeModule: "Boss", chosenSource: "<none>", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2.2f, activeModule: "Boss", chosenSource: "<none>", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(3.1f, activeModule: "Boss", chosenSource: "<none>", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "range-failure");
    AssertContains("BMR safety pressure", incident.Evidence, "BMR pressure context");
}

static void RangeFailureIgnoresAllPressureGreedLinger()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(1, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2.2f, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(3.1f, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
    };

    AssertNoIncident(IncidentDetector.Detect(LogWithCombatStyle("Greed", frames)), "range-failure");
}

static void RangeFailureKeepsGreedRecoveryFailures()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(1, activeModule: "Boss", chosenSource: "<none>", bmrForbiddenZones: 1, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2.2f, activeModule: "Boss", chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
        Frame(3.1f, activeModule: "Boss", chosenSource: "<none>", targetSurfaceDistance: 10, engagementRange: 3),
    };

    var incident = IncidentDetector.Detect(LogWithCombatStyle("Greed", frames)).Single(i => i.Category == "range-failure");
    AssertContains("Greed combat style", incident.Evidence, "Greed pressure context");
    AssertContains("recover range", incident.SuggestedGoal, "Greed recovery goal");
}

static void RangeFailureIgnoresManualSuppression()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", suppressionReason: "ManualMovementSuppressed", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(1, chosenSource: "<none>", suppressionReason: "ManualMovementSuppressed", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(2.2f, chosenSource: "<none>", suppressionReason: "ManualMovementSuppressed", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
        Frame(3.1f, chosenSource: "<none>", suppressionReason: "ManualMovementSuppressed", bmrMoveRequested: true, targetSurfaceDistance: 10, engagementRange: 3),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "range-failure");
}

static void LowValueGapCloserDetector()
{
    var frame = Frame(
        0,
        gapCloser: GapCloser(charges: 1),
        mobility: MobilityUsed(moveDistance: 2.2f, safetyGain: 0.1f, uptimeGain: 0.2f, pathGain: 0.1f));

    var incident = IncidentDetector.Detect(Log(frame)).Single(i => i.Category == "gap-close-low-value");
    AssertContains("low gains", incident.Evidence, "low value gap evidence");
}

static void GapCloserMissedCurrentGcdDetector()
{
    var frames = new[]
    {
        Frame(
            0,
            targetSurfaceDistance: 12f,
            engagementRange: 3f,
            gapCloser: GapCloser(charges: 0),
            nextGcd: NextGcd(gcdRemaining: 0.4f),
            mobility: MobilityUsed(moveDistance: 10f, uptimeGain: 7f)),
        Frame(0.5f, targetSurfaceDistance: 8f, engagementRange: 3f, gapCloser: GapCloser(charges: 0), nextGcd: NextGcd(gcdRemaining: 0f)),
        Frame(0.9f, targetSurfaceDistance: 2.5f, engagementRange: 3f, gapCloser: GapCloser(charges: 0), nextGcd: NextGcd(gcdRemaining: 0f)),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "gap-close-missed-current-gcd");
    AssertContains("current GCD", incident.Evidence, "missed current GCD evidence");
}

static void DepletedGapCloserOpportunityDetector()
{
    var frames = new[]
    {
        Frame(
            0,
            targetSurfaceDistance: 2f,
            engagementRange: 3f,
            gapCloser: GapCloser(charges: 0),
            mobility: MobilityUsed(moveDistance: 2.2f, safetyGain: 0.1f, uptimeGain: 0.1f, pathGain: 0.1f)),
        Frame(1.0f, targetSurfaceDistance: 14f, engagementRange: 3f, gapCloser: GapCloser(charges: 0), nextGcd: NextGcd(gcdRemaining: 0.5f)),
        Frame(1.6f, targetSurfaceDistance: 14f, engagementRange: 3f, gapCloser: GapCloser(charges: 0), nextGcd: NextGcd(gcdRemaining: 0.1f)),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "gap-close-depleted-opportunity");
    AssertContains("depleted", incident.Evidence, "depleted gap opportunity evidence");
}

static void CappedGapCloserOpportunityDetector()
{
    var frames = new[]
    {
        Frame(0, targetSurfaceDistance: 14f, engagementRange: 3f, gapCloser: GapCloser(charges: 2), nextGcd: NextGcd(gcdRemaining: 0.6f)),
        Frame(0.6f, targetSurfaceDistance: 14f, engagementRange: 3f, gapCloser: GapCloser(charges: 2), nextGcd: NextGcd(gcdRemaining: 0.2f)),
        Frame(1.2f, targetSurfaceDistance: 13f, engagementRange: 3f, gapCloser: GapCloser(charges: 2), nextGcd: NextGcd(gcdRemaining: 0.1f)),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "gap-close-capped-opportunity");
    AssertContains("stayed at 2 charges", incident.Evidence, "capped gap opportunity evidence");
}

static void TrashLatePackEngagementDetector()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "Target range", targetSurfaceDistance: 8, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.3f, chosenSource: "Target range", targetSurfaceDistance: 8, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.6f, chosenSource: "Target range", targetSurfaceDistance: 7, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "trash-pack-late-engage");
}

static void TrashLatePackEngagementIgnoresBmrSafety()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "Target range", bmrForbiddenZones: 1, targetSurfaceDistance: 8, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.3f, chosenSource: "Target range", bmrForbiddenZones: 1, targetSurfaceDistance: 8, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.6f, chosenSource: "Target range", bmrForbiddenZones: 1, targetSurfaceDistance: 7, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "trash-pack-late-engage");
}

static void TrashLatePackEngagementIgnoresInRangeSingleTarget()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "<none>", targetSurfaceDistance: 1, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.3f, chosenSource: "<none>", targetSurfaceDistance: 1, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
        Frame(0.6f, chosenSource: "<none>", targetSurfaceDistance: 1, engagementRange: 3, packTargetCount: 4, aoeReason: "RSR unsupported cast type 1 for Aeolian Edge"),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "trash-pack-late-engage");
}

static void MissedTankLeadDetector()
{
    var frame = Frame(
        0,
        chosenSource: "Pack engagement",
        packTargetCount: 4,
        trashPull: TrashPull(
            leadCandidateActive: true,
            behindDistance: 9,
            leadDestination: new Vec3(6, 0, 0)));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "missed-tank-lead");
}

static void MissedTankLeadIgnoresLegacyDirectGoal()
{
    var frame = Frame(
        0,
        chosenSource: "<none>",
        actionName: "Tank pull lead",
        aoeReason: "following tank lead; behind=9.0y; clamped behind tank",
        packTargetCount: 4,
        trashPull: TrashPull(
            leadCandidateActive: true,
            behindDistance: 9,
            leadDestination: new Vec3(6, 0, 0)));

    AssertNoIncident(IncidentDetector.Detect(Log(frame)), "missed-tank-lead");
}

static void TankLeadClampDetector()
{
    var frame = Frame(
        0,
        chosenSource: "Tank pull lead",
        packTargetCount: 4,
        trashPull: TrashPull(
            leadCandidateActive: true,
            leadClampApplied: true,
            behindDistance: 5,
            leadDestination: new Vec3(6, 0, 0)));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "tank-lead-clamped");
}

static void StragglerFocusDuringGatheringDetector()
{
    var frame = Frame(
        0,
        chosenSource: "Pack engagement",
        targetObjectId: 99,
        packTargetCount: 4,
        trashPull: TrashPull(stragglerTargetIds: [99]));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "straggler-focus-during-gathering");
}

static void TankLeadCornerFailureDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, 0),
        chosenSource: "Tank pull lead",
        packTargetCount: 4,
        pathStatus: "Reachable",
        pathDistance: 28,
        directDistance: 10,
        extraPathDistance: 18,
        pathDetourRatio: 2.8f,
        trashPull: TrashPull(leadCandidateActive: true, leadDestination: new Vec3(10, 0, 0)));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "tank-lead-corner-failure");
}

static void RouteMemoryChurnDetector()
{
    var frames = new[]
    {
        RouteMemoryFrame(0, new Vec3(3, 0, 1.5f)),
        RouteMemoryFrame(0.6f, new Vec3(6, 0, -1.5f)),
        RouteMemoryFrame(1.2f, new Vec3(4, 0, 1.5f)),
        RouteMemoryFrame(1.8f, new Vec3(7, 0, -1.5f)),
        RouteMemoryFrame(2.4f, new Vec3(5, 0, 1.5f))
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "route-memory-churn");
}

static void RouteMemoryBudgetDetector()
{
    var frame = Frame(
        0,
        chosenSource: "<none>",
        destination: null,
        packTargetCount: 4,
        trashPull: TrashPull(),
        generatedCount: 3,
        acceptedCount: 0,
        rejectedByReason: new Dictionary<string, int> { ["RouteBudgetSuppressed"] = 2, ["RouteBudgetExceeded"] = 1 },
        routeMemory: RouteMemory(localDestination: new Vec3(4, 0, 1.5f), queryBudgetUsed: 3, queryBudgetLimit: 3));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "route-memory-budget-exhausted");
}

static void RouteMemoryUnsafeRejectionDetector()
{
    var rejected = new CandidateSnapshot(
        "Trash route memory",
        "following tank trail",
        new Vec3(4, 0, 1.5f),
        false,
        "BmrPathBlocked",
        0,
        "Reachable",
        5,
        4,
        1,
        1.25f,
        2,
        0,
        new Vec3(2, 0, 1),
        2,
        0.5f,
        "BmrPathBlocked");
    var frame = Frame(
        0,
        chosenSource: "<none>",
        destination: null,
        packTargetCount: 4,
        trashPull: TrashPull(),
        generatedCount: 1,
        acceptedCount: 0,
        topCandidates: [rejected],
        routeMemory: RouteMemory(localDestination: new Vec3(4, 0, 1.5f), invalidationReason: "BmrPathBlocked"));

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "route-memory-unsafe-waypoint");
}

static void RouteMemoryFallbackDetector()
{
    var frames = new[]
    {
        RouteMemoryFrame(0, new Vec3(3, 0, 0), source: "vnav-fallback"),
        RouteMemoryFrame(0.7f, new Vec3(4, 0, 0), source: "vnav-fallback"),
        RouteMemoryFrame(1.4f, new Vec3(5, 0, 0), source: "vnav-fallback"),
        RouteMemoryFrame(2.1f, new Vec3(6, 0, 0), source: "vnav-fallback")
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "route-memory-vnav-fallback");
}

static void TrashAoeOpportunityDetector()
{
    var frames = new[]
    {
        Frame(0, chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(0.6f, chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(1.2f, chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
        Frame(1.8f, chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "trash-aoe-hit-opportunity");
}

static void BmrTrashModuleStaysTrashContext()
{
    var frames = new[]
    {
        Frame(0, activeModule: "D90StationSpecter", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(0.6f, activeModule: "D90StationSpecter", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(1.2f, activeModule: "D90StationSpecter", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
        Frame(1.8f, activeModule: "D90StationSpecter", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
    };

    var incidents = IncidentDetector.Detect(Log(frames));
    AssertHasIncident(incidents, "trash-aoe-hit-opportunity");
    AssertNoIncident(incidents, "arena-edge");
}

static void TrashAoeOpportunityIgnoresBossContext()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(0.6f, activeModule: "Boss", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 1, bestHits: 3, actionName: "Death Blossom"),
        Frame(1.2f, activeModule: "Boss", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
        Frame(1.8f, activeModule: "Boss", chosenSource: "AoE pack", packTargetCount: 4, currentHits: 2, bestHits: 4, actionName: "Death Blossom"),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "trash-aoe-hit-opportunity");
}

static void PersistentEdgeHuggingDetector()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20),
        Frame(2, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20),
        Frame(4, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20),
        Frame(6, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20),
        Frame(8.5f, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "edge-hugging");
}

static void EdgeHuggingSuppressesAllPressureBmrWindows()
{
    var frames = new[]
    {
        Frame(0, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20, bmrForbiddenZones: 1),
        Frame(2, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20, bmrForbiddenZones: 1),
        Frame(4, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20, bmrForbiddenZones: 1),
        Frame(6, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20, bmrForbiddenZones: 1),
        Frame(8.5f, activeModule: "Boss", playerPosition: new Vec3(0, 0, 19), pathfindRadius: 20, bmrForbiddenZones: 1),
    };

    AssertNoIncident(IncidentDetector.Detect(Log(frames)), "edge-hugging");
}

static void EdgeHuggingDetector()
{
    var frame = Frame(
        0,
        destination: new Vec3(19, 0, 0),
        chosenSource: "comfort",
        activeModule: "Boss",
        targetPosition: new Vec3(5, 0, 5),
        pathfindRadius: 20);

    AssertHasIncident(IncidentDetector.Detect(Log(frame)), "arena-edge");
}

static void TrashAwkwardDestinationsIgnored()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, -3),
        chosenSource: "Pack engagement",
        targetPosition: new Vec3(10, 0, 0),
        targetRadius: 10,
        packTargetCount: 3);

    var incidents = IncidentDetector.Detect(Log(frame));
    AssertNoIncident(incidents, "frontal-position");
    AssertNoIncident(incidents, "boss-center");
}

static void BossCenterIgnoredAsStandalone()
{
    var frame = Frame(
        0,
        destination: new Vec3(10, 0, -3),
        chosenSource: "comfort",
        activeModule: "Boss",
        targetPosition: new Vec3(10, 0, 0),
        targetRadius: 10);

    var incidents = IncidentDetector.Detect(Log(frame));
    AssertNoIncident(incidents, "frontal-position");
    AssertNoIncident(incidents, "boss-center");
}

static void TrashDefensiveZoneRequiresObjectiveFailure()
{
    var noFailure = Frame(
        0,
        destination: new Vec3(10.1f, 0, 0.1f),
        chosenSource: "Defensive zone",
        targetPosition: new Vec3(10, 0, 0),
        targetRadius: 5,
        packTargetCount: 3);

    AssertNoIncident(IncidentDetector.Detect(Log(noFailure)), "defensive-zone-overcommit");

    var stuck = new[]
    {
        Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", playerStepDistance: 0, playerSpeed: 0, packTargetCount: 3),
        Frame(0.6f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", playerStepDistance: 0, playerSpeed: 0, packTargetCount: 3),
        Frame(1.2f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", playerStepDistance: 0, playerSpeed: 0, packTargetCount: 3),
        Frame(1.8f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", playerStepDistance: 0, playerSpeed: 0, packTargetCount: 3),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(stuck)), "defensive-zone-overcommit");
}

static void DefensiveZoneOvercommitDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", activeModule: "Boss", playerStepDistance: 0, playerSpeed: 0),
        Frame(0.6f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", activeModule: "Boss", playerStepDistance: 0, playerSpeed: 0),
        Frame(1.2f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", activeModule: "Boss", playerStepDistance: 0, playerSpeed: 0),
        Frame(1.8f, destination: new Vec3(8, 0, 0), chosenSource: "Defensive zone", activeModule: "Boss", playerStepDistance: 0, playerSpeed: 0),
    };

    AssertHasIncident(IncidentDetector.Detect(Log(frames)), "defensive-zone-overcommit");
}

static void ManualCorrectionDetector()
{
    var frames = new[]
    {
        Frame(0, destination: new Vec3(19, 0, 0), chosenSource: "Arena edge", playerPosition: new Vec3(18.5f, 0, 0), pathfindRadius: 20),
        Frame(0.8f, destination: new Vec3(19, 0, 0), chosenSource: "Arena edge", playerPosition: new Vec3(18.8f, 0, 0), pathfindRadius: 20),
        Frame(1.6f, destination: new Vec3(19, 0, 0), chosenSource: "Arena edge", playerPosition: new Vec3(19f, 0, 0), pathfindRadius: 20),
        Frame(2.4f, suppressionReason: "ManualMovementSuppressed", automatedMovementSuppressed: true, playerPosition: new Vec3(19f, 0, 0), pathfindRadius: 20),
    };

    var incident = IncidentDetector.Detect(Log(frames)).Single(i => i.Category == "manual-correction");
    AssertContains("arena boundary", incident.Evidence, "manual correction evidence");
}

static void ManualCorrectionGoldenFixture()
{
    var log = XcaiLogReader.Read(FixturePath("manual-correction.jsonl"));
    var incident = IncidentDetector.Detect(log).Single(i => i.Category == "manual-correction");
    AssertContains("last active planner source was Arena edge", incident.Evidence, "manual correction fixture source");
}

static void ManualCorrectionFeedbackLowersAdvisoryGoals()
{
    var feedback = new ManualCorrectionFeedback();
    var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var movement = BossModMovementDiagnostics.Empty with { NavigationDestinationPosition = new Vector2(1, 0) };
    feedback.RecordManualMovement(new Vector3(0, 0, 0), "Arena edge", movement, now);

    Func<Vector2, float> score = _ => 1f;
    var advisory = new BossModGoalContribution(score, BossModGoalPriority.Convenience, "Arena edge", AdvisoryWeight: 1f);
    var raw = new BossModGoalContribution(score, BossModGoalPriority.DefensiveMechanic, "Mechanic exit margin", ScoreMode: BossModGoalScoreMode.Raw, AdvisoryWeight: 1f);
    var adjusted = feedback.Apply([advisory, raw], new Vector3(0, 0, 0), now);

    AssertTrue(adjusted[0].AdvisoryWeight is > 0f and < 1f, "manual feedback lowers advisory weight");
    AssertEqual(1f, adjusted[1].AdvisoryWeight, "manual feedback preserves raw mechanic weight");
}

static XcaiLog Log(params XcaiFrame[] frames)
{
    return LogWithCombatStyle("Standard", frames);
}

static XcaiLog LogWithCombatStyle(string combatStyle, params XcaiFrame[] frames)
{
    var first = frames.FirstOrDefault();
    return new XcaiLog(
        "incident-fixture.jsonl",
        new XcaiHeader(
            "combat",
            "tests",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc),
            10,
            frames.Length,
            first?.PlayerClassJobId ?? 38,
            1234,
            first?.ContentFinderConditionId ?? 0,
            "<none>",
            "<none>",
            combatStyle,
            EmptyJson()),
        frames);
}

static BmrEncounterSummary Encounter(int index, ulong instanceId, uint oid, ushort zone, DateTime start, DateTime end)
{
    return new BmrEncounterSummary(
        index,
        instanceId,
        oid,
        zone,
        start,
        end,
        0,
        0,
        [],
        [],
        [new BmrEncounterParticipantSummary(instanceId, oid, oid == 0 ? $"Actor-{instanceId:X}" : $"Enemy-{oid}", false)]);
}

static XcaiFrame Frame(
    float t,
    Vec3? destination = null,
    string chosenSource = "<none>",
    string suppressionReason = "none",
    int bmrForbiddenZones = 0,
    bool bmrMoveRequested = false,
    bool bmrMoveImminent = false,
    float? targetSurfaceDistance = null,
    float engagementRange = 3,
    string targetUptimeRangeSource = "none",
    string targetUptimeRangeReason = "not logged",
    bool automatedMovementSuppressed = false,
    string manualMovementInput = "available",
    Vec3? targetPosition = null,
    float targetRadius = 2,
    float pathfindRadius = 20,
    Vec3? playerPosition = null,
    float? playerStepDistance = null,
    float? playerSpeed = null,
    string activeModule = "<none>",
    uint contentFinderConditionId = 0,
    uint targetBaseId = 19045,
    ulong targetObjectId = 4096,
    int packTargetCount = 0,
    int currentHits = 0,
    int bestHits = 0,
    string actionName = "<none>",
    string actionSource = "none",
    string actionShape = "<none>",
    string positionalIntentSource = "none",
    string trueNorthDecisionSource = "none",
    string trueNorthDecisionReason = "not logged",
    string redMageNextActionSource = "none",
    string redMageNextActionName = "<none>",
    uint redMageNextActionId = 0,
    GapCloserSnapshot? gapCloser = null,
    NextGcdSnapshot? nextGcd = null,
    MobilitySnapshot? mobility = null,
    string aoeReason = "<none>",
    TrashPullSnapshot? trashPull = null,
    string pathStatus = "Ready",
    float? pathDistance = null,
    float? directDistance = null,
    float? extraPathDistance = null,
    float? pathDetourRatio = null,
    int? pathWaypointCount = null,
    double? pathCacheAgeMilliseconds = null,
    Vec3? firstWaypoint = null,
    float? firstWaypointDistance = null,
    float? firstWaypointYawDelta = null,
    int? generatedCount = null,
    int? acceptedCount = null,
    IReadOnlyDictionary<string, int>? rejectedByReason = null,
    IReadOnlyList<CandidateSnapshot>? topCandidates = null,
    VnavmeshRuntimeSnapshot? vnavmesh = null,
    string vnavmeshProbeSource = "<none>",
    VnavmeshPointSnapshot? vnavmeshDestination = null,
    SafetyRasterSnapshot? safetyRaster = null,
    RouteMemorySnapshot? routeMemory = null,
    IReadOnlyList<ActorSnapshot>? actors = null,
    uint playerClassJobId = 38)
{
    targetPosition ??= new Vec3(10, 0, 0);
    playerPosition ??= new Vec3(0, 0, 0);
    if (destination != null)
    {
        directDistance ??= Vec3.Distance2D(playerPosition, destination);
    }

    return new XcaiFrame(
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t),
        t,
        true,
        false,
        playerClassJobId,
        1234,
        contentFinderConditionId,
        playerPosition,
        0,
        targetBaseId,
        targetObjectId,
        targetPosition,
        MathF.PI,
        targetRadius,
        engagementRange,
        targetUptimeRangeSource,
        targetUptimeRangeReason,
        automatedMovementSuppressed,
        manualMovementInput,
        FacingSnapshot.Empty,
        new RedMageMeleeSnapshot(false, "inactive", "not logged", redMageNextActionName, redMageNextActionSource, redMageNextActionId, 0),
        activeModule,
        "<none>",
        aoeReason,
        trashPull ?? TrashPullSnapshot.Empty,
        new PlannerSnapshot(
            "test",
            chosenSource,
            destination,
            destination == null ? null : 0.5f,
            "test",
            suppressionReason,
            generatedCount ?? (destination == null ? 0 : 1),
            acceptedCount ?? (destination == null ? 0 : 1),
            rejectedByReason ?? new Dictionary<string, int>(),
            topCandidates ?? [],
            "test",
            pathStatus,
            pathDistance,
            directDistance,
            extraPathDistance,
            pathDetourRatio,
            pathWaypointCount,
            pathCacheAgeMilliseconds,
            firstWaypoint,
            firstWaypointDistance,
            firstWaypointYawDelta,
            vnavmesh ?? new VnavmeshRuntimeSnapshot(true, true, false, 0),
            vnavmeshProbeSource,
            vnavmeshDestination,
            LineOfSightSnapshot.NotLogged,
            null,
            bmrForbiddenZones,
            bmrMoveRequested,
            bmrMoveImminent,
            routeMemory ?? RouteMemorySnapshot.Empty),
        new BossModSnapshot(
            "<none>",
            "none",
            "not logged",
            "not logged",
            new Vec3(0, 0, 0),
            pathfindRadius,
            null,
            null,
            0,
            bmrForbiddenZones,
            "<none>",
            safetyRaster ?? SafetyRasterSnapshot.Unavailable("test fixture")),
        gapCloser ?? GapCloserSnapshot.Empty,
        nextGcd ?? NextGcdSnapshot.Empty,
        mobility ?? MobilitySnapshot.Empty,
        new MotionSnapshot(playerStepDistance, playerSpeed, null, targetSurfaceDistance),
        positionalIntentSource,
        trueNorthDecisionSource,
        trueNorthDecisionReason,
        packTargetCount,
        currentHits,
        bestHits,
        actionName,
        actionSource,
        actionShape,
        actors ?? [],
        EmptyJson());
}

static SafetyRasterSnapshot Raster(
    string player = "safe",
    string destination = "safe",
    string firstWaypoint = "safe",
    string target = "safe")
{
    return new SafetyRasterSnapshot(
        "captured",
        "test",
        new Vec3(0, 0, 0),
        0,
        0.5f,
        4,
        4,
        1,
        4,
        4,
        0,
        0,
        "rle-v1",
        SafetyRasterCodec.Encode([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        SafetyPoint(player),
        SafetyPoint(destination),
        SafetyPoint(firstWaypoint),
        SafetyPoint(target));
}

static SafetyRasterSnapshot RasterCells(
    int width,
    int height,
    IReadOnlyList<int> cells,
    float sourceResolution = 0.5f,
    int cellScale = 1,
    Vec3? center = null)
{
    return new SafetyRasterSnapshot(
        "captured",
        "test",
        center ?? new Vec3(0, 0, 0),
        0,
        sourceResolution,
        width * cellScale,
        height * cellScale,
        cellScale,
        width,
        height,
        0,
        0,
        "rle-v1",
        SafetyRasterCodec.Encode(cells),
        SafetyPoint("safe"),
        SafetyPoint("safe"),
        SafetyPoint("safe"),
        SafetyPoint("safe"));
}

static TrashPullSnapshot TrashPull(
    string phase = "Gathering",
    float confidence = 0.8f,
    bool leadCandidateActive = false,
    bool leadClampApplied = false,
    float? behindDistance = 8,
    Vec3? leadDestination = null,
    IReadOnlyList<ulong>? dominantTargetIds = null,
    IReadOnlyList<ulong>? stragglerTargetIds = null)
{
    return new TrashPullSnapshot(
        phase,
        confidence,
        "tank dragging trash",
        10,
        new Vec3(8, 0, 0),
        new Vec3(4, 0, 0),
        4,
        new Vec3(10, 0, 0),
        leadDestination,
        leadCandidateActive,
        leadClampApplied,
        behindDistance,
        new Vec3(9, 0, 0),
        new Vec3(2, 0, 0),
        2,
        5,
        dominantTargetIds?.Count ?? 3,
        stragglerTargetIds?.Count ?? 0,
        dominantTargetIds ?? [1, 2, 3],
        stragglerTargetIds ?? [],
        leadClampApplied ? "clamped behind tank" : "<none>");
}

static AoePackPositioningStatus AoePackStatus(int priorityTargetCount = 0, TrashPullDiagnostics? trashPull = null, bool bossModuleContext = false, bool trashContext = false)
{
    return new AoePackPositioningStatus(
        "test",
        "test",
        "test",
        "test",
        0,
        "<none>",
        "<none>",
        "<none>",
        0,
        0,
        false,
        false,
        StateCommandType.Off,
        "test",
        priorityTargetCount,
        bossModuleContext,
        trashContext,
        null,
        null,
        false,
        trashPull ?? TrashPullDiagnostics.Empty);
}

static TrashPullDiagnostics TrashDiagnostics(TrashPullPhase phase = TrashPullPhase.Gathering, int dominantTargetCount = 3)
{
    return TrashPullDiagnostics.Empty with
    {
        Phase = phase,
        DominantTargetCount = dominantTargetCount
    };
}

static XcaiFrame RouteMemoryFrame(float t, Vec3 localDestination, string source = "tank-trail")
{
    return Frame(
        t,
        chosenSource: "Trash route memory",
        destination: localDestination,
        packTargetCount: 4,
        trashPull: TrashPull(),
        routeMemory: RouteMemory(localDestination: localDestination, source: source));
}

static GapCloserSnapshot GapCloser(uint charges, uint actionId = ActionUse.SamuraiGyotenActionId, string actionName = "Gyoten")
{
    return new GapCloserSnapshot(true, actionId, actionName, charges);
}

static NextGcdSnapshot NextGcd(float gcdRemaining, string actionName = "Hakaze")
{
    return new NextGcdSnapshot(0, 0, actionName, "test", 4096, gcdRemaining, 2.5f - gcdRemaining, 2.5f, 0.35f);
}

static MobilitySnapshot MobilityUsed(
    float moveDistance,
    float safetyGain = 0,
    float uptimeGain = 0,
    float pathGain = 0,
    uint actionId = ActionUse.SamuraiGyotenActionId,
    string actionName = "Gyoten")
{
    return new MobilitySnapshot(
        "Used",
        "Uptime",
        "uptime",
        actionName,
        actionId,
        new Vec3(8, 0, 0),
        moveDistance,
        safetyGain,
        uptimeGain,
        pathGain,
        "local",
        "safe landing",
        "uptime gain",
        "path gain",
        "used");
}

static RouteMemorySnapshot RouteMemory(
    bool active = true,
    string source = "tank-trail",
    Vec3? localDestination = null,
    int queryBudgetUsed = 1,
    int queryBudgetLimit = 3,
    string invalidationReason = "<none>")
{
    localDestination ??= new Vec3(4, 0, 1.5f);
    return new RouteMemorySnapshot(
        active,
        active ? "active" : "inactive",
        source,
        source == "vnav-fallback" ? "tank trail unavailable; using bounded vnav fallback" : "following tank trail with stable lateral offset",
        new Vec3(6, 0, 0),
        localDestination,
        new Vec3(2, 0, 0),
        1,
        1.8f,
        750,
        1,
        3,
        "Reachable",
        queryBudgetUsed,
        queryBudgetLimit,
        invalidationReason,
        [new Vec3(0, 0, 0), new Vec3(3, 0, 0), new Vec3(6, 0, 0)]);
}

static SafetyPointSnapshot SafetyPoint(string state)
{
    return new SafetyPointSnapshot(state, new Vec3(0, 0, 0), 1, 1, null, null);
}

static ActorSnapshot PartyActor(Vec3 position)
{
    return new ActorSnapshot(
        "party",
        10,
        10,
        0,
        "Player",
        1,
        19,
        100,
        position,
        0,
        0.5f,
        true,
        false,
        true,
        1000,
        1000,
        0,
        0);
}

static ActorSnapshot EnemyActor(string relation, ulong gameObjectId, uint baseId, uint currentHp, uint maxHp, Vec3 position, float radius)
{
    return new ActorSnapshot(
        relation,
        gameObjectId,
        (uint)gameObjectId,
        baseId,
        "BattleNpc",
        5,
        0,
        100,
        position,
        0,
        radius,
        true,
        false,
        true,
        currentHp,
        maxHp,
        0,
        0);
}

static TrashPullObservation TrashObservation(DateTime now, Vector3 player, Vector3 tank, float pack, bool bmrSafety = false, bool manual = false)
{
    var targets = new[]
    {
        new TargetSnapshot(1, new Vector2(pack, 0), 1),
        new TargetSnapshot(2, new Vector2(pack + 1, 1), 1),
        new TargetSnapshot(3, new Vector2(pack - 1, -1), 1),
    };
    return new TrashPullObservation(
        now,
        InCombat: true,
        TrashContext: true,
        BossContext: false,
        ManualSuppressed: manual,
        BmrSafetyPressure: bmrSafety,
        PlayerPosition: player,
        PackAoeRange: 5,
        Tank: new TrashPullActorPosition(10, tank),
        PartyMembers: [new TrashPullActorPosition(10, tank), new TrashPullActorPosition(11, tank - new Vector3(1, 0, 0))],
        DominantTargets: targets,
        AllTargets: targets);
}

static JsonElement EmptyJson()
{
    using var document = JsonDocument.Parse("{}");
    return document.RootElement.Clone();
}

static string FixturePath(string name)
{
    return Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

static void AssertHasIncident(IReadOnlyList<Incident> incidents, string category)
{
    if (!incidents.Any(i => i.Category == category))
    {
        throw new InvalidOperationException($"Expected incident '{category}', got: {string.Join(", ", incidents.Select(i => i.Category))}");
    }
}

static void AssertNoIncident(IReadOnlyList<Incident> incidents, string category)
{
    if (incidents.Any(i => i.Category == category))
    {
        throw new InvalidOperationException($"Did not expect incident '{category}'.");
    }
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
}

static void AssertContains(string expected, string actual, string name)
{
    if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{name}: expected '{actual}' to contain '{expected}'.");
    }
}

static void AssertEqual<T>(T expected, T actual, string name)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void AssertApproximately(float expected, float actual, float tolerance, string name)
{
    if (MathF.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"{name}: expected {expected:0.###}, got {actual:0.###}.");
    }
}

static void AssertTrue(bool value, string name)
{
    if (!value)
    {
        throw new InvalidOperationException($"{name}: expected true.");
    }
}

static void AssertFalse(bool value, string name)
{
    if (value)
    {
        throw new InvalidOperationException($"{name}: expected false.");
    }
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path)
    {
        this.Path = path;
    }

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xcai-fight-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.Path))
        {
            Directory.Delete(this.Path, recursive: true);
        }
    }
}

internal sealed class SkipTestException(string message) : Exception(message);
