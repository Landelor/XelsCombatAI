using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.UI;

internal sealed class DecisionOverlayController(
    Configuration config,
    DalamudServices services,
    BossModPresetController presetController,
    AoePackPositioningController aoePackPositioningController,
    BossModReflectionSafety bossModSafety,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RotationSolverActionReflection rotationSolverActions)
{

    private DecisionOverlayState gapCloserDisplayedState = DecisionOverlayState.Suppressed;
    private DecisionOverlayState gapCloserPendingState = DecisionOverlayState.Suppressed;
    private DateTime gapCloserPendingStateAt = DateTime.MinValue;
    private static readonly TimeSpan GapCloserStateDebounce = TimeSpan.FromMilliseconds(400);

    public void Draw()
    {
        if (!config.Enabled || !config.ShowDecisionOverlay || !services.Condition[ConditionFlag.InCombat])
        {
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var snapshots = this.BuildSnapshots(player).ToArray();
        // Group snapshots by their label anchor position so overlapping labels are stacked vertically.
        var anchorCounts = new Dictionary<(int, int), int>();
        foreach (var snapshot in snapshots)
        {
            var anchor = snapshot.Markers.FirstOrDefault()?.Position ??
                         snapshot.Shapes.FirstOrDefault()?.Origin ??
                         snapshot.Lines.FirstOrDefault()?.To;
            int stackIndex = 0;
            if (anchor.HasValue)
            {
                var key = ((int)MathF.Round(anchor.Value.X * 2f), (int)MathF.Round(anchor.Value.Z * 2f));
                stackIndex = anchorCounts.TryGetValue(key, out var count) ? count : 0;
                anchorCounts[key] = stackIndex + 1;
            }
            this.DrawSnapshot(drawList, snapshot, stackIndex);
        }
    }

    private IEnumerable<DecisionOverlaySnapshot> BuildSnapshots(IBattleChara player)
    {
        var target = services.TargetManager.Target as IBattleChara;

        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var nextAction, out _))
        {
            var shapeKind = nextAction.Shape switch
            {
                RsrAoeShape.Cone         => DecisionOverlayShapeKind.Cone,
                RsrAoeShape.StraightLine => DecisionOverlayShapeKind.Rectangle,
                _                        => DecisionOverlayShapeKind.Circle
            };
            // Project from the best known candidate destination so the player always sees what
            // they'd hit when they arrive — whether moving (Overlay) or drifting (SuggestedCandidate).
            var injectedOverlay = aoePackPositioningController.Overlay;
            var suggestion = aoePackPositioningController.SuggestedCandidate;
            var actionOrigin = injectedOverlay != null
                ? injectedOverlay.Candidate
                : suggestion != null
                    ? suggestion.Candidate
                    : nextAction.Shape == RsrAoeShape.Circle
                        ? nextAction.PrimaryTargetPosition
                        : player.Position;
            var rotation = MathF.Atan2(
                nextAction.PrimaryTargetPosition.X - actionOrigin.X,
                nextAction.PrimaryTargetPosition.Z - actionOrigin.Z);
            yield return new(
                DecisionOverlaySource.NextAction,
                DecisionOverlayState.Future,
                nextAction.ActionName,
                null,
                5,
                [new(shapeKind, DecisionOverlayState.Future, actionOrigin, nextAction.EffectRange, nextAction.HalfWidth, nextAction.EffectRange, rotation)],
                [],
                []);
        }

        if (target != null)
        {
            if (presetController.LastRange > 0f)
            {
                var state = presetController.LastMovement == true ? DecisionOverlayState.Active : DecisionOverlayState.Suppressed;
                yield return new(
                    DecisionOverlaySource.Range,
                    state,
                    $"Range: {presetController.LastRange:0}y",
                    presetController.LastMovement == true ? null : "movement suppressed",
                    10,
                    [new(DecisionOverlayShapeKind.Circle, state, target.Position, presetController.LastRange + target.HitboxRadius, 0f, 0f, 0f)],
                    [],
                    []);
            }


        }

        if (presetController.LastHealerStayNearParty == true)
        {
            var partyMembers = this.GetVisiblePartyMembers(player).ToArray();
            if (partyMembers.Length > 0)
            {
                var center = new Vector3(partyMembers.Average(member => member.Position.X), player.Position.Y, partyMembers.Average(member => member.Position.Z));
                yield return new(
                    DecisionOverlaySource.HealerCoverage,
                    DecisionOverlayState.Candidate,
                    "Healer: party",
                    null,
                    35,
                    [new(DecisionOverlayShapeKind.Circle, DecisionOverlayState.Candidate, center, 15f, 0f, 0f, 0f)],
                    [],
                    partyMembers.Select(member => new DecisionOverlayMarker(DecisionOverlayState.Candidate, member.Position, member.HitboxRadius, null)).ToArray());
            }
        }

        var aoe = aoePackPositioningController.Overlay;
        if (aoe != null)
        {
            var rotation = MathF.Atan2(aoe.PrimaryTarget.X - aoe.Candidate.X, aoe.PrimaryTarget.Z - aoe.Candidate.Z);
            var shapeKind = aoe.Shape switch
            {
                nameof(RsrAoeShape.Cone)         => DecisionOverlayShapeKind.Cone,
                nameof(RsrAoeShape.StraightLine) => DecisionOverlayShapeKind.Rectangle,
                _                                => DecisionOverlayShapeKind.Circle
            };
            yield return new(
                DecisionOverlaySource.AoE,
                DecisionOverlayState.Candidate,
                aoePackPositioningController.RsrHenchedActive ? $"AoE: {aoe.CurrentHits} -> {aoe.BestHits} +RSR" : $"AoE: {aoe.CurrentHits} -> {aoe.BestHits}",
                aoe.ActionName,
                40,
                [new(shapeKind, DecisionOverlayState.Candidate, aoe.Candidate, aoe.Radius, aoe.HalfWidth, aoe.Radius, rotation)],
                [new(DecisionOverlayState.Candidate, player.Position, aoe.Candidate, "AoE")],
                aoe.Targets.Select(targetMarker => new DecisionOverlayMarker(
                    targetMarker.Hit ? DecisionOverlayState.Active : DecisionOverlayState.Rejected,
                    targetMarker.Position,
                    targetMarker.Radius,
                    targetMarker.Hit ? null : "miss")).ToArray());
        }

        var aoeSuggestion = aoePackPositioningController.SuggestedCandidate;
        if (aoeSuggestion != null)
        {
            yield return new(
                DecisionOverlaySource.AoE,
                DecisionOverlayState.Future,
                $"AoE: drift {aoeSuggestion.CurrentHits} -> {aoeSuggestion.BestHits}",
                aoeSuggestion.ActionName,
                38,
                [],
                [new(DecisionOverlayState.Future, player.Position, aoeSuggestion.Candidate, "AoE")],
                []);
        }

        this.AddGapCloserSnapshot(player, target, out var gapCloserSnapshot);
        if (gapCloserSnapshot != null)
        {
            yield return gapCloserSnapshot;
        }

        if (config.UseEscapeGapCloser)
        {
            var escapeDest = escapeGapCloserController.LastSafeEscapeDestination;
            if (escapeDest.HasValue)
            {
                yield return new(
                    DecisionOverlaySource.EscapeLanding,
                    DecisionOverlayState.Future,
                    "Escape",
                    null,
                    55,
                    [],
                    [new(DecisionOverlayState.Future, player.Position, escapeDest.Value, "Escape")],
                    [new(DecisionOverlayState.Future, escapeDest.Value, 0.35f, null)]);
            }
        }

        if (bossModSafety.TryGetSafeMovementIntent(player.Position, out var destination, out _))
        {
            yield return new(
                DecisionOverlaySource.FinalMovement,
                DecisionOverlayState.Active,
                "Move: BMR",
                null,
                100,
                [],
                [new(DecisionOverlayState.Active, player.Position, destination, "Move")],
                [new(DecisionOverlayState.Active, destination, 0.35f, null)]);
        }
    }


    private void AddGapCloserSnapshot(IBattleChara player, IBattleChara? target, out DecisionOverlaySnapshot? snapshot)
    {
        snapshot = null;
        if (!config.UseGapCloser && !config.UseEscapeGapCloser)
        {
            return;
        }

        var reason = config.UseEscapeGapCloser ? escapeGapCloserController.LastEscapeGapCloserSafety : gapCloserController.LastGapCloserSafety;
        var rawState = reason.Contains("used ", StringComparison.OrdinalIgnoreCase)
            ? DecisionOverlayState.Active
            : reason.Contains("current position safe", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("not in gap closer range", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("animation lock", StringComparison.OrdinalIgnoreCase)
                ? DecisionOverlayState.Suppressed
                : reason.Contains("safe", StringComparison.OrdinalIgnoreCase)
                    ? DecisionOverlayState.Candidate
                    : DecisionOverlayState.Rejected;

        var now = DateTime.UtcNow;
        if (rawState != this.gapCloserPendingState)
        {
            this.gapCloserPendingState = rawState;
            this.gapCloserPendingStateAt = now;
        }
        else if (now - this.gapCloserPendingStateAt >= GapCloserStateDebounce)
        {
            this.gapCloserDisplayedState = rawState;
        }

        var state = this.gapCloserDisplayedState;
        if (target == null)
        {
            return;
        }

        var landingPos = gapCloserController.LastSafeLandingPosition;
        var landingMarker = state == DecisionOverlayState.Candidate && landingPos.HasValue
            ? new DecisionOverlayMarker(DecisionOverlayState.Future, landingPos.Value, 0.35f, "Land")
            : null;

        snapshot = new(
            DecisionOverlaySource.GapCloser,
            state,
            state == DecisionOverlayState.Rejected ? "Gap: unsafe" : "Gap",
            reason,
            50,
            [],
            [new(state, player.Position, target.Position, "Gap")],
            landingMarker != null
                ? [new(state, target.Position, target.HitboxRadius, null), landingMarker]
                : [new(state, target.Position, target.HitboxRadius, null)]);

    }


    private IEnumerable<IBattleChara> GetVisiblePartyMembers(IBattleChara player)
    {
        return services.ObjectTable
            .OfType<IBattleChara>()
            .Where(actor => actor.ObjectKind == ObjectKind.Pc && actor.GameObjectId != player.GameObjectId);
    }

    private void DrawSnapshot(ImDrawListPtr drawList, DecisionOverlaySnapshot snapshot, int labelStackIndex = 0)
    {
        var color = ColorFor(snapshot.State, snapshot.Source);
        foreach (var shape in snapshot.Shapes)
        {
            this.DrawShape(drawList, shape, ColorFor(shape.State, snapshot.Source));
        }

        foreach (var line in snapshot.Lines)
        {
            this.DrawLine(drawList, line.From, line.To, ColorFor(line.State, snapshot.Source), thickness: snapshot.Source == DecisionOverlaySource.FinalMovement ? 3f : 2f);
        }

        foreach (var marker in snapshot.Markers)
        {
            this.DrawCircle(drawList, marker.Position, Math.Max(marker.Radius, 0.25f), ColorFor(marker.State, snapshot.Source), thickness: 2f);
            if (marker.Label != null)
            {
                this.DrawLabel(drawList, marker.Position, marker.Label, ColorFor(marker.State, snapshot.Source));
            }
        }

        var labelAnchor = snapshot.Markers.FirstOrDefault()?.Position ??
                          snapshot.Shapes.FirstOrDefault()?.Origin ??
                          snapshot.Lines.FirstOrDefault()?.To;
        if (labelAnchor.HasValue)
        {
            this.DrawLabel(drawList, labelAnchor.Value, snapshot.Label, color, pixelYOffset: labelStackIndex * 16f);
        }
    }

    private void DrawShape(ImDrawListPtr drawList, DecisionOverlayShape shape, uint color)
    {
        switch (shape.Kind)
        {
            case DecisionOverlayShapeKind.Circle:
                this.DrawCircle(drawList, shape.Origin, shape.Radius, color, 2f);
                break;
            case DecisionOverlayShapeKind.Cone:
                this.DrawCone(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, color);
                break;
            case DecisionOverlayShapeKind.Rectangle:
                this.DrawRectangle(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, color);
                break;
        }
    }

    private void DrawCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color, float thickness)
    {
        const int Segments = 64;
        Vector2? previous = null;
        for (var i = 0; i <= Segments; ++i)
        {
            var angle = i * MathF.Tau / Segments;
            var point = center + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            if (!this.Project(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous.HasValue)
            {
                drawList.AddLine(previous.Value, screen, color, thickness);
            }

            previous = screen;
        }
    }

    private void DrawCone(ImDrawListPtr drawList, Vector3 center, float radius, float rotation, float halfWidth, uint color)
    {
        var left = center + Direction(rotation - halfWidth) * radius;
        var right = center + Direction(rotation + halfWidth) * radius;
        this.DrawLine(drawList, center, left, color, 2f);
        this.DrawLine(drawList, center, right, color, 2f);

        const int Segments = 24;
        Vector2? previous = null;
        for (var i = 0; i <= Segments; ++i)
        {
            var angle = rotation - halfWidth + 2f * halfWidth * i / Segments;
            var point = center + Direction(angle) * radius;
            if (!this.Project(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous.HasValue)
            {
                drawList.AddLine(previous.Value, screen, color, 2f);
            }

            previous = screen;
        }
    }

    private void DrawRectangle(ImDrawListPtr drawList, Vector3 origin, float rotation, float length, float halfWidth, uint color)
    {
        var forward = Direction(rotation);
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var p1 = origin + side * halfWidth;
        var p2 = origin - side * halfWidth;
        var p3 = origin + forward * length - side * halfWidth;
        var p4 = origin + forward * length + side * halfWidth;
        this.DrawLine(drawList, p1, p2, color, 2f);
        this.DrawLine(drawList, p2, p3, color, 2f);
        this.DrawLine(drawList, p3, p4, color, 2f);
        this.DrawLine(drawList, p4, p1, color, 2f);
    }

    private void DrawLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness)
    {
        if (this.Project(from, out var fromScreen) && this.Project(to, out var toScreen))
        {
            drawList.AddLine(fromScreen, toScreen, color, thickness);
        }
    }

    private void DrawLabel(ImDrawListPtr drawList, Vector3 world, string label, uint color, float pixelYOffset = 0f)
    {
        if (!this.Project(world, out var screen))
        {
            return;
        }

        var textSize = ImGui.CalcTextSize(label);
        // Center horizontally, stack upward from floor point; pixelYOffset shifts additional labels further up.
        var pos = screen + new Vector2(-textSize.X * 0.5f, -textSize.Y - 4f - pixelYOffset);
        drawList.AddText(pos + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)), label);
        drawList.AddText(pos, color, label);
    }

    private bool Project(Vector3 world, out Vector2 screen)
    {
        return services.GameGui.WorldToScreen(world, out screen);
    }

    private static Vector3 Direction(float rotation)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return new Vector3(sin, 0f, cos);
    }

    private static uint ColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        if (source == DecisionOverlaySource.FinalMovement)
        {
            return ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        }

        return state switch
        {
            DecisionOverlayState.Active     => ImGui.GetColorU32(new Vector4(0.25f, 0.82f, 0.38f, 1f)),
            DecisionOverlayState.Candidate  => ImGui.GetColorU32(new Vector4(0.25f, 0.64f, 1f, 1f)),
            DecisionOverlayState.Future     => ImGui.GetColorU32(new Vector4(1f, 0.84f, 0.2f, 1f)),
            DecisionOverlayState.Rejected   => ImGui.GetColorU32(new Vector4(1f, 0.2f, 0.2f, 1f)),
            DecisionOverlayState.Suppressed => ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f)),
            _                               => ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f))
        };
    }
}
