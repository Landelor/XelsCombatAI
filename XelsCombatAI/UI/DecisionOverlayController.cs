using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.UI;

internal sealed class DecisionOverlayController(
    Configuration config,
    DalamudServices services,
    AoePackPositioningController aoePackPositioningController,
    Func<bool> currentTargetHasBossModule,
    PassageOfArmsPositioningController passageOfArmsPositioningController,
    HealerAoePositioningController healerAoePositioningController,
    SurvivabilityZonePositioningController survivabilityZonePositioningController,
    BossModReflectionSafety bossModSafety,
    MobilityDecisionEvaluator mobilityDecisionEvaluator,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RedMageMeleeComboController redMageMeleeComboController,
    RotationSolverActionReflection rotationSolverActions)
{

    private DecisionOverlayState gapCloserDisplayedState = DecisionOverlayState.Suppressed;
    private DecisionOverlayState gapCloserPendingState = DecisionOverlayState.Suppressed;
    private DateTime gapCloserPendingStateAt = DateTime.MinValue;
    private DateTime nextDrawErrorLog = DateTime.MinValue;
    private static readonly TimeSpan GapCloserStateDebounce = TimeSpan.FromMilliseconds(400);

    private sealed record OverlayLabel(DecisionOverlayState State, DecisionOverlaySource Source, string Text);

    public void Draw()
    {
        try
        {
            if (!config.ShowDecisionOverlay)
            {
                return;
            }

            if (config.ShowDecisionOverlayHud)
            {
                this.DrawConfigHud();
            }

            this.DrawWorldDebug();
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow < this.nextDrawErrorLog)
            {
                return;
            }

            services.Log.Verbose(ex, "Decision overlay draw failed.");
            this.nextDrawErrorLog = DateTime.UtcNow.AddSeconds(10);
        }
    }

    private void DrawWorldDebug()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var snapshotSource = this.BuildSnapshots(player);
        if (config.ShowDecisionOverlayHud)
        {
            snapshotSource = snapshotSource.Concat(this.BuildConfigDebugSnapshots(player));
        }

        var snapshots = snapshotSource.OrderBy(snapshot => snapshot.Priority).ToArray();
        var labelGroups = new Dictionary<(int, int), (Vector3 Anchor, List<OverlayLabel> Labels)>();
        foreach (var snapshot in snapshots)
        {
            var anchor = snapshot.Markers.FirstOrDefault()?.Position ??
                         snapshot.Shapes.FirstOrDefault()?.Origin ??
                         snapshot.Lines.FirstOrDefault()?.To;
            if (anchor.HasValue)
            {
                var key = ((int)MathF.Round(anchor.Value.X * 2f), (int)MathF.Round(anchor.Value.Z * 2f));
                if (!labelGroups.TryGetValue(key, out var group))
                {
                    group = (anchor.Value, []);
                    labelGroups[key] = group;
                }

                group.Labels.Add(new(snapshot.State, snapshot.Source, BuildSnapshotLabel(snapshot)));
            }

            this.DrawSnapshot(drawList, snapshot);
        }

        foreach (var group in labelGroups.Values)
        {
            this.DrawLabelGroup(drawList, group.Anchor, group.Labels);
        }
    }

    private IEnumerable<DecisionOverlaySnapshot> BuildSnapshots(IBattleChara player)
    {
        var target = services.TargetManager.Target as IBattleChara;

        var redMage = redMageMeleeComboController.Status;
        if (redMage.Enabled && redMage.CandidateDestination.HasValue)
        {
            yield return new(
                DecisionOverlaySource.RedMageMeleeCombo,
                DecisionOverlayState.Candidate,
                $"RDM: {redMage.Mode}",
                redMage.LastReason,
                18,
                [],
                [new(DecisionOverlayState.Candidate, player.Position, redMage.CandidateDestination.Value, "RDM combo range")],
                [new(DecisionOverlayState.Candidate, redMage.CandidateDestination.Value, 0.35f, "RDM")]);
        }


        if (rotationSolverActions.TryGetUpcomingGcd(requirePreview: false, out var nextAction, out _))
        {
            var shapeKind = nextAction.Shape switch
            {
                RsrAoeShape.Cone => DecisionOverlayShapeKind.Cone,
                RsrAoeShape.StraightLine => DecisionOverlayShapeKind.Rectangle,
                _ => DecisionOverlayShapeKind.Circle
            };
            var actionOrigin = this.ResolveNextActionOrigin(player, nextAction);
            var rotation = MathF.Atan2(
                nextAction.PrimaryTargetPosition.X - actionOrigin.X,
                nextAction.PrimaryTargetPosition.Z - actionOrigin.Z);
            yield return new(
                DecisionOverlaySource.NextAction,
                DecisionOverlayState.Future,
                $"Next cast: {nextAction.ActionName}",
                $"{nextAction.Shape} {nextAction.EffectRange:0.#}y",
                5,
                [new(shapeKind, DecisionOverlayState.Future, actionOrigin, nextAction.EffectRange, nextAction.HalfWidth, nextAction.EffectRange, rotation, "next action area")],
                [],
                []);
        }

        var healerCoverage = healerAoePositioningController.Overlay;
        if (healerCoverage != null)
        {
            var coverageState = !config.Enabled || !config.ManageMovement || !config.ManageHealerCoverageZone
                ? DecisionOverlayState.Suppressed
                : healerCoverage.Injected
                ? DecisionOverlayState.Candidate
                : DecisionOverlayState.Active;
            var coverageLabel = $"Healer coverage: {healerCoverage.CoveredMembers}/{healerCoverage.TotalMembers}";
            var coverageReason = coverageState == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageHealerCoverageZone, "Healer coverage zone"))
                : null;
            yield return new(
                DecisionOverlaySource.HealerCoverage,
                coverageState,
                coverageLabel,
                coverageReason ?? (healerCoverage.Injected ? $"{healerCoverage.DistanceToCenter:0.0}y to better coverage" : "current coverage held"),
                20,
                [new(DecisionOverlayShapeKind.Circle, coverageState, healerCoverage.Center, healerCoverage.Radius, 0f, 0f, 0f, "coverage zone")],
                healerCoverage.Injected
                    ? [new(DecisionOverlayState.Candidate, player.Position, healerCoverage.Center, "move into zone")]
                    : [],
                healerCoverage.Members
                    .Select(member => new DecisionOverlayMarker(coverageState, member, 0.35f, null))
                    .ToArray());
        }

        var aoe = aoePackPositioningController.Overlay;
        if (aoe != null)
        {
            if (string.Equals(aoe.ActionName, "Pack engagement", StringComparison.Ordinal))
            {
                // Approach ring — PrimaryTarget is the pack centroid, Radius is the engagement range
                var centroid = aoe.PrimaryTarget;
                var rangeLabel = $"{aoe.Radius:0}y";
                yield return new(
                    DecisionOverlaySource.AoE,
                    DecisionOverlayState.Candidate,
                    "Move into attack range",
                    $"target range {rangeLabel}",
                    40,
                    [new(DecisionOverlayShapeKind.Circle, DecisionOverlayState.Candidate, centroid, aoe.Radius, 0f, aoe.Radius, 0f, "attack range")],
                    [new(DecisionOverlayState.Candidate, player.Position, centroid, $"move to {rangeLabel}")],
                    aoe.Targets.Select(t => new DecisionOverlayMarker(DecisionOverlayState.Suppressed, t.Position, t.Radius, null)).ToArray());
            }
            else
            {
                var rotation = MathF.Atan2(aoe.PrimaryTarget.X - aoe.Candidate.X, aoe.PrimaryTarget.Z - aoe.Candidate.Z);
                var shapeKind = aoe.Shape switch
                {
                    nameof(RsrAoeShape.Cone) => DecisionOverlayShapeKind.Cone,
                    nameof(RsrAoeShape.StraightLine) => DecisionOverlayShapeKind.Rectangle,
                    _ => DecisionOverlayShapeKind.Circle
                };
                var label = aoePackPositioningController.RsrHenchedActive
                    ? $"Move for AoE hits: {aoe.CurrentHits}->{aoe.BestHits} +RSR"
                    : $"Move for AoE hits: {aoe.CurrentHits}->{aoe.BestHits}";
                yield return new(
                    DecisionOverlaySource.AoE,
                    DecisionOverlayState.Candidate,
                    label,
                    aoe.ActionName,
                    40,
                    [new(shapeKind, DecisionOverlayState.Candidate, aoe.Candidate, aoe.Radius, aoe.HalfWidth, aoe.Radius, rotation, "AoE from here")],
                    [new(DecisionOverlayState.Candidate, player.Position, aoe.Candidate, "move for AoE")],
                    aoe.Targets.Select(t => new DecisionOverlayMarker(
                        t.InsideAvoidedHitbox ? DecisionOverlayState.Rejected
                            : t.Hit ? DecisionOverlayState.Active : DecisionOverlayState.Rejected,
                        t.Position, t.Radius,
                        t.InsideAvoidedHitbox ? t.AvoidanceLabel ?? "inside" : t.Hit ? null : "miss")).ToArray());
            }
        }

        // AoE suggestion — better position found but still evaluating (debounce not yet elapsed)
        var suggestion = aoePackPositioningController.SuggestedCandidate;
        if (suggestion != null && aoe == null)
        {
            var rotation = MathF.Atan2(suggestion.PrimaryTarget.X - suggestion.Candidate.X, suggestion.PrimaryTarget.Z - suggestion.Candidate.Z);
            var shapeKind = suggestion.Shape switch
            {
                nameof(RsrAoeShape.Cone) => DecisionOverlayShapeKind.Cone,
                nameof(RsrAoeShape.StraightLine) => DecisionOverlayShapeKind.Rectangle,
                _ => DecisionOverlayShapeKind.Circle
            };
            yield return new(
                DecisionOverlaySource.AoE,
                DecisionOverlayState.Future,
                $"AoE candidate: {suggestion.CurrentHits}->{suggestion.BestHits}",
                suggestion.ActionName,
                39,
                [new(shapeKind, DecisionOverlayState.Future, suggestion.Candidate, suggestion.Radius, suggestion.HalfWidth, suggestion.Radius, rotation, "AoE preview")],
                [new(DecisionOverlayState.Future, player.Position, suggestion.Candidate, "preview AoE")],
                suggestion.Targets.Select(t => new DecisionOverlayMarker(
                    t.Hit ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed,
                    t.Position, t.Radius, null)).ToArray());
        }


        passageOfArmsPositioningController.RefreshOverlay();
        var passage = passageOfArmsPositioningController.Overlay;
        if (passage != null)
        {
            var state = !config.Enabled || !config.ManageMovement || !config.ManagePassageOfArmsPositioning
                ? DecisionOverlayState.Suppressed
                : passage.Injected
                ? passage.PlayerInCone ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            var reason = state == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManagePassageOfArmsPositioning, "Stand behind Passage of Arms"))
                : null;
            yield return new(
                DecisionOverlaySource.PassageOfArms,
                state,
                passage.PlayerInCone ? "Stay in Passage of Arms" : "Move behind Passage of Arms",
                reason ?? passage.PaladinName,
                45,
                [new(DecisionOverlayShapeKind.Cone, state, passage.PaladinPosition, passage.Radius, passage.HalfAngle, passage.Radius, passage.RotationRadians, "protected cone")],
                [new(state, player.Position, passage.PreferredPosition, "move to Passage")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, passage.PaladinPosition, 0.35f, "PLD"),
                    new DecisionOverlayMarker(state, passage.PreferredPosition, 0.35f, "Wings")
                ]);
        }

        survivabilityZonePositioningController.RefreshOverlay();
        var survZone = survivabilityZonePositioningController.Overlay;
        if (survZone != null)
        {
            var state = !config.Enabled || !config.ManageMovement || !config.ManageDefensiveGroundZonePositioning
                ? DecisionOverlayState.Suppressed
                : survZone.Injected
                ? survZone.PlayerInZone ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            var reason = state == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageDefensiveGroundZonePositioning, "Stand in defensive ground effects"))
                : null;
            var label = survZone.PlayerInZone ? $"{survZone.ZoneName}: inside" : survZone.ZoneName;
            yield return new(
                DecisionOverlaySource.SurvivabilityZone,
                state,
                survZone.PlayerInZone ? $"Stay in {label}" : $"Move into {label}",
                reason ?? survZone.CasterName,
                44,
                [new(DecisionOverlayShapeKind.Circle, state, survZone.ZoneCenter, survZone.Radius, 0f, 0f, 0f, "defensive ground")],
                survZone.PlayerInZone
                    ? []
                    : [new DecisionOverlayLine(state, player.Position, survZone.ZoneCenter, $"move into {survZone.ZoneName}")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, survZone.CasterPosition, 0.35f, survZone.ZoneName[..3]),
                    new DecisionOverlayMarker(state, survZone.ZoneCenter, 0.25f, "Zone")
                ]);
        }

        this.AddGapCloserSnapshot(player, target, out var gapCloserSnapshot);
        if (gapCloserSnapshot != null)
        {
            yield return gapCloserSnapshot;
        }

        if (config.UseGapCloser)
        {
            var escapeDest = escapeGapCloserController.LastSafeEscapeDestination;
            if (escapeDest.HasValue)
            {
                yield return new(
                    DecisionOverlaySource.EscapeLanding,
                    DecisionOverlayState.Future,
                    "Gap closer safety",
                    "safe landing preview",
                    55,
                    [],
                    [new(DecisionOverlayState.Future, player.Position, escapeDest.Value, "dash to safe spot")],
                    [new(DecisionOverlayState.Future, escapeDest.Value, 0.35f, "safe landing")]);
            }
        }

        if (bossModSafety.TryGetSafeMovementIntent(player.Position, out var destination, out _))
        {
            yield return new(
                DecisionOverlaySource.FinalMovement,
                DecisionOverlayState.Active,
                "BMR mechanic movement",
                "encounter safety destination",
                100,
                [],
                [new(DecisionOverlayState.Active, player.Position, destination, "BMR safe spot")],
                [new(DecisionOverlayState.Active, destination, 0.35f, "safe spot")]);
        }
    }

    private IEnumerable<DecisionOverlaySnapshot> BuildConfigDebugSnapshots(IBattleChara player)
    {
        var target = services.TargetManager.Target as IBattleChara;
        if (target != null)
        {
            if (!config.Enabled || !config.ManageMovement || !config.ManageForbiddenZoneDistance)
            {
                var forbiddenReason = this.DisabledReason(
                    (config.Enabled, "Enabled"),
                    (config.ManageMovement, "Automate movement"),
                    (config.ManageForbiddenZoneDistance, "Avoid danger zones"));
                yield return new(
                    DecisionOverlaySource.TargetUptime,
                    DecisionOverlayState.Suppressed,
                    $"Danger buffer disabled ({config.PreferredForbiddenZoneDistance:0.#}y)",
                    forbiddenReason,
                    16,
                    [new(DecisionOverlayShapeKind.Circle, DecisionOverlayState.Suppressed, target.Position, target.HitboxRadius + config.PreferredForbiddenZoneDistance, 0f, 0f, 0f, "danger buffer")],
                    [],
                    []);
            }

            var insideEnemiesEnabled = config.Enabled && config.ManageMovement && config.AvoidStandingInsideEnemies
                && currentTargetHasBossModule();
            var insideEnemiesState = insideEnemiesEnabled ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed;
            var insideEnemiesReason = this.DisabledReason(
                (config.Enabled, "Enabled"),
                (config.ManageMovement, "Automate movement"),
                (config.AvoidStandingInsideEnemies, "Avoid standing inside bosses"));
            yield return new(
                DecisionOverlaySource.TargetUptime,
                insideEnemiesState,
                insideEnemiesEnabled ? "Avoid boss center" : "Boss center avoidance disabled",
                insideEnemiesReason,
                17,
                [new(DecisionOverlayShapeKind.Circle, insideEnemiesState, target.Position, BossCenterAvoidanceController.AvoidanceRadius(target.HitboxRadius), 0f, 0f, 0f, "avoid center")],
                [],
                [new(insideEnemiesState, target.Position, 0.35f, "Boss")]);

            foreach (var positionalSnapshot in this.BuildPositionalDebugSnapshots(target))
            {
                yield return positionalSnapshot;
            }

            foreach (var gapSnapshot in this.BuildGapCloserDebugSnapshots(player, target))
            {
                yield return gapSnapshot;
            }
        }

        if (bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out var safeReason))
        {
            var movementEnabled = config.Enabled && config.ManageMovement;
            var state = movementEnabled ? DecisionOverlayState.Active : DecisionOverlayState.Suppressed;
            yield return new(
                DecisionOverlaySource.FinalMovement,
                state,
                movementEnabled ? "Safe move" : "Safe move disabled",
                movementEnabled ? safeReason : this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement")),
                90,
                [],
                [new(state, player.Position, safeDestination, "move to safe spot")],
                [new(state, safeDestination, 0.35f, "safe spot")]);

            if (config.UseGapCloser || !movementEnabled)
            {
                var escapeEnabled = movementEnabled && config.UseGapCloser && this.CurrentJobGapCloserEnabled();
                var escapeState = escapeEnabled ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed;
                var escapeReason = this.DisabledReason(
                    (config.Enabled, "Enabled"),
                    (config.ManageMovement, "Automate movement"),
                    (config.UseGapCloser, "Gap closers"),
                    (this.CurrentJobGapCloserEnabled(), "Current job gap closer allowlist"));
                yield return new(
                    DecisionOverlaySource.EscapeLanding,
                    escapeState,
                    $"Safety gap closer {config.MinimumGapCloserDistance:0}y",
                    escapeReason,
                    54,
                    [],
                    [new(escapeState, player.Position, safeDestination, "dash to safety")],
                    [new(escapeState, safeDestination, 0.35f, "safe landing")]);
            }
        }
    }

    private IEnumerable<DecisionOverlaySnapshot> BuildPositionalDebugSnapshots(IBattleChara target)
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null || JobRoles.GetRangeRole(player) != RangeRole.Melee)
        {
            yield break;
        }

        var state = config.Enabled && config.ManagePositionals ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed;
        var reason = this.DisabledReason(
            (config.Enabled, "Enabled"),
            (config.ManagePositionals, "Do positionals"));
        var radius = MathF.Max(target.HitboxRadius + 1.5f, 2.5f);
        var rearRotation = target.Rotation + MathF.PI;
        var flankLeftRotation = target.Rotation + MathF.PI * 0.5f;
        var flankRightRotation = target.Rotation - MathF.PI * 0.5f;

        yield return new(
            DecisionOverlaySource.Positionals,
            state,
            state == DecisionOverlayState.Future ? "Positionals: rear/flank zones" : "Positionals disabled",
            reason,
            20,
            [
                new(DecisionOverlayShapeKind.Cone, state, target.Position, radius, MathF.PI / 4f, radius, rearRotation, "rear"),
                new(DecisionOverlayShapeKind.Cone, state, target.Position, radius, MathF.PI / 5f, radius, flankLeftRotation, "flank"),
                new(DecisionOverlayShapeKind.Cone, state, target.Position, radius, MathF.PI / 5f, radius, flankRightRotation, "flank")
            ],
            [],
            []);

    }

    private IEnumerable<DecisionOverlaySnapshot> BuildGapCloserDebugSnapshots(IBattleChara player, IBattleChara target)
    {
        var movementEnabled = config.Enabled && config.ManageMovement;
        var reengageEnabled = movementEnabled && config.UseGapCloser && this.CurrentJobGapCloserEnabled();
        var state = reengageEnabled ? DecisionOverlayState.Future : DecisionOverlayState.Suppressed;
        var distanceToHitbox = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var reason = this.DisabledReason(
            (config.Enabled, "Enabled"),
            (config.ManageMovement, "Automate movement"),
            (config.UseGapCloser, "Gap closers"),
            (this.CurrentJobGapCloserEnabled(), "Current job gap closer allowlist"));

        yield return new(
            DecisionOverlaySource.GapCloser,
            state,
            $"Reengage gap closer {distanceToHitbox:0.#}y / min {config.MinimumGapCloserDistance:0}y",
            reason,
            51,
            [],
            [new(state, player.Position, target.Position, "dash to target")],
            [new(state, target.Position, target.HitboxRadius, "Target")]);
    }

    private void DrawConfigHud()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.72f);
        var flags = ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin("XCAI Debug Overlay###XCAIConfigDebugOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("XCAI debug overlay");
        ImGui.Separator();
        if (ImGui.BeginTable("##xcai_config_overlay_table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthFixed, 210f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 82f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 190f);

            this.DrawConfigSection("General");
            this.DrawConfigRow("Enabled", config.Enabled, config.Enabled);
            this.DrawConfigRow("Print command messages in chat", config.EchoStatusToChat, config.EchoStatusToChat);
            this.DrawConfigRow("Show movement overlay", config.ShowDecisionOverlay, config.ShowDecisionOverlay);
            this.DrawConfigRow("Show overlay debug HUD", config.ShowDecisionOverlayHud, config.ShowDecisionOverlay && config.ShowDecisionOverlayHud, this.DisabledReason((config.ShowDecisionOverlay, "Show movement overlay"), (config.ShowDecisionOverlayHud, "Show overlay debug HUD")));

            this.DrawConfigSection("Movement");
            this.DrawConfigRow("Automate movement", config.ManageMovement, config.Enabled && config.ManageMovement, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement")));
            this.DrawConfigRow("Pause when I move", config.RespectManualMovement, config.Enabled && config.ManageMovement && config.RespectManualMovement, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.RespectManualMovement, "Pause when I move")));
            this.DrawConfigRow("Movement timing", config.CombatStyle, config.Enabled && config.ManageMovement, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement")));
            this.DrawConfigRow("Avoid danger zones", config.ManageForbiddenZoneDistance, config.Enabled && config.ManageMovement && config.ManageForbiddenZoneDistance, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageForbiddenZoneDistance, "Avoid danger zones")));
            this.DrawConfigRow("Extra danger-zone space", config.PreferredForbiddenZoneDistance, config.Enabled && config.ManageMovement && config.ManageForbiddenZoneDistance, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageForbiddenZoneDistance, "Avoid danger zones")));
            this.DrawConfigRow("Stand in defensive ground effects", config.ManageDefensiveGroundZonePositioning, config.Enabled && config.ManageMovement && config.ManageDefensiveGroundZonePositioning, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageDefensiveGroundZonePositioning, "Stand in defensive ground effects")));
            this.DrawConfigRow("Stand behind Passage of Arms", config.ManagePassageOfArmsPositioning, config.Enabled && config.ManageMovement && config.ManagePassageOfArmsPositioning, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManagePassageOfArmsPositioning, "Stand behind Passage of Arms")));
            this.DrawConfigRow("Avoid standing inside bosses", config.AvoidStandingInsideEnemies, config.Enabled && config.ManageMovement && config.AvoidStandingInsideEnemies, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.AvoidStandingInsideEnemies, "Avoid standing inside bosses")));
            this.DrawConfigRow("Avoid arena edge", config.AvoidArenaEdge, config.Enabled && config.ManageMovement && config.AvoidArenaEdge, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.AvoidArenaEdge, "Avoid arena edge")));

            this.DrawConfigSection("AoE & Trash");
            this.DrawConfigRow("Move for better AoE hits", config.ManageAoePackPositioning, config.Enabled && config.ManageMovement && config.ManageAoePackPositioning, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageAoePackPositioning, "Move for better AoE hits")));
            this.DrawConfigRow("Pick better AoE target", config.PickBetterAoeTarget, config.Enabled && config.PickBetterAoeTarget, this.DisabledReason((config.Enabled, "Enabled"), (config.PickBetterAoeTarget, "Pick better AoE target")));
            this.DrawConfigRow("Keep a trash target selected", config.KeepTrashTargetSelected, config.Enabled && config.KeepTrashTargetSelected, this.DisabledReason((config.Enabled, "Enabled"), (config.KeepTrashTargetSelected, "Keep a trash target selected")));
            this.DrawConfigRow("Healer coverage zone", config.ManageHealerCoverageZone, config.Enabled && config.ManageMovement && config.ManageHealerCoverageZone, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageHealerCoverageZone, "Healer coverage zone")));

            this.DrawConfigSection("Positionals");
            this.DrawConfigRow("Do positionals", config.ManagePositionals, config.Enabled && config.ManagePositionals, this.DisabledReason((config.Enabled, "Enabled"), (config.ManagePositionals, "Do positionals")));
            this.DrawConfigRow("Use True North", config.ManageTrueNorth, config.Enabled && config.ManagePositionals && config.ManageTrueNorth, this.DisabledReason((config.Enabled, "Enabled"), (config.ManagePositionals, "Do positionals"), (config.ManageTrueNorth, "Use True North")));

            this.DrawConfigSection("Job Specific");
            this.DrawConfigRow("RDM melee combo movement", config.UseRedMageMeleeComboMovement, config.Enabled && config.ManageMovement && config.UseRedMageMeleeComboMovement, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseRedMageMeleeComboMovement, "Move for melee combo")));
            this.DrawConfigRow("RDM combo state", redMageMeleeComboController.Status.Mode, config.Enabled && config.ManageMovement && config.UseRedMageMeleeComboMovement && redMageMeleeComboController.Status.Enabled, redMageMeleeComboController.Status.LastReason);
            this.DrawConfigRow("Stay in Ley Lines", config.ManageLeylines, config.Enabled && config.ManageMovement && config.ManageLeylines, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageLeylines, "Stay in Ley Lines")));
            this.DrawConfigRow("Use Between the Lines", config.UseBetweenTheLines, config.Enabled && config.ManageMovement && config.ManageLeylines && config.UseBetweenTheLines, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageLeylines, "Stay in Ley Lines"), (config.UseBetweenTheLines, "Use Between the Lines")));
            this.DrawConfigRow("Use Retrace", config.UseRetrace, config.Enabled && config.ManageMovement && config.ManageLeylines && config.UseRetrace, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageLeylines, "Stay in Ley Lines"), (config.UseRetrace, "Use Retrace")));
            this.DrawConfigRow("Walk back to Ley Lines", config.ReturnToLeylines, config.Enabled && config.ManageMovement && config.ManageLeylines && config.ReturnToLeylines, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageLeylines, "Stay in Ley Lines"), (config.ReturnToLeylines, "Walk back to Ley Lines")));

            this.DrawConfigSection("Dashes");
            this.DrawConfigRow("Gap closers", config.UseGapCloser, config.Enabled && config.ManageMovement && config.UseGapCloser, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseGapCloser, "Gap closers")));
            this.DrawConfigRow("Gap closer jobs", this.FormatJobAllowlist("PLD", config.GapCloserPLD, "WAR", config.GapCloserWAR, "DRK", config.GapCloserDRK, "GNB", config.GapCloserGNB, "MNK", config.GapCloserMNK, "DRG", config.GapCloserDRG, "BRD", config.GapCloserBRD, "NIN", config.GapCloserNIN, "SAM", config.GapCloserSAM, "DNC", config.GapCloserDNC, "RPR", config.GapCloserRPR, "VPR", config.GapCloserVPR, "WHM", config.GapCloserWHM, "BLM", config.GapCloserBLM, "RDM", config.GapCloserRDM, "SGE", config.GapCloserSGE, "PCT", config.GapCloserPCT), config.Enabled && config.ManageMovement && config.UseGapCloser && this.CurrentJobGapCloserEnabled(), this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseGapCloser, "Gap closers"), (this.CurrentJobGapCloserEnabled(), "Current job gap closer allowlist")));
            this.DrawConfigRow("Minimum gap-closer distance", config.MinimumGapCloserDistance, config.Enabled && config.ManageMovement && config.UseGapCloser, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseGapCloser, "Gap closers")));
            this.DrawConfigRow("Greedy dash style", config.CombatStyle == CombatStyle.Normal ? "off" : "on", config.Enabled && config.ManageMovement && config.UseGapCloser && config.CombatStyle != CombatStyle.Normal, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseGapCloser, "Gap closers"), (config.CombatStyle != CombatStyle.Normal, "Greedy movement timing")));
            this.DrawConfigRow("Greedy unsafe safety dashes", config.CombatStyle == CombatStyle.Normal ? "off" : config.CombatStyle, config.Enabled && config.ManageMovement && config.UseGapCloser && config.CombatStyle != CombatStyle.Normal, this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.UseGapCloser, "Gap closers"), (config.CombatStyle != CombatStyle.Normal, "Greedy movement timing")));

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private void DrawConfigSection(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 1f, 1f), label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(string.Empty);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(string.Empty);
    }

    private void DrawConfigRow(string label, object? value, bool effective, string? reason = null)
    {
        var color = effective
            ? new Vector4(0.92f, 0.92f, 0.92f, 1f)
            : new Vector4(0.55f, 0.55f, 0.55f, 1f);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(color, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(color, FormatConfigValue(value));
        ImGui.TableSetColumnIndex(2);
        ImGui.TextColored(color, effective ? "active" : reason ?? "inactive");
    }

    private string? DisabledReason(params (bool Enabled, string Label)[] gates)
    {
        foreach (var gate in gates)
        {
            if (!gate.Enabled)
            {
                return $"disabled: {gate.Label}";
            }
        }

        return null;
    }

    private bool CurrentJobGapCloserEnabled()
    {
        var classJobId = services.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        return classJobId switch
        {
            1 or 19 => config.GapCloserPLD,
            3 or 21 => config.GapCloserWAR,
            32 => config.GapCloserDRK,
            37 => config.GapCloserGNB,
            2 or 20 => config.GapCloserMNK,
            4 or 22 => config.GapCloserDRG,
            5 or 23 => config.GapCloserBRD,
            25 => config.GapCloserBLM,
            29 or 30 => config.GapCloserNIN,
            34 => config.GapCloserSAM,
            38 => config.GapCloserDNC,
            39 => config.GapCloserRPR,
            24 => config.GapCloserWHM,
            35 => config.GapCloserRDM,
            40 => config.GapCloserSGE,
            41 => config.GapCloserVPR,
            42 => config.GapCloserPCT,
            _ => false
        };
    }

    private string FormatJobAllowlist(params object[] nameValuePairs)
    {
        var enabled = new List<string>();
        for (var i = 0; i + 1 < nameValuePairs.Length; i += 2)
        {
            if (nameValuePairs[i] is string name && nameValuePairs[i + 1] is bool value && value)
            {
                enabled.Add(name);
            }
        }

        return enabled.Count == 0 ? "none" : string.Join(", ", enabled);
    }

    private static string FormatConfigValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            bool boolValue => boolValue ? "on" : "off",
            float floatValue => floatValue.ToString("0.###", CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private Vector3 ResolveNextActionOrigin(IBattleChara player, RsrAoeActionSnapshot nextAction)
    {
        if (nextAction.IsFriendly && nextAction.Shape == RsrAoeShape.Circle)
        {
            return player.Position;
        }

        var injectedOverlay = aoePackPositioningController.Overlay;
        var suggestedOverlay = aoePackPositioningController.SuggestedCandidate;
        if (injectedOverlay != null &&
            injectedOverlay.ActionId == nextAction.AdjustedActionId)
        {
            return injectedOverlay.Candidate;
        }

        if (suggestedOverlay != null &&
            suggestedOverlay.ActionId == nextAction.AdjustedActionId)
        {
            return suggestedOverlay.Candidate;
        }

        var targetCenteredCircle = nextAction.Shape == RsrAoeShape.Circle &&
                                   nextAction.Range > nextAction.EffectRange + 3f;
        return targetCenteredCircle ? nextAction.PrimaryTargetPosition : player.Position;
    }


    private void AddGapCloserSnapshot(IBattleChara player, IBattleChara? target, out DecisionOverlaySnapshot? snapshot)
    {
        snapshot = null;
        if (!config.UseGapCloser)
        {
            return;
        }

        var useMobilityDecision = TryGetRecentMobilityDecision(mobilityDecisionEvaluator.LastDecision, out var mobilityDecision);
        var escapeReason = escapeGapCloserController.LastEscapeGapCloserSafety;
        var reengageReason = gapCloserController.LastGapCloserSafety;
        var showingEscapeDash = !useMobilityDecision &&
                                 !string.Equals(escapeReason, "not checked", StringComparison.Ordinal) &&
                                 !(escapeReason.Contains("current position safe", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(reengageReason, "not checked", StringComparison.Ordinal));
        var reason = useMobilityDecision
            ? mobilityDecision.RiskReason
            : showingEscapeDash ? escapeReason : reengageReason;
        var rawState = useMobilityDecision
            ? DashStateFromMobilityDecision(mobilityDecision)
            : DashStateFromReason(reason);

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

        var distanceToTarget = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var label = useMobilityDecision
            ? BuildMobilityOverlayLabel(mobilityDecision, state)
            : BuildDashOverlayLabel(showingEscapeDash, state);
        var readableReason = useMobilityDecision
            ? BuildMobilityOverlayReason(mobilityDecision, distanceToTarget)
            : BuildDashOverlayReason(showingEscapeDash, reason, distanceToTarget);
        var landingPos = useMobilityDecision
            ? mobilityDecision.Destination
            : showingEscapeDash ? escapeGapCloserController.LastSafeEscapeDestination : gapCloserController.LastSafeLandingPosition;
        var lineTarget = landingPos ?? target.Position;
        var landingMarker = state == DecisionOverlayState.Candidate && landingPos.HasValue
            ? new DecisionOverlayMarker(DecisionOverlayState.Future, landingPos.Value, 0.35f, "dash landing")
            : null;

        snapshot = new(
            DecisionOverlaySource.GapCloser,
            state,
            label,
            readableReason,
            50,
            [],
            [new(state, player.Position, lineTarget, label)],
            landingMarker != null
                ? [new(state, target.Position, target.HitboxRadius, "target"), landingMarker]
                : [new(state, target.Position, target.HitboxRadius, "target")]);

    }

    private static bool TryGetRecentMobilityDecision(MobilityDecisionDiagnostics decision, out MobilityDecisionDiagnostics recent)
    {
        recent = decision;
        return decision.State != MobilityDecisionState.NotChecked &&
               decision.TimestampUtc != DateTime.MinValue &&
               DateTime.UtcNow - decision.TimestampUtc <= TimeSpan.FromMilliseconds(1500);
    }

    private static DecisionOverlayState DashStateFromMobilityDecision(MobilityDecisionDiagnostics decision)
    {
        return decision.State switch
        {
            MobilityDecisionState.Used => DecisionOverlayState.Active,
            MobilityDecisionState.Candidate => DecisionOverlayState.Candidate,
            MobilityDecisionState.Rejected => DecisionOverlayState.Rejected,
            _ => DecisionOverlayState.Suppressed
        };
    }

    private static DecisionOverlayState DashStateFromReason(string reason)
    {
        return reason.Contains("used ", StringComparison.OrdinalIgnoreCase)
            ? DecisionOverlayState.Active
            : reason.Contains("current position safe", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("not in gap closer range", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("target within", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("animation lock", StringComparison.OrdinalIgnoreCase) ||
              reason.Contains("not checked", StringComparison.OrdinalIgnoreCase)
                ? DecisionOverlayState.Suppressed
                : reason.Contains("safe", StringComparison.OrdinalIgnoreCase) ||
                  reason.Contains("turning for", StringComparison.OrdinalIgnoreCase)
                    ? DecisionOverlayState.Candidate
                    : DecisionOverlayState.Rejected;
    }

    private static string BuildDashOverlayLabel(bool escapeDash, DecisionOverlayState state)
    {
        return escapeDash
            ? state switch
            {
                DecisionOverlayState.Active => "Safety gap closer used",
                DecisionOverlayState.Candidate => "Safety gap closer ready",
                DecisionOverlayState.Rejected => "Safety gap closer blocked",
                DecisionOverlayState.Suppressed => "Safety gap closer idle",
                _ => "Safety gap closer"
            }
            : state switch
            {
                DecisionOverlayState.Active => "Reengage gap closer used",
                DecisionOverlayState.Candidate => "Reengage gap closer ready",
                DecisionOverlayState.Rejected => "Reengage gap closer blocked",
                DecisionOverlayState.Suppressed => "Reengage gap closer idle",
                _ => "Reengage gap closer"
            };
    }

    private static string BuildDashOverlayReason(bool escapeDash, string reason, float distanceToTarget)
    {
        var prefix = escapeDash
            ? $"Safety dash; target {distanceToTarget:0.#}y away"
            : $"Target {distanceToTarget:0.#}y away";
        return $"{prefix}; {NormalizeDashReason(reason)}";
    }

    private static string BuildMobilityOverlayLabel(MobilityDecisionDiagnostics decision, DecisionOverlayState state)
    {
        var intent = decision.IntentLabel;
        return state switch
        {
            DecisionOverlayState.Active => $"Dash used: {intent}",
            DecisionOverlayState.Candidate => $"Dash ready: {intent}",
            DecisionOverlayState.Rejected => $"Dash blocked: {intent}",
            DecisionOverlayState.Suppressed => $"Dash idle: {intent}",
            _ => $"Dash: {intent}"
        };
    }

    private static string BuildMobilityOverlayReason(MobilityDecisionDiagnostics decision, float distanceToTarget)
    {
        var parts = new List<string>
        {
            $"{decision.ActionName}; target {distanceToTarget:0.#}y away"
        };

        if (decision.SafetyGain > 0.1f)
        {
            parts.Add($"safety +{decision.SafetyGain:0.0}y");
        }

        if (decision.UptimeGain > 0.1f)
        {
            parts.Add($"uptime +{decision.UptimeGain:0.0}");
        }

        if (decision.PathGain > 0.1f)
        {
            parts.Add($"path +{decision.PathGain:0.0}y");
        }

        parts.Add(NormalizeDashReason(decision.RiskReason));
        return string.Join("; ", parts.Take(4));
    }

    private static string NormalizeDashReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Equals("not checked", StringComparison.OrdinalIgnoreCase))
        {
            return "waiting for dash check";
        }

        if (reason.Contains("used ", StringComparison.OrdinalIgnoreCase))
        {
            return "dash was used";
        }

        if (reason.Contains("current position safe", StringComparison.OrdinalIgnoreCase))
        {
            return "already safe";
        }

        if (reason.Contains("target within", StringComparison.OrdinalIgnoreCase))
        {
            return "already in attack range";
        }

        if (reason.Contains("not in gap closer range", StringComparison.OrdinalIgnoreCase))
        {
            return "target is too far for a dash";
        }

        if (reason.Contains("under", StringComparison.OrdinalIgnoreCase))
        {
            return "destination is closer than the configured minimum";
        }

        if (reason.Contains("animation lock", StringComparison.OrdinalIgnoreCase))
        {
            return "animation locked";
        }

        if (reason.Contains("player casting", StringComparison.OrdinalIgnoreCase))
        {
            return "casting";
        }

        if (reason.Contains("action unavailable", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains(" unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "dash action unavailable";
        }

        if (reason.Contains("dangerous", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("unsafe", StringComparison.OrdinalIgnoreCase))
        {
            return "landing is unsafe";
        }

        if (reason.Contains("no safe", StringComparison.OrdinalIgnoreCase))
        {
            return "no safe landing found";
        }

        if (reason.Contains("could not calculate", StringComparison.OrdinalIgnoreCase))
        {
            return "could not calculate a landing point";
        }

        if (reason.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return "dash action failed";
        }

        return reason;
    }

    private void DrawSnapshot(ImDrawListPtr drawList, DecisionOverlaySnapshot snapshot)
    {
        foreach (var shape in snapshot.Shapes)
        {
            this.DrawShape(drawList, shape, snapshot.Source, ColorFor(shape.State, snapshot.Source));
            if (ShouldDrawShapeLabel(snapshot.Source, shape))
            {
                this.DrawLabel(drawList, ShapeLabelAnchor(shape), shape.Label!, ColorFor(shape.State, snapshot.Source), pixelYOffset: 10f);
            }
        }

        foreach (var line in snapshot.Lines)
        {
            this.DrawLine(drawList, line.From, line.To, ColorFor(line.State, snapshot.Source), thickness: snapshot.Source == DecisionOverlaySource.FinalMovement ? 3f : 2f, arrow: true);
            if (ShouldDrawLineLabel(snapshot.Source, line))
            {
                this.DrawLabel(drawList, Midpoint(line.From, line.To), line.Label!, ColorFor(line.State, snapshot.Source), pixelYOffset: 8f);
            }
        }

        foreach (var marker in snapshot.Markers)
        {
            this.DrawCircle(drawList, marker.Position, Math.Max(marker.Radius, 0.25f), ColorFor(marker.State, snapshot.Source), thickness: 2f);
            if (marker.Label != null)
            {
                this.DrawLabel(drawList, marker.Position, marker.Label, ColorFor(marker.State, snapshot.Source));
            }
        }
    }

    private void DrawShape(ImDrawListPtr drawList, DecisionOverlayShape shape, DecisionOverlaySource source, uint color)
    {
        var fillColor = FillColorFor(shape.State, source);
        switch (shape.Kind)
        {
            case DecisionOverlayShapeKind.Circle:
                if (ShouldFillShape(shape.State, source))
                {
                    this.DrawCircleFilled(drawList, shape.Origin, shape.Radius, fillColor);
                }
                this.DrawCircle(drawList, shape.Origin, shape.Radius, color, shape.State == DecisionOverlayState.Suppressed ? 1.5f : 2.5f);
                break;
            case DecisionOverlayShapeKind.Cone:
                if (ShouldFillShape(shape.State, source))
                {
                    this.DrawConeFilled(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, fillColor);
                }
                this.DrawCone(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, color);
                break;
            case DecisionOverlayShapeKind.Rectangle:
                if (ShouldFillShape(shape.State, source))
                {
                    this.DrawRectangleFilled(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, fillColor);
                }
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

    private void DrawCircleFilled(ImDrawListPtr drawList, Vector3 center, float radius, uint color)
    {
        const int Segments = 48;
        var points = new List<Vector2>(Segments);
        for (var i = 0; i < Segments; ++i)
        {
            var angle = i * MathF.Tau / Segments;
            var point = center + new Vector3(MathF.Sin(angle) * radius, 0f, MathF.Cos(angle) * radius);
            if (this.Project(point, out var screen))
            {
                points.Add(screen);
            }
        }

        if (points.Count >= 3)
        {
            DrawFilledPolygon(drawList, points, color);
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

    private void DrawConeFilled(ImDrawListPtr drawList, Vector3 center, float radius, float rotation, float halfWidth, uint color)
    {
        const int Segments = 24;
        var points = new List<Vector2>(Segments + 2);
        if (!this.Project(center, out var centerScreen))
        {
            return;
        }

        points.Add(centerScreen);
        for (var i = 0; i <= Segments; ++i)
        {
            var angle = rotation - halfWidth + 2f * halfWidth * i / Segments;
            var point = center + Direction(angle) * radius;
            if (this.Project(point, out var screen))
            {
                points.Add(screen);
            }
        }

        if (points.Count >= 3)
        {
            DrawFilledPolygon(drawList, points, color);
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

    private void DrawRectangleFilled(ImDrawListPtr drawList, Vector3 origin, float rotation, float length, float halfWidth, uint color)
    {
        var forward = Direction(rotation);
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var p1 = origin + side * halfWidth;
        var p2 = origin - side * halfWidth;
        var p3 = origin + forward * length - side * halfWidth;
        var p4 = origin + forward * length + side * halfWidth;
        if (this.Project(p1, out var s1) &&
            this.Project(p2, out var s2) &&
            this.Project(p3, out var s3) &&
            this.Project(p4, out var s4))
        {
            DrawFilledPolygon(drawList, [s1, s2, s3, s4], color);
        }
    }

    private static void DrawFilledPolygon(ImDrawListPtr drawList, IReadOnlyList<Vector2> points, uint color)
    {
        for (var i = 1; i + 1 < points.Count; ++i)
        {
            drawList.AddTriangleFilled(points[0], points[i], points[i + 1], color);
        }
    }

    private void DrawLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness, bool arrow = false)
    {
        if (this.Project(from, out var fromScreen) && this.Project(to, out var toScreen))
        {
            drawList.AddLine(fromScreen, toScreen, color, thickness);
            if (arrow)
            {
                DrawArrowHead(drawList, fromScreen, toScreen, color, MathF.Max(8f, thickness * 4f));
            }
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
        var padding = new Vector2(4f, 2f);
        drawList.AddRectFilled(
            pos - padding,
            pos + textSize + padding,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.58f)),
            4f);
        drawList.AddText(pos, color, label);
    }

    private void DrawLabelGroup(ImDrawListPtr drawList, Vector3 anchor, IReadOnlyList<OverlayLabel> labels)
    {
        if (labels.Count == 0 || !this.Project(anchor, out var anchorScreen))
        {
            return;
        }

        var lines = labels
            .OrderBy(label => LabelSortPriority(label.State))
            .ThenBy(label => label.Source)
            .ToArray();

        var padding = new Vector2(6f, 4f);
        var lineHeight = ImGui.GetTextLineHeight();
        var width = 0f;
        foreach (var label in lines)
        {
            width = MathF.Max(width, ImGui.CalcTextSize(label.Text).X);
        }

        var size = new Vector2(width + padding.X * 2f, lineHeight * lines.Length + padding.Y * 2f);
        var displaySize = ImGui.GetIO().DisplaySize;
        var pos = anchorScreen + new Vector2(14f, -size.Y - 12f);
        if (pos.X + size.X > displaySize.X - 8f)
        {
            pos.X = anchorScreen.X - size.X - 14f;
        }

        if (pos.Y < 8f)
        {
            pos.Y = anchorScreen.Y + 14f;
        }

        pos.X = Math.Clamp(pos.X, 8f, MathF.Max(8f, displaySize.X - size.X - 8f));
        pos.Y = Math.Clamp(pos.Y, 8f, MathF.Max(8f, displaySize.Y - size.Y - 8f));

        var bg = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.62f));
        var border = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f));
        drawList.AddLine(anchorScreen, new Vector2(pos.X, pos.Y + size.Y * 0.5f), border, 1f);
        drawList.AddRectFilled(pos, pos + size, bg, 5f);
        drawList.AddRect(pos, pos + size, border, 5f);

        var textPos = pos + padding;
        foreach (var label in lines)
        {
            drawList.AddText(textPos, ColorFor(label.State, label.Source), label.Text);
            textPos.Y += lineHeight;
        }
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

    private static Vector3 Midpoint(Vector3 from, Vector3 to)
    {
        return new Vector3((from.X + to.X) * 0.5f, (from.Y + to.Y) * 0.5f, (from.Z + to.Z) * 0.5f);
    }

    private static Vector3 ShapeLabelAnchor(DecisionOverlayShape shape)
    {
        return shape.Kind switch
        {
            DecisionOverlayShapeKind.Cone => shape.Origin + Direction(shape.RotationRadians) * MathF.Max(0.5f, shape.Radius * 0.65f),
            DecisionOverlayShapeKind.Rectangle => shape.Origin + Direction(shape.RotationRadians) * MathF.Max(0.5f, shape.Length * 0.5f),
            _ => shape.Origin + new Vector3(0f, 0f, MathF.Max(0.5f, shape.Radius * 0.65f))
        };
    }

    private static void DrawArrowHead(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float size)
    {
        var direction = to - from;
        if (direction.LengthSquared() <= 0.01f)
        {
            return;
        }

        direction = Vector2.Normalize(direction);
        var side = new Vector2(-direction.Y, direction.X);
        var tip = to;
        var basePoint = to - direction * size;
        drawList.AddTriangleFilled(tip, basePoint + side * (size * 0.45f), basePoint - side * (size * 0.45f), color);
    }

    private static string BuildSnapshotLabel(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.Source == DecisionOverlaySource.FinalMovement &&
            snapshot.State == DecisionOverlayState.Active &&
            !string.IsNullOrWhiteSpace(snapshot.Reason))
        {
            return $"{snapshot.Label}: {snapshot.Reason}";
        }

        return snapshot.State == DecisionOverlayState.Suppressed && snapshot.Reason != null
            ? $"{snapshot.Label} ({snapshot.Reason})"
            : snapshot.Label;
    }

    private static int LabelSortPriority(DecisionOverlayState state)
    {
        return state switch
        {
            DecisionOverlayState.Active => 0,
            DecisionOverlayState.Candidate => 1,
            DecisionOverlayState.Future => 2,
            DecisionOverlayState.Rejected => 3,
            DecisionOverlayState.Suppressed => 4,
            _ => 5
        };
    }

    private static bool ShouldFillShape(DecisionOverlayState state, DecisionOverlaySource source)
    {
        if (source is DecisionOverlaySource.NextAction or DecisionOverlaySource.HealerCoverage)
        {
            return false;
        }

        return state is DecisionOverlayState.Active or DecisionOverlayState.Candidate;
    }

    private static bool ShouldDrawShapeLabel(DecisionOverlaySource source, DecisionOverlayShape shape)
    {
        if (shape.Label == null || shape.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return source switch
        {
            DecisionOverlaySource.Positionals => false,
            DecisionOverlaySource.TargetUptime => false,
            DecisionOverlaySource.GapCloser => false,
            DecisionOverlaySource.NextAction => false,
            _ => true
        };
    }

    private static bool ShouldDrawLineLabel(DecisionOverlaySource source, DecisionOverlayLine line)
    {
        if (line.Label == null || line.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return source is DecisionOverlaySource.FinalMovement
            or DecisionOverlaySource.EscapeLanding
            or DecisionOverlaySource.AoE
            or DecisionOverlaySource.HealerCoverage
            or DecisionOverlaySource.PassageOfArms
            or DecisionOverlaySource.SurvivabilityZone
            or DecisionOverlaySource.RedMageMeleeCombo;
    }

    private static uint ColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        if (source == DecisionOverlaySource.FinalMovement && state == DecisionOverlayState.Active)
        {
            return ImGui.GetColorU32(new Vector4(0.95f, 1f, 1f, 1f));
        }

        var color = state switch
        {
            DecisionOverlayState.Active => ImGui.GetColorU32(new Vector4(0.25f, 0.82f, 0.38f, 1f)),
            DecisionOverlayState.Candidate => ImGui.GetColorU32(SourceAccent(source, 1f)),
            DecisionOverlayState.Future => ImGui.GetColorU32(new Vector4(1f, 0.86f, 0.25f, 1f)),
            DecisionOverlayState.Rejected => ImGui.GetColorU32(new Vector4(1f, 0.2f, 0.2f, 1f)),
            DecisionOverlayState.Suppressed => ImGui.GetColorU32(new Vector4(0.58f, 0.58f, 0.58f, 0.82f)),
            _ => ImGui.GetColorU32(new Vector4(0.55f, 0.55f, 0.55f, 1f))
        };
        return color;
    }

    private static uint FillColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        var alpha = state switch
        {
            DecisionOverlayState.Active => 0.11f,
            DecisionOverlayState.Candidate => 0.085f,
            DecisionOverlayState.Future => 0.025f,
            DecisionOverlayState.Rejected => 0.08f,
            DecisionOverlayState.Suppressed => 0.0f,
            _ => 0.08f
        };
        var color = state switch
        {
            DecisionOverlayState.Active => new Vector4(0.25f, 0.82f, 0.38f, alpha),
            DecisionOverlayState.Candidate => SourceAccent(source, alpha),
            DecisionOverlayState.Future => new Vector4(1f, 0.86f, 0.25f, alpha),
            DecisionOverlayState.Rejected => new Vector4(1f, 0.2f, 0.2f, alpha),
            DecisionOverlayState.Suppressed => new Vector4(0.58f, 0.58f, 0.58f, alpha),
            _ => new Vector4(0.55f, 0.55f, 0.55f, alpha)
        };
        return ImGui.GetColorU32(color);
    }

    private static Vector4 SourceAccent(DecisionOverlaySource source, float alpha)
    {
        return source switch
        {
            DecisionOverlaySource.AoE => new Vector4(1f, 0.55f, 0.18f, alpha),
            DecisionOverlaySource.HealerCoverage => new Vector4(0.35f, 0.95f, 0.48f, alpha),
            DecisionOverlaySource.SurvivabilityZone => new Vector4(0.35f, 0.95f, 0.48f, alpha),
            DecisionOverlaySource.PassageOfArms => new Vector4(1f, 0.88f, 0.25f, alpha),
            DecisionOverlaySource.Positionals => new Vector4(0.88f, 0.45f, 1f, alpha),
            DecisionOverlaySource.GapCloser => new Vector4(0.25f, 0.95f, 0.95f, alpha),
            DecisionOverlaySource.EscapeLanding => new Vector4(0.25f, 0.95f, 0.95f, alpha),
            DecisionOverlaySource.TargetUptime => new Vector4(0.35f, 0.68f, 1f, alpha),
            DecisionOverlaySource.NextAction => new Vector4(1f, 0.86f, 0.25f, alpha),
            DecisionOverlaySource.RedMageMeleeCombo => new Vector4(1f, 0.35f, 0.35f, alpha),
            DecisionOverlaySource.FinalMovement => new Vector4(0.95f, 1f, 1f, alpha),
            _ => new Vector4(0.35f, 0.68f, 1f, alpha)
        };
    }
}
