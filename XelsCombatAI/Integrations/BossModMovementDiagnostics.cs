using System.Numerics;

namespace XelsCombatAI.Integrations;

internal sealed record BossModMovementDiagnostics(
    string ActiveModule,
    string ActiveZoneModule,
    string NavigationDestination,
    string NavigationNextWaypoint,
    string NavigationStats,
    string PlannerSteer,
    string MechanicWhisper,
    string ControllerTarget,
    string MovementOverride,
    string HintSummary,
    Vector2? NavigationDestinationPosition,
    Vector2? NavigationNextWaypointPosition,
    BossModNavigationDiagnostics NavigationDetails,
    BossModControllerDiagnostics ControllerDetails,
    BossModMovementOverrideDiagnostics MovementDetails,
    BossModHintDiagnostics HintDetails,
    BossModSafetyRasterDiagnostics SafetyRaster)
{
    public static BossModMovementDiagnostics Empty { get; } = new(
        "<none>",
        "<none>",
        "<none>",
        "<none>",
        "<none>",
        "not checked",
        "not logged",
        "<none>",
        "<none>",
        "<none>",
        null,
        null,
        BossModNavigationDiagnostics.Empty,
        BossModControllerDiagnostics.Empty,
        BossModMovementOverrideDiagnostics.Empty,
        BossModHintDiagnostics.Empty,
        BossModSafetyRasterDiagnostics.Unavailable("not captured"));
}

internal sealed record BossModNavigationDiagnostics(
    float? LeewaySeconds,
    float? TimeToGoal,
    double? PathfindMilliseconds,
    double? RasterizeMilliseconds,
    float? ForceMovementIn)
{
    public static BossModNavigationDiagnostics Empty { get; } = new(null, null, null, null, null);
}

internal sealed record BossModControllerDiagnostics(
    Vector2? NavigationTarget,
    bool? AllowInterruptingCastByMovement,
    bool? ForceCancelCast)
{
    public static BossModControllerDiagnostics Empty { get; } = new(null, null, null);
}

internal sealed record BossModMovementOverrideDiagnostics(
    Vector3? DesiredDirection,
    Vector2? UserMove,
    Vector2? ActualMove,
    bool? MovementBlocked)
{
    public static BossModMovementOverrideDiagnostics Empty { get; } = new(null, null, null, null);
}

internal sealed record BossModHintDiagnostics(
    int? GoalZones,
    int? ForbiddenZones,
    int? TemporaryObstacles,
    int? Teleporters,
    int? ForbiddenDirections,
    int? PredictedDamage,
    int? PotentialTargets,
    string ImminentSpecialMode,
    Vector3? ForcedMovement,
    float? MaxCastTime,
    bool? ForceCancelCast,
    BossModBoundsDiagnostics PathfindMapBounds,
    Vector2? PathfindMapCenter)
{
    public static BossModHintDiagnostics Empty { get; } = new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        "<none>",
        null,
        null,
        null,
        BossModBoundsDiagnostics.Empty,
        null);
}

internal sealed record BossModBoundsDiagnostics(
    string Type,
    string Text,
    float? Radius,
    float? HalfWidth,
    float? HalfHeight,
    float? MapResolution,
    float? PathfindingOffset,
    int? Vertices,
    float? ScaleFactor)
{
    public static BossModBoundsDiagnostics Empty { get; } = new("<none>", "<none>", null, null, null, null, null, null, null);
}

internal sealed record BossModSafetyRasterDiagnostics(
    string Status,
    string Reason,
    Vector2? Center,
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
    BossModSafetyPointDiagnostics Player,
    BossModSafetyPointDiagnostics Destination,
    BossModSafetyPointDiagnostics FirstWaypoint,
    BossModSafetyPointDiagnostics Target)
{
    public static BossModSafetyRasterDiagnostics Unavailable(string reason) => new(
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
        BossModSafetyPointDiagnostics.Empty,
        BossModSafetyPointDiagnostics.Empty,
        BossModSafetyPointDiagnostics.Empty,
        BossModSafetyPointDiagnostics.Empty);
}

internal sealed record BossModSafetyPointDiagnostics(
    string State,
    Vector3? Position,
    int? GridX,
    int? GridY,
    float? PixelMaxG,
    float? PixelPriority)
{
    public static BossModSafetyPointDiagnostics Empty { get; } = new("unknown", null, null, null, null, null);
}
