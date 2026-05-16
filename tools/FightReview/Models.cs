using System.Text.Json;

namespace FightReview;

internal sealed record Vec3(float X, float Y, float Z)
{
    public static Vec3? FromJson(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty("X", out var x) &&
               element.TryGetProperty("Y", out var y) &&
               element.TryGetProperty("Z", out var z)
            ? new Vec3(x.GetSingle(), y.GetSingle(), z.GetSingle())
            : null;
    }

    public static float Distance2D(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}

internal sealed record XcaiLog(string Path, XcaiHeader Header, IReadOnlyList<XcaiFrame> Frames);

internal sealed record XcaiHeader(
    string LogScope,
    string PluginVersion,
    DateTime RunStartUtc,
    DateTime RunEndUtc,
    DateTime CombatStartUtc,
    DateTime CombatEndUtc,
    float DurationSeconds,
    int FrameCount,
    uint PlayerClassJobId,
    uint TerritoryType,
    uint ContentFinderConditionId,
    string BossModActiveModule,
    string BossModActiveZoneModule,
    string CombatStyle,
    JsonElement Raw);

internal sealed record XcaiFrame(
    DateTime TimestampUtc,
    float T,
    bool InCombat,
    bool IsDead,
    uint PlayerClassJobId,
    uint TerritoryType,
    uint ContentFinderConditionId,
    Vec3? PlayerPosition,
    float PlayerRotation,
    uint TargetBaseId,
    ulong TargetObjectId,
    Vec3? TargetPosition,
    float TargetRotation,
    float TargetRadius,
    float EngagementRange,
    bool AutomatedMovementSuppressed,
    string ManualMovementInput,
    FacingSnapshot Facing,
    string BossModActiveModule,
    string BossModActiveZoneModule,
    string AoeReason,
    TrashPullSnapshot TrashPull,
    PlannerSnapshot Planner,
    BossModSnapshot BossMod,
    MobilitySnapshot Mobility,
    MotionSnapshot Motion,
    int PackTargetCount,
    int CurrentHits,
    int BestHits,
    string ActionName,
    string ActionShape,
    IReadOnlyList<ActorSnapshot> Actors,
    JsonElement Raw);

internal sealed record FacingSnapshot(
    string Source,
    string Reason,
    float? DesiredRotation,
    float? CurrentRotation,
    float? DeltaRadians,
    bool Applied,
    string RejectionReason,
    int ConsensusMembers)
{
    public static FacingSnapshot Empty { get; } = new(
        "None",
        "not logged",
        null,
        null,
        null,
        false,
        "not logged",
        0);
}

internal sealed record TrashPullSnapshot(
    string Phase,
    float Confidence,
    string Reason,
    ulong TankObjectId,
    Vec3? TankPosition,
    Vec3? TankVelocity,
    float TankSpeed,
    Vec3? ProjectedTankPosition,
    Vec3? LeadDestination,
    bool LeadCandidateActive,
    bool LeadClampApplied,
    float? BehindDistance,
    Vec3? PackCentroid,
    Vec3? PackVelocity,
    float PackSpeed,
    float? PartyMedianSpeed,
    int DominantTargetCount,
    int StragglerTargetCount,
    IReadOnlyList<ulong> DominantTargetIds,
    IReadOnlyList<ulong> StragglerTargetIds,
    string LeadRejectionReason)
{
    public static TrashPullSnapshot Empty { get; } = new(
        "None",
        0f,
        "not logged",
        0,
        null,
        null,
        0f,
        null,
        null,
        false,
        false,
        null,
        null,
        null,
        0f,
        null,
        0,
        0,
        [],
        [],
        "<none>");
}

internal sealed record PlannerSnapshot(
    string IntentId,
    string ChosenSource,
    Vec3? Destination,
    float? AcceptanceRadius,
    string SwitchReason,
    string SuppressionReason,
    int GeneratedCount,
    int AcceptedCount,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyList<CandidateSnapshot> TopCandidates,
    string ScoreBreakdown,
    string PathStatus,
    float? PathDistance,
    float? DirectDistance,
    float? ExtraPathDistance,
    float? PathDetourRatio,
    int? PathWaypointCount,
    double? PathCacheAgeMilliseconds,
    Vec3? FirstWaypoint,
    float? FirstWaypointDistance,
    float? FirstWaypointYawDelta,
    VnavmeshRuntimeSnapshot Vnavmesh,
    string VnavmeshProbeSource,
    VnavmeshPointSnapshot? VnavmeshDestination,
    LineOfSightSnapshot LineOfSight,
    Vec3? BmrForcedMovement,
    int BmrForbiddenZones,
    bool BmrMoveRequested,
    bool BmrMoveImminent,
    RouteMemorySnapshot RouteMemory);

internal sealed record RouteMemorySnapshot(
    bool Active,
    string State,
    string Source,
    string Reason,
    Vec3? RouteGoal,
    Vec3? LocalDestination,
    Vec3? NextWaypoint,
    int OffsetSide,
    float OffsetDistance,
    double RouteAgeMilliseconds,
    int WaypointIndex,
    int WaypointCount,
    string VnavStatus,
    int QueryBudgetUsed,
    int QueryBudgetLimit,
    string InvalidationReason,
    IReadOnlyList<Vec3> TankTrail)
{
    public static RouteMemorySnapshot Empty { get; } = new(
        false,
        "inactive",
        "<none>",
        "not logged",
        null,
        null,
        null,
        0,
        0f,
        0d,
        0,
        0,
        "None",
        0,
        0,
        "<none>",
        []);
}

internal sealed record LineOfSightSnapshot(
    bool Checked,
    bool Blocked,
    bool CombatClear,
    bool NavigationClear,
    string Reason,
    Vec3? BlockedPoint,
    float? BlockedDistance)
{
    public static LineOfSightSnapshot NotLogged { get; } = new(false, false, true, true, "not logged", null, null);
}

internal sealed record CandidateSnapshot(
    string Source,
    string Reason,
    Vec3? Destination,
    bool Accepted,
    string RejectionReason,
    float TotalScore,
    string PathStatus,
    float? PathDistance,
    float? DirectDistance,
    float? ExtraPathDistance,
    float? PathDetourRatio,
    int? PathWaypointCount,
    double? PathCacheAgeMilliseconds,
    Vec3? FirstWaypoint,
    float? FirstWaypointDistance,
    float? FirstWaypointYawDelta,
    string ScoreBreakdown);

internal sealed record VnavmeshRuntimeSnapshot(
    bool Ready,
    bool? AutoLoad,
    bool? PathfindInProgress,
    int? PathfindQueued);

internal sealed record VnavmeshPointSnapshot(
    string Status,
    Vec3? NearestPoint,
    float? NearestPointDistance,
    Vec3? NearestReachablePoint,
    float? NearestReachablePointDistance,
    Vec3? FloorPoint,
    float? FloorPointDistance);

internal sealed record BossModSnapshot(
    string MovementOverride,
    string HintSummary,
    string PlannerSteer,
    string MechanicWhisper,
    Vec3? PathfindMapCenter,
    float? PathfindMapRadius,
    float? PathfindMapHalfWidth,
    float? PathfindMapHalfHeight,
    int? GoalZones,
    int? ForbiddenZones,
    string ImminentSpecialMode,
    SafetyRasterSnapshot SafetyRaster);

internal sealed record MobilitySnapshot(
    string State,
    string Intent,
    string IntentLabel,
    string ActionName,
    uint ActionId,
    Vec3? Destination,
    float MoveDistance,
    float SafetyGain,
    float UptimeGain,
    float PathGain,
    string SafetyReason,
    string UptimeReason,
    string PathReason,
    string RiskReason)
{
    public static MobilitySnapshot Empty { get; } = new(
        "NotChecked",
        "None",
        "none",
        "<none>",
        0,
        null,
        0f,
        0f,
        0f,
        0f,
        "not logged",
        "not logged",
        "not logged",
        "not logged");
}

internal sealed record SafetyRasterSnapshot(
    string Status,
    string Reason,
    Vec3? Center,
    float RotationRadians,
    float SourceResolution,
    int SourceWidth,
    int SourceHeight,
    int CellScale,
    int Width,
    int Height,
    float? MaxG,
    float? MaxPriority,
    string Encoding,
    string CellsRle,
    SafetyPointSnapshot Player,
    SafetyPointSnapshot Destination,
    SafetyPointSnapshot FirstWaypoint,
    SafetyPointSnapshot Target)
{
    public static SafetyRasterSnapshot Unavailable(string reason) => new(
        "unavailable",
        reason,
        null,
        0f,
        0f,
        0,
        0,
        1,
        0,
        0,
        null,
        null,
        "rle-v1",
        string.Empty,
        SafetyPointSnapshot.Empty,
        SafetyPointSnapshot.Empty,
        SafetyPointSnapshot.Empty,
        SafetyPointSnapshot.Empty);
}

internal sealed record SafetyPointSnapshot(
    string State,
    Vec3? Position,
    int? GridX,
    int? GridY,
    float? PixelMaxG,
    float? PixelPriority)
{
    public static SafetyPointSnapshot Empty { get; } = new("unknown", null, null, null, null, null);
}

internal sealed record MotionSnapshot(
    float? PlayerStepDistance,
    float? PlayerSpeed,
    float? TargetDistance,
    float? TargetSurfaceDistance);

internal sealed record ActorSnapshot(
    string Relation,
    ulong GameObjectId,
    uint EntityId,
    uint BaseId,
    string ObjectKind,
    byte SubKind,
    uint ClassJobId,
    byte Level,
    Vec3 Position,
    float Rotation,
    float Radius,
    bool IsTargetable,
    bool IsDead,
    bool InCombat,
    uint CurrentHp,
    uint MaxHp,
    ulong TargetObjectId,
    float DistanceToPlayer);

internal sealed record BmrReplayData(string Path, BossMod.Replay Replay, BmrSummary Summary);

internal sealed record BmrSummary(
    string Path,
    DateTime? Start,
    DateTime? End,
    int OperationCount,
    int EncounterCount,
    IReadOnlyList<BmrEncounterSummary> Encounters,
    IReadOnlyList<BmrParticipantSummary> Participants,
    IReadOnlyList<BmrEvent> Events);

internal sealed record BmrEncounterSummary(
    int Index,
    ulong InstanceId,
    uint Oid,
    ushort Zone,
    DateTime Start,
    DateTime End,
    int StateCount,
    int PhaseCount,
    IReadOnlyList<BmrEncounterStateSummary> States,
    IReadOnlyList<BmrEncounterPhaseSummary> Phases,
    IReadOnlyList<BmrEncounterParticipantSummary> Participants);

internal sealed record BmrEncounterStateSummary(
    uint Id,
    string Name,
    string Comment,
    float ExpectedDuration,
    DateTime Exit);

internal sealed record BmrEncounterPhaseSummary(
    int Id,
    uint LastStateId,
    DateTime Exit);

internal sealed record BmrEncounterParticipantSummary(
    ulong InstanceId,
    uint Oid,
    string Label,
    bool WasAlly);

internal sealed record BmrParticipantSummary(
    ulong InstanceId,
    uint Oid,
    string Label,
    string Type,
    uint ZoneId,
    uint Cfcid,
    DateTime FirstSeen,
    DateTime LastSeen,
    float MinRadius,
    float MaxRadius,
    bool WasAlly,
    bool HasAnyActions,
    bool HasAnyStatuses,
    bool IsTargetOfAnyActions,
    IReadOnlyList<BmrPositionSample> Positions);

internal sealed record BmrPositionSample(
    DateTime Timestamp,
    Vec3 Position,
    float Rotation);

internal sealed record BmrEvent(
    string Type,
    DateTime Timestamp,
    ulong SourceId,
    uint SourceOid,
    ulong? TargetId,
    uint? TargetOid,
    ulong ActionRaw,
    string Label);

internal sealed record MatchResult(
    string BmrPath,
    double Confidence,
    IReadOnlyList<string> Evidence);

internal sealed record Incident(
    string Id,
    string Category,
    DateTime TimestampUtc,
    float T,
    string Severity,
    string Evidence,
    string SuggestedGoal,
    int StartFrame,
    int EndFrame);

internal sealed record ReviewBundle(
    XcaiLog Xcai,
    BmrSummary Bmr,
    MatchResult Match,
    IReadOnlyList<Incident> Incidents);
