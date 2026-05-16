using System.Globalization;
using System.Numerics;
using System.Text.Json;
using FightReview;
using XelsCombatAI.Combat;
using XelsCombatAI.Config;
using XelsCombatAI.Game;
using XelsCombatAI.Integrations;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
BossMod.Service.LogHandlerDebug = message => Console.Error.WriteLine("BMR: " + message);

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
    ("configuration tank lead defaults/reset", ConfigurationTankLeadDefaultsAndReset),
    ("friendly anchor dash requires meaningful gain", FriendlyAnchorDashRequiresMeaningfulGain),
    ("gap closer follows RSR auto target", GapCloserFollowsRsrAutoTarget),
    ("hostile relay dash requires target momentum", HostileRelayDashRequiresTargetMomentum),
    ("healer coverage restores single missing member", HealerCoverageRestoresSingleMissingMember),
    ("healer coverage uses stable natural centers", HealerCoverageUsesStableNaturalCenters),
    ("healer coverage catches up for party-saving AoE heals", HealerCoverageCatchesUpForPartySavingAoeHeals),
    ("healer coverage catches up from critical party isolation", HealerCoverageCatchesUpFromCriticalPartyIsolation),
    ("healer coverage combines with forbidden zones", HealerCoverageCombinesWithForbiddenZones),
    ("pack movement combines with forbidden zones", PackMovementCombinesWithForbiddenZones),
    ("multi-target large trash remains trash context", MultiTargetLargeTrashRemainsTrashContext),
    ("trash pull tracker phase transitions", TrashPullTrackerPhaseTransitions),
    ("trash pull tracker remote settled pack remains catch-up", TrashPullTrackerRemoteSettledPackRemainsCatchUp),
    ("trash pull tracker tank lead only when behind", TrashPullTrackerTankLeadOnlyWhenBehind),
    ("trash pull tracker never leads beyond tank", TrashPullTrackerNeverLeadsBeyondTank),
    ("trash pull tracker disrupted suppresses lead", TrashPullTrackerDisruptedSuppressesLead),
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
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
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
    AssertTrue(analysis.NegativeSignals.Any(signal => signal.Category == "melee-comfort-loss"), "melee comfort loss signal");
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
{"Type":"frame","Frame":{"TimestampUtc":"2026-01-01T00:00:00.0000000Z","T":0,"InCombat":true,"IsDead":false,"PlayerClassJobId":25,"TerritoryType":1234,"ContentFinderConditionId":0,"PlayerPosition":{"X":84,"Y":0,"Z":388},"PlayerRotation":0,"TargetBaseId":1,"TargetObjectId":1,"TargetPosition":{"X":84,"Y":0,"Z":370},"TargetRotation":0,"TargetRadius":4.5,"TargetUptimeRange":25,"BossModActiveModule":"Boss","BossModActiveZoneModule":"<none>","MovementPlanner":{"IntentId":"<none>","ChosenSource":"<none>","Destination":null,"AcceptanceRadius":null,"SwitchReason":"none","SuppressionReason":"none","GeneratedCount":0,"AcceptedCount":0,"RejectedByReason":{},"TopCandidates":[],"ScoreBreakdown":"<none>","PathStatus":"None","BmrForcedMovement":null,"BmrForbiddenZones":0,"BmrMoveRequested":false,"BmrMoveImminent":false},"BossModMovement":{"MovementOverride":"<none>","HintSummary":"none","HintDetails":{"PathfindMapCenter":{"X":84,"Y":370},"PathfindMapBounds":{"Radius":19.5,"HalfWidth":19.5,"HalfHeight":19.5},"GoalZones":0,"ForbiddenZones":0,"ImminentSpecialMode":"<none>"}},"Actors":[]},"Motion":{"TargetDistance":18,"TargetSurfaceDistance":13.5}}
""");

    var log = XcaiLogReader.Read(path);
    AssertEqual(new Vec3(84, 0, 370), log.Frames[0].BossMod.PathfindMapCenter!, "2D center");
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

    config.FightReviewLoggingEnabled = true;
    config.ResetAll();
    AssertFalse(config.FightReviewLoggingEnabled, "reset disables logging");

    config.Version = 16;
    config.FightReviewLoggingEnabled = true;
    config.Migrate();
    AssertEqual(currentVersion, config.Version, "migrated version");
    AssertFalse(config.FightReviewLoggingEnabled, "migration disables logging");
    AssertTrue(config.ManageSocialTurning, "migration enables social turning");
}

static void ConfigurationTankLeadDefaultsAndReset()
{
    var config = new Configuration();
    var currentVersion = config.Version;
    AssertTrue(config.LeadTrashPullsWithTank, "default tank lead enabled");

    config.LeadTrashPullsWithTank = false;
    config.ResetBehaviorSettings();
    AssertTrue(config.LeadTrashPullsWithTank, "behavior reset enables tank lead");

    config.LeadTrashPullsWithTank = false;
    config.ResetAll();
    AssertTrue(config.LeadTrashPullsWithTank, "full reset enables tank lead");

    config.Version = 16;
    config.LeadTrashPullsWithTank = false;
    config.Migrate();
    AssertEqual(currentVersion, config.Version, "tank lead migrated version");
    AssertTrue(config.LeadTrashPullsWithTank, "migration enables tank lead");
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

static void HostileRelayDashRequiresTargetMomentum()
{
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

static void HealerCoverageRestoresSingleMissingMember()
{
    AssertTrue(
        HealerAoePositioningController.ShouldRestoreSingleMissingCoverage(
            currentCoveredCount: 6,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 5.5f),
        "short move to restore full healer coverage should be worthwhile");

    AssertFalse(
        HealerAoePositioningController.ShouldRestoreSingleMissingCoverage(
            currentCoveredCount: 5,
            bestCoveredCount: 6,
            totalMembers: 7,
            distanceToCenter: 3f),
        "single-member partial coverage gain should remain a convenience skip");

    AssertFalse(
        HealerAoePositioningController.ShouldRestoreSingleMissingCoverage(
            currentCoveredCount: 6,
            bestCoveredCount: 7,
            totalMembers: 7,
            distanceToCenter: 8f),
        "full coverage restore should stay distance bounded");
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
        Vector2.Distance(center, new Vector2(100f / 3f, 0f)) < 0.01f,
        "coverage center should prefer the covered group centroid over standing on a party member");

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

static void HealerCoverageCatchesUpForPartySavingAoeHeals()
{
    var action = new RsrAoeActionSnapshot(
        ActionId: 0,
        AdjustedActionId: 0,
        ActionName: "Eukrasian Prognosis II",
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

static void HealerCoverageCombinesWithForbiddenZones()
{
    AssertFalse(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            bmrMoveRequested: false,
            bmrMoveImminent: false),
        "healer coverage should be allowed to bias safe mechanic gaps");

    AssertFalse(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            bmrMoveRequested: true,
            bmrMoveImminent: true),
        "existing BMR movement should not suppress coverage scoring");

    AssertTrue(
        HealerAoePositioningController.ShouldYieldCoverageForSafety(
            forcedMovementActive: true,
            forbiddenSafetyActive: true,
            bmrMoveRequested: false,
            bmrMoveImminent: false),
        "forced mechanic movement remains authoritative");
}

static void PackMovementCombinesWithForbiddenZones()
{
    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: true,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: true),
        "pack movement should be allowed to bias safe mechanic gaps");

    AssertFalse(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: false,
            forbiddenSafetyActive: false,
            bmrMoveRequested: true,
            bmrMoveImminent: true,
            bossModuleContext: false),
        "existing BMR movement should not suppress pack scoring");

    AssertTrue(
        AoePackPositioningController.ShouldYieldPackMovementForSafety(
            forcedMovementActive: true,
            forbiddenSafetyActive: false,
            bmrMoveRequested: false,
            bmrMoveImminent: false,
            bossModuleContext: false),
        "forced mechanic movement remains authoritative");
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

    AssertTrue(
        AoePackPositioningController.ShouldUseBossModuleContext(
            bossModEncounterActive: true,
            targetHasBossModule: false,
            packLikeTrashContext: true,
            hitboxBossLikeContext: false,
            previousBossLikeCombatActive: false),
        "BossMod encounter context remains authoritative");

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
    AssertTrue(status.LeadCandidateActive, "remote settled pack emits tank lead");
}

static void TrashPullTrackerTankLeadOnlyWhenBehind()
{
    var tracker = WarmGatheringTracker(player: new Vector3(0, 0, 0), tank: new Vector3(10, 0, 0), pack: 8);
    AssertTrue(tracker.Current.LeadCandidateActive, "far behind lead active");

    tracker = WarmGatheringTracker(player: new Vector3(7.5f, 0, 0), tank: new Vector3(10, 0, 0), pack: 8);
    AssertFalse(tracker.Current.LeadCandidateActive, "close behind lead inactive");
}

static void TrashPullTrackerNeverLeadsBeyondTank()
{
    var tracker = WarmGatheringTracker(player: new Vector3(0, 0, 0), tank: new Vector3(12, 0, 0), pack: 11);
    var status = tracker.Current;
    AssertTrue(status.LeadCandidateActive, "lead active");
    AssertTrue(status.LeadDestination != null, "lead destination");
    var leadAhead = status.LeadDestination!.Value.X - status.TankPosition!.Value.X;
    AssertTrue(leadAhead <= -2f + 0.01f, "lead remains behind tank");
}

static void TrashPullTrackerDisruptedSuppressesLead()
{
    var tracker = WarmGatheringTracker(player: new Vector3(0, 0, 0), tank: new Vector3(10, 0, 0), pack: 8, bmrSafety: true);
    AssertTrue(tracker.Current.Phase == TrashPullPhase.Disrupted, "BMR disrupted phase");
    AssertFalse(tracker.Current.LeadCandidateActive, "BMR suppresses lead");

    tracker = WarmGatheringTracker(player: new Vector3(0, 0, 0), tank: new Vector3(10, 0, 0), pack: 8, manual: true);
    AssertTrue(tracker.Current.Phase == TrashPullPhase.Disrupted, "manual disrupted phase");
    AssertFalse(tracker.Current.LeadCandidateActive, "manual suppresses lead");
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
    if (!File.Exists(Path.Combine("external", "FfxivDatamining", "csv", "en", "ContentFinderCondition.csv")))
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
    string actionShape = "<none>",
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
        automatedMovementSuppressed,
        manualMovementInput,
        FacingSnapshot.Empty,
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
            new Vec3(0, 0, 0),
            pathfindRadius,
            null,
            null,
            0,
            bmrForbiddenZones,
            "<none>",
            safetyRaster ?? SafetyRasterSnapshot.Unavailable("test fixture")),
        MobilitySnapshot.Empty,
        new MotionSnapshot(playerStepDistance, playerSpeed, null, targetSurfaceDistance),
        packTargetCount,
        currentHits,
        bestHits,
        actionName,
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

static TrashPullStateTracker WarmGatheringTracker(Vector3 player, Vector3 tank, float pack, bool bmrSafety = false, bool manual = false)
{
    var tracker = new TrashPullStateTracker();
    var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    tracker.Update(TrashObservation(start, player, tank - new Vector3(4, 0, 0), pack - 2, bmrSafety, manual));
    tracker.Update(TrashObservation(start.AddMilliseconds(300), player, tank - new Vector3(2, 0, 0), pack - 1, bmrSafety, manual));
    tracker.Update(TrashObservation(start.AddMilliseconds(900), player, tank, pack, bmrSafety, manual));
    return tracker;
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
        EngagementRange: 3,
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
