using System.Collections.Generic;
using System.Numerics;

namespace XelsCombatAI.Models;

internal enum DecisionOverlaySource
{
    Positionals,
    TargetUptime,
    AoE,
    LeyLines,
    HealerCoverage,
    GapCloser,
    FinalMovement,
    NextAction,
    GapCloserLanding,
    EscapeLanding,
    PassageOfArms,
    SurvivabilityZone,
    RedMageMeleeCombo,
}

internal enum DecisionOverlayState
{
    Active,
    Candidate,
    Suppressed,
    Rejected,
    Future
}

internal enum DecisionOverlayShapeKind
{
    Circle,
    Cone,
    Rectangle
}

internal sealed record DecisionOverlaySnapshot(
    DecisionOverlaySource Source,
    DecisionOverlayState State,
    string Label,
    string? Reason,
    int Priority,
    IReadOnlyList<DecisionOverlayShape> Shapes,
    IReadOnlyList<DecisionOverlayLine> Lines,
    IReadOnlyList<DecisionOverlayMarker> Markers);

internal sealed record DecisionOverlayShape(
    DecisionOverlayShapeKind Kind,
    DecisionOverlayState State,
    Vector3 Origin,
    float Radius,
    float HalfWidth,
    float Length,
    float RotationRadians,
    string? Label = null);

internal sealed record DecisionOverlayLine(
    DecisionOverlayState State,
    Vector3 From,
    Vector3 To,
    string? Label = null);

internal sealed record DecisionOverlayMarker(
    DecisionOverlayState State,
    Vector3 Position,
    float Radius,
    string? Label = null);
