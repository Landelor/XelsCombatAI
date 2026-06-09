using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;

namespace XelsCombatAI.UI;

internal sealed class DecisionOverlayController(
    Configuration config,
    DalamudServices services,
    AoePackPositioningController aoePackPositioningController,
    PassageOfArmsPositioningController passageOfArmsPositioningController,
    HealerAoePositioningController healerAoePositioningController,
    PartyHealerRangePositioningController partyHealerRangePositioningController,
    SurvivabilityZonePositioningController survivabilityZonePositioningController,
    PictomancerStarryMusePositioningController pictomancerStarryMusePositioningController,
    BossCenterAvoidanceController bossCenterAvoidanceController,
    BossModReflectionSafety bossModSafety,
    MobilityDecisionEvaluator mobilityDecisionEvaluator,
    GapCloserController gapCloserController,
    EscapeGapCloserController escapeGapCloserController,
    RedMageMeleeComboController redMageMeleeComboController,
    Func<Positional> activePositional,
    Func<bool> trueNorthActive,
    Func<float> targetUptimeRange,
    Func<string> targetUptimeRangeSource,
    Func<string> targetUptimeRangeReason,
    Func<bool?> leylinesBetweenTheLines,
    Func<bool?> leylinesRetrace,
    Func<bool?> leylinesGoal,
    RotationSolverActionReflection rotationSolverActions)
{

    private DecisionOverlayState gapCloserDisplayedState = DecisionOverlayState.Suppressed;
    private DecisionOverlayState gapCloserPendingState = DecisionOverlayState.Suppressed;
    private DateTime gapCloserPendingStateAt = DateTime.MinValue;
    private DateTime nextDrawErrorLog = DateTime.MinValue;
    private static readonly TimeSpan GapCloserStateDebounce = TimeSpan.FromMilliseconds(400);
    private const int MaxHudLinesPerSection = 4;

    private sealed record OverlayBadge(DecisionOverlayState State, DecisionOverlaySource Source, string Text);
    private sealed record MovementHudLine(DecisionOverlayState State, DecisionOverlaySource Source, string Text);

    public void Draw()
    {
        try
        {
            if (!config.ShowDecisionOverlay)
            {
                return;
            }

            if (this.ShouldHideForGameUiState())
            {
                return;
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

    private bool ShouldHideForGameUiState()
    {
        return services.GameGui.GameUiHidden ||
               services.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
               services.Condition[ConditionFlag.WatchingCutscene78] ||
               services.Condition[ConditionFlag.WatchingCutscene] ||
               services.ClientState.IsGPosing;
    }

    private void DrawWorldDebug()
    {
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var snapshots = this.BuildSnapshots(player)
            .OrderBy(snapshot => snapshot.Priority)
            .ToArray();
        if (config.ShowDecisionOverlayHud)
        {
            this.DrawMovementStatusHud(snapshots);
        }

        var badgeGroups = new Dictionary<(int, int), (Vector3 Anchor, List<OverlayBadge> Badges)>();
        foreach (var snapshot in snapshots)
        {
            var badgeAnchor = BadgeAnchor(snapshot);
            if (badgeAnchor.HasValue && ShouldDrawWorldBadge(snapshot))
            {
                var key = ((int)MathF.Round(badgeAnchor.Value.X * 2f), (int)MathF.Round(badgeAnchor.Value.Z * 2f));
                if (!badgeGroups.TryGetValue(key, out var group))
                {
                    group = (badgeAnchor.Value, []);
                    badgeGroups[key] = group;
                }

                group.Badges.Add(new(snapshot.State, snapshot.Source, BuildSnapshotBadge(snapshot)));
            }

            this.DrawSnapshot(drawList, snapshot);
        }

        foreach (var group in badgeGroups.Values)
        {
            this.DrawBadgeGroup(drawList, group.Anchor, group.Badges);
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


        var positionalSnapshot = this.BuildPositionalSnapshot(player, target);
        if (positionalSnapshot != null)
        {
            yield return positionalSnapshot;
        }

        var targetUptimeSnapshot = this.BuildTargetUptimeSnapshot(player, target);
        if (targetUptimeSnapshot != null)
        {
            yield return targetUptimeSnapshot;
        }

        var leyLinesSnapshot = this.BuildLeyLinesSnapshot(player);
        if (leyLinesSnapshot != null)
        {
            yield return leyLinesSnapshot;
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
                    ? [new(DecisionOverlayState.Candidate, player.Position, healerCoverage.Center, "")]
                    : [],
                healerCoverage.Members
                    .Select(member => new DecisionOverlayMarker(coverageState, member, 0.35f, null))
                    .ToArray());
        }

        var partyHealerRange = partyHealerRangePositioningController.Overlay;
        if (partyHealerRange != null)
        {
            var partyHealerStatus = partyHealerRangePositioningController.Status;
            var partyHealerBlocked = partyHealerStatus.LastReason.Contains("too far", StringComparison.OrdinalIgnoreCase);
            var partyHealerState = !config.Enabled || !config.ManageMovement || !config.ManageHealerCoverageZone
                ? DecisionOverlayState.Suppressed
                : partyHealerRange.PlayerInRange
                    ? DecisionOverlayState.Active
                    : partyHealerRange.Injected
                        ? DecisionOverlayState.Candidate
                        : partyHealerBlocked
                            ? DecisionOverlayState.Rejected
                            : DecisionOverlayState.Future;
            var reason = partyHealerState == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageHealerCoverageZone, "Stay in healer range"))
                : partyHealerRange.PlayerInRange
                    ? $"{partyHealerRange.DistanceToHealer:0.0}y from {partyHealerRange.HealerName}"
                    : partyHealerBlocked
                        ? partyHealerStatus.LastReason
                    : $"{partyHealerRange.DistanceToEntry:0.0}y to healer range";
            yield return new(
                DecisionOverlaySource.PartyHealerRange,
                partyHealerState,
                partyHealerRange.PlayerInRange
                    ? "Inside healer range"
                    : partyHealerBlocked
                        ? "Healer range too far"
                        : "Move into healer range",
                reason,
                21,
                [new(DecisionOverlayShapeKind.Circle, partyHealerState, partyHealerRange.HealerPosition, partyHealerRange.Radius, 0f, 0f, 0f, "healer range")],
                partyHealerRange.PlayerInRange
                    ? []
                    : [new DecisionOverlayLine(partyHealerState, player.Position, partyHealerRange.PreferredEntryPosition, "")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, partyHealerRange.HealerPosition, 0.35f, "HEAL"),
                    new DecisionOverlayMarker(partyHealerState, partyHealerRange.PreferredEntryPosition, 0.28f, null)
                ]);
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
                    [new(DecisionOverlayState.Candidate, player.Position, centroid, "")],
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
                    [new(DecisionOverlayState.Candidate, player.Position, aoe.Candidate, "")],
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
                [new(DecisionOverlayState.Future, player.Position, suggestion.Candidate, "")],
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
                [new(state, player.Position, passage.PreferredPosition, "")],
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
                    : [new DecisionOverlayLine(state, player.Position, survZone.ZoneCenter, "")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, survZone.CasterPosition, 0.35f, survZone.ZoneName[..3]),
                    new DecisionOverlayMarker(state, survZone.ZoneCenter, 0.25f, "Zone")
                ]);
        }

        pictomancerStarryMusePositioningController.RefreshOverlay();
        var starryMuse = pictomancerStarryMusePositioningController.Overlay;
        if (starryMuse != null)
        {
            var state = !config.Enabled || !config.ManageMovement || !config.ManagePictomancerStarryMuse
                ? DecisionOverlayState.Suppressed
                : starryMuse.Injected
                ? starryMuse.PlayerInZone ? DecisionOverlayState.Active : DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
            var reason = state == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManagePictomancerStarryMuse, "Stay in Starry Muse"))
                : null;
            yield return new(
                DecisionOverlaySource.StarryMuse,
                state,
                starryMuse.PlayerInZone ? "Stay in Starry Muse" : "Move into Starry Muse",
                reason ?? "Pictomancer ground buff",
                43,
                [new(DecisionOverlayShapeKind.Circle, state, starryMuse.ZoneCenter, starryMuse.Radius, 0f, 0f, 0f, "Starry Muse")],
                starryMuse.PlayerInZone
                    ? []
                    : [new DecisionOverlayLine(state, player.Position, starryMuse.PreferredPosition, "")],
                [new DecisionOverlayMarker(state, starryMuse.PreferredPosition, 0.25f, "Starry")]);
        }

        var bossCenter = bossCenterAvoidanceController.Overlay;
        if (bossCenter != null)
        {
            var state = bossCenter.Injected ? DecisionOverlayState.Candidate : DecisionOverlayState.Future;
            yield return new(
                DecisionOverlaySource.BossCenterAvoidance,
                state,
                bossCenter.Injected ? "Move out of boss center" : "Avoid boss center",
                bossCenter.Reason,
                46,
                [new(DecisionOverlayShapeKind.Circle, state, bossCenter.TargetPosition, bossCenter.Radius, 0f, 0f, 0f, "boss center")],
                [],
                []);
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
                    [new(DecisionOverlayState.Future, player.Position, escapeDest.Value, "")],
                    [new(DecisionOverlayState.Future, escapeDest.Value, 0.35f, null)]);
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
                [new(DecisionOverlayState.Active, player.Position, destination, "")],
                [new(DecisionOverlayState.Active, destination, 0.35f, null)]);
        }
    }

    private DecisionOverlaySnapshot? BuildPositionalSnapshot(IBattleChara player, IBattleChara? target)
    {
        if (!config.Enabled ||
            !config.ManagePositionals ||
            trueNorthActive() ||
            target == null ||
            JobRoles.GetRangeRole(player) != RangeRole.Melee)
        {
            return null;
        }

        if (!rotationSolverActions.TryGetUpcomingGcdTiming(out var action, out var reason))
        {
            return null;
        }

        if (action.PrimaryTargetId != 0 && action.PrimaryTargetId != target.GameObjectId)
        {
            return null;
        }

        if (!PositionalTrueNorthPolicy.TryGetActionPositional(action, out var positional) ||
            !PositionalDashPolicy.IsActive(positional))
        {
            return null;
        }

        var satisfied = PositionalDashPolicy.IsSatisfied(positional, player.Position, target.Position, target.Rotation);
        var state = satisfied
            ? DecisionOverlayState.Active
            : activePositional() == positional
                ? DecisionOverlayState.Candidate
                : DecisionOverlayState.Future;
        var radius = MathF.Max(
            target.HitboxRadius + player.HitboxRadius + CombatConstants.MeleeActionRange,
            2.5f);
        return new(
            DecisionOverlaySource.Positionals,
            state,
            $"{positional}: {action.ActionName}",
            satisfied ? "already in positional" : reason,
            15,
            BuildPositionalShapes(positional, state, target.Position, target.Rotation, radius),
            [],
            []);
    }

    private DecisionOverlaySnapshot? BuildTargetUptimeSnapshot(IBattleChara player, IBattleChara? target)
    {
        if (!config.Enabled ||
            !config.ManageMovement ||
            target == null ||
            target.IsDead ||
            target.CurrentHp == 0)
        {
            return null;
        }

        var range = targetUptimeRange();
        if (range <= 0f || range >= Configuration.InternalDisabledUptimeRange - 0.5f)
        {
            return null;
        }

        var source = targetUptimeRangeSource();
        var reason = targetUptimeRangeReason();
        var surfaceDistance = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        var outOfRange = surfaceDistance > range + 0.2f;
        var noteworthyRange = !source.Equals("local", StringComparison.OrdinalIgnoreCase) &&
                              !source.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (!outOfRange && !noteworthyRange)
        {
            return null;
        }

        var state = outOfRange ? DecisionOverlayState.Candidate : DecisionOverlayState.Active;
        return new(
            DecisionOverlaySource.TargetUptime,
            state,
            outOfRange ? "Move into attack range" : "In attack range",
            $"{surfaceDistance:0.0}/{range:0.0}y - {NormalizeRangeReason(reason)}",
            22,
            [new(DecisionOverlayShapeKind.Circle, state, target.Position, target.HitboxRadius + range, 0f, 0f, 0f, "attack range")],
            outOfRange
                ? [new DecisionOverlayLine(state, player.Position, target.Position, "")]
                : [],
            []);
    }

    private DecisionOverlaySnapshot? BuildLeyLinesSnapshot(IBattleChara player)
    {
        if (!config.Enabled ||
            !config.ManageMovement ||
            !config.ManageLeylines ||
            !IsBlackMage(player))
        {
            return null;
        }

        var inLeyLines = HasStatus(player, ActionUse.CircleOfPowerStatusId);
        var hasLeyLines = HasStatus(player, ActionUse.LeyLinesStatusId);
        var leyLinesObject = this.TryFindLeyLinesObject(player);
        if (!inLeyLines && !hasLeyLines && leyLinesObject == null)
        {
            return null;
        }

        var btl = leylinesBetweenTheLines() == true;
        var retrace = leylinesRetrace() == true;
        var goal = leylinesGoal() == true;
        var state = inLeyLines ? DecisionOverlayState.Active : DecisionOverlayState.Candidate;
        var reason = BuildLeyLinesReason(btl, retrace, goal, leyLinesObject != null);
        if (leyLinesObject == null)
        {
            return new(
                DecisionOverlaySource.LeyLines,
                state,
                inLeyLines ? "In Ley Lines" : "Ley Lines active",
                reason,
                42,
                [],
                [],
                []);
        }

        return new(
            DecisionOverlaySource.LeyLines,
            state,
            inLeyLines ? "Stay in Ley Lines" : "Return to Ley Lines",
            reason,
            42,
            [new(DecisionOverlayShapeKind.Circle, state, leyLinesObject.Position, 3f, 0f, 0f, 0f, "Ley Lines")],
            inLeyLines
                ? []
                : [new DecisionOverlayLine(state, player.Position, leyLinesObject.Position, "")],
            [new DecisionOverlayMarker(state, leyLinesObject.Position, 0.32f, "LL")]);
    }

    private IGameObject? TryFindLeyLinesObject(IBattleChara player)
    {
        foreach (var obj in services.ObjectTable)
        {
            if (obj.BaseId == ActionUse.BlackMageLeyLinesObjectDataId &&
                obj.OwnerId == player.GameObjectId)
            {
                return obj;
            }
        }

        return null;
    }

    private static bool IsBlackMage(IBattleChara player)
        => player.ClassJob.RowId is 7 or 25;

    private static bool HasStatus(IBattleChara player, uint statusId)
        => player.StatusList.Any(status => status.StatusId == statusId && status.RemainingTime > 0f);

    private static string NormalizeRangeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Equals("not checked", StringComparison.OrdinalIgnoreCase))
        {
            return "desired attack range";
        }

        return reason;
    }

    private static string BuildLeyLinesReason(bool betweenTheLines, bool retrace, bool goal, bool objectVisible)
    {
        var parts = new List<string>(3);
        if (betweenTheLines)
        {
            parts.Add("Between the Lines");
        }

        if (retrace)
        {
            parts.Add("Retrace");
        }

        if (goal)
        {
            parts.Add("walk back");
        }

        if (!objectVisible)
        {
            parts.Add("zone not visible");
        }

        return parts.Count == 0 ? "Ley Lines handling enabled" : string.Join(", ", parts);
    }

    private void DrawMovementStatusHud(IReadOnlyList<DecisionOverlaySnapshot> snapshots)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(12f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.72f);
        var flags = ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoCollapse;
        if (!ImGui.Begin("Movement Overlay###XCAIMovementStatusOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Movement overlay");
        ImGui.Separator();

        if (!config.Enabled || !config.ManageMovement)
        {
            this.DrawMovementHudMuted(!config.Enabled ? "Disabled" : "Movement automation off");
            ImGui.End();
            return;
        }

        var now = this.BuildMovementHudLines(snapshots, future: false)
            .Take(MaxHudLinesPerSection)
            .ToArray();
        var next = this.BuildMovementHudLines(snapshots, future: true)
            .Take(MaxHudLinesPerSection)
            .ToArray();

        this.DrawMovementHudSection("Now", now, "No movement decision active");
        ImGui.Spacing();
        this.DrawMovementHudSection("Next", next, "Waiting for enabled option trigger");

        ImGui.End();
    }

    private IEnumerable<MovementHudLine> BuildMovementHudLines(IReadOnlyList<DecisionOverlaySnapshot> snapshots, bool future)
    {
        foreach (var snapshot in snapshots
                     .Where(snapshot => IsMovementHudSource(snapshot.Source))
                     .Where(snapshot => future ? snapshot.State == DecisionOverlayState.Future : snapshot.State is DecisionOverlayState.Active or DecisionOverlayState.Candidate or DecisionOverlayState.Rejected)
                     .OrderBy(snapshot => MovementHudStatePriority(snapshot.State))
                     .ThenBy(snapshot => snapshot.Priority))
        {
            yield return new(snapshot.State, snapshot.Source, FormatMovementHudText(snapshot));
        }
    }

    private void DrawMovementHudSection(string label, IReadOnlyList<MovementHudLine> lines, string emptyText)
    {
        ImGui.TextColored(new Vector4(0.75f, 0.75f, 1f, 1f), label);
        if (lines.Count == 0)
        {
            this.DrawMovementHudMuted(emptyText);
            return;
        }

        foreach (var line in lines)
        {
            ImGui.TextColored(ColorVectorFor(line.State, line.Source), $"{MovementHudStateLabel(line.State),-7}");
            ImGui.SameLine();
            ImGui.TextUnformatted(line.Text);
        }
    }

    private void DrawMovementHudMuted(string text)
    {
        ImGui.TextColored(new Vector4(0.58f, 0.58f, 0.58f, 1f), text);
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

    private static IReadOnlyList<DecisionOverlayShape> BuildPositionalShapes(
        Positional positional,
        DecisionOverlayState state,
        Vector3 targetPosition,
        float targetRotation,
        float radius)
    {
        const float PositionalHalfAngle = MathF.PI / 4f;

        return positional switch
        {
            Positional.Rear =>
            [
                new(DecisionOverlayShapeKind.Cone, state, targetPosition, radius, PositionalHalfAngle, radius, targetRotation + MathF.PI, null)
            ],
            Positional.Flank =>
            [
                new(DecisionOverlayShapeKind.Cone, state, targetPosition, radius, PositionalHalfAngle, radius, targetRotation + MathF.PI * 0.5f, null),
                new(DecisionOverlayShapeKind.Cone, state, targetPosition, radius, PositionalHalfAngle, radius, targetRotation - MathF.PI * 0.5f, null)
            ],
            Positional.Front =>
            [
                new(DecisionOverlayShapeKind.Cone, state, targetPosition, radius, PositionalHalfAngle, radius, targetRotation, null)
            ],
            _ => []
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
            ? new DecisionOverlayMarker(DecisionOverlayState.Future, landingPos.Value, 0.35f, null)
            : null;

        snapshot = new(
            DecisionOverlaySource.GapCloser,
            state,
            label,
            readableReason,
            50,
            [],
            [new(state, player.Position, lineTarget, "")],
            landingMarker != null
                ? [new(state, target.Position, target.HitboxRadius, null), landingMarker]
                : [new(state, target.Position, target.HitboxRadius, null)]);

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
        if (!ShouldDrawWorldSnapshot(snapshot))
        {
            return;
        }

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
            this.DrawLine(drawList, line.From, line.To, ColorFor(line.State, snapshot.Source), thickness: LineThickness(snapshot.Source, line.State), arrow: true);
            if (ShouldDrawLineLabel(snapshot.Source, line))
            {
                this.DrawLabel(drawList, Midpoint(line.From, line.To), line.Label!, ColorFor(line.State, snapshot.Source), pixelYOffset: 8f);
            }
        }

        foreach (var marker in snapshot.Markers)
        {
            var markerRadius = MarkerRadius(snapshot.Source, marker);
            var markerColor = ColorFor(marker.State, snapshot.Source);
            if (ShouldFillMarker(marker.State, snapshot.Source))
            {
                this.DrawCircleFilled(drawList, marker.Position, markerRadius, MarkerFillColorFor(marker.State, snapshot.Source));
            }

            this.DrawCircle(drawList, marker.Position, markerRadius, markerColor, thickness: MarkerThickness(snapshot.Source, marker.State));
            if (marker.Label != null)
            {
                this.DrawLabel(drawList, marker.Position, marker.Label, markerColor);
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
                this.DrawCircle(drawList, shape.Origin, shape.Radius, color, ShapeThickness(source, shape.State));
                break;
            case DecisionOverlayShapeKind.Cone:
                if (ShouldFillShape(shape.State, source))
                {
                    this.DrawConeFilled(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, fillColor);
                }
                if (source == DecisionOverlaySource.Positionals)
                {
                    this.DrawPositionalArc(drawList, shape, color);
                    break;
                }

                this.DrawCone(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, color, ShapeThickness(source, shape.State));
                break;
            case DecisionOverlayShapeKind.Rectangle:
                if (ShouldFillShape(shape.State, source))
                {
                    this.DrawRectangleFilled(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, fillColor);
                }
                this.DrawRectangle(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, color, ShapeThickness(source, shape.State));
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
                drawList.AddLine(previous.Value, screen, ShadowColor(), thickness + 2.5f);
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

    private void DrawCone(ImDrawListPtr drawList, Vector3 center, float radius, float rotation, float halfWidth, uint color, float thickness)
    {
        var left = center + Direction(rotation - halfWidth) * radius;
        var right = center + Direction(rotation + halfWidth) * radius;
        this.DrawLine(drawList, center, left, color, thickness);
        this.DrawLine(drawList, center, right, color, thickness);

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
                drawList.AddLine(previous.Value, screen, ShadowColor(), thickness + 2.5f);
                drawList.AddLine(previous.Value, screen, color, thickness);
            }

            previous = screen;
        }
    }

    private void DrawPositionalArc(ImDrawListPtr drawList, DecisionOverlayShape shape, uint color)
    {
        var thickness = shape.State switch
        {
            DecisionOverlayState.Active => 3.2f,
            DecisionOverlayState.Candidate => 3.6f,
            DecisionOverlayState.Future => 2.8f,
            _ => 2.2f
        };
        var start = shape.RotationRadians - shape.HalfWidth;
        var end = shape.RotationRadians + shape.HalfWidth;
        var dashed = shape.State == DecisionOverlayState.Future;
        this.DrawArc(drawList, shape.Origin, shape.Radius, start, end, color, thickness, dashed);
        this.DrawRadialTick(drawList, shape.Origin, shape.Radius, start, color, thickness);
        this.DrawRadialTick(drawList, shape.Origin, shape.Radius, end, color, thickness);
        if (shape.State is DecisionOverlayState.Candidate or DecisionOverlayState.Active)
        {
            this.DrawRadialTick(drawList, shape.Origin, shape.Radius, shape.RotationRadians, color, thickness);
        }
    }

    private void DrawArc(ImDrawListPtr drawList, Vector3 center, float radius, float start, float end, uint color, float thickness, bool dashed)
    {
        const int MinimumSegments = 8;
        var segments = Math.Max(MinimumSegments, (int)(MathF.Abs(end - start) / MathF.Tau * 96f));
        Vector2? previous = null;
        for (var i = 0; i <= segments; ++i)
        {
            var angle = start + (end - start) * i / segments;
            var point = center + Direction(angle) * radius;
            if (!this.Project(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous.HasValue && (!dashed || i % 3 != 1))
            {
                drawList.AddLine(previous.Value, screen, ShadowColor(), thickness + 2.8f);
                drawList.AddLine(previous.Value, screen, color, thickness);
            }

            previous = screen;
        }
    }

    private void DrawRadialTick(ImDrawListPtr drawList, Vector3 center, float radius, float angle, uint color, float thickness)
    {
        var direction = Direction(angle);
        var outer = center + direction * radius;
        var inner = center + direction * MathF.Max(0.1f, radius - 0.75f);
        this.DrawLine(drawList, inner, outer, color, thickness);
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

    private void DrawRectangle(ImDrawListPtr drawList, Vector3 origin, float rotation, float length, float halfWidth, uint color, float thickness)
    {
        var forward = Direction(rotation);
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var p1 = origin + side * halfWidth;
        var p2 = origin - side * halfWidth;
        var p3 = origin + forward * length - side * halfWidth;
        var p4 = origin + forward * length + side * halfWidth;
        this.DrawLine(drawList, p1, p2, color, thickness);
        this.DrawLine(drawList, p2, p3, color, thickness);
        this.DrawLine(drawList, p3, p4, color, thickness);
        this.DrawLine(drawList, p4, p1, color, thickness);
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
            drawList.AddLine(fromScreen, toScreen, ShadowColor(), thickness + 2.5f);
            drawList.AddLine(fromScreen, toScreen, color, thickness);
            if (arrow)
            {
                var arrowSize = MathF.Max(8f, thickness * 4f);
                DrawArrowHead(drawList, fromScreen, toScreen, ShadowColor(), arrowSize + 2f);
                DrawArrowHead(drawList, fromScreen, toScreen, color, arrowSize);
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

    private void DrawBadgeGroup(ImDrawListPtr drawList, Vector3 anchor, IReadOnlyList<OverlayBadge> badges)
    {
        if (badges.Count == 0 || !this.Project(anchor, out var anchorScreen))
        {
            return;
        }

        var visibleBadges = badges
            .Where(badge => badge.State != DecisionOverlayState.Suppressed)
            .OrderBy(badge => LabelSortPriority(badge.State))
            .ThenBy(badge => badge.Source)
            .Take(4)
            .ToArray();
        if (visibleBadges.Length == 0)
        {
            return;
        }

        var padding = new Vector2(5f, 3f);
        var badgeHeight = ImGui.GetTextLineHeight() + padding.Y * 2f;
        var gap = 4f;
        var widths = new float[visibleBadges.Length];
        var totalWidth = 0f;
        for (var i = 0; i < visibleBadges.Length; ++i)
        {
            widths[i] = ImGui.CalcTextSize(visibleBadges[i].Text).X + padding.X * 2f;
            totalWidth += widths[i];
            if (i > 0)
            {
                totalWidth += gap;
            }
        }

        var size = new Vector2(totalWidth, badgeHeight);
        var displaySize = ImGui.GetIO().DisplaySize;
        var pos = anchorScreen + new Vector2(-size.X * 0.5f, -size.Y - 18f);
        if (pos.X + size.X > displaySize.X - 8f)
        {
            pos.X = displaySize.X - size.X - 8f;
        }

        if (pos.Y < 8f)
        {
            pos.Y = anchorScreen.Y + 14f;
        }

        pos.X = Math.Clamp(pos.X, 8f, MathF.Max(8f, displaySize.X - size.X - 8f));
        pos.Y = Math.Clamp(pos.Y, 8f, MathF.Max(8f, displaySize.Y - size.Y - 8f));

        var bg = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.50f));
        var border = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f));
        drawList.AddLine(anchorScreen, new Vector2(pos.X, pos.Y + size.Y * 0.5f), border, 1f);
        var cursor = pos;
        for (var i = 0; i < visibleBadges.Length; ++i)
        {
            var badge = visibleBadges[i];
            var badgeSize = new Vector2(widths[i], badgeHeight);
            var fill = BadgeFillColorFor(badge.State, badge.Source);
            var textColor = BadgeTextColorFor(badge.State, badge.Source);
            drawList.AddRectFilled(cursor, cursor + badgeSize, bg, 4f);
            drawList.AddRectFilled(cursor, cursor + new Vector2(4f, badgeSize.Y), fill, 4f);
            drawList.AddRect(cursor, cursor + badgeSize, border, 4f);
            drawList.AddText(cursor + padding, textColor, badge.Text);
            cursor.X += badgeSize.X + gap;
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

    private static Vector3? BadgeAnchor(DecisionOverlaySnapshot snapshot)
    {
        return snapshot.Markers.FirstOrDefault(marker => marker.State != DecisionOverlayState.Suppressed)?.Position ??
               snapshot.Lines.FirstOrDefault(line => line.State != DecisionOverlayState.Suppressed)?.To ??
               snapshot.Shapes.FirstOrDefault(shape => shape.State != DecisionOverlayState.Suppressed)?.Origin;
    }

    private static bool ShouldDrawWorldBadge(DecisionOverlaySnapshot snapshot)
    {
        return ShouldDrawWorldSnapshot(snapshot) &&
               snapshot.Source is DecisionOverlaySource.AoE
                   or DecisionOverlaySource.EscapeLanding
                   or DecisionOverlaySource.FinalMovement
                   or DecisionOverlaySource.GapCloser
                   or DecisionOverlaySource.HealerCoverage
                   or DecisionOverlaySource.LeyLines
                   or DecisionOverlaySource.NextAction
                   or DecisionOverlaySource.PassageOfArms
                   or DecisionOverlaySource.PartyHealerRange
                   or DecisionOverlaySource.Positionals
                   or DecisionOverlaySource.RedMageMeleeCombo
                   or DecisionOverlaySource.StarryMuse
                   or DecisionOverlaySource.SurvivabilityZone
                   or DecisionOverlaySource.TargetUptime
                   or DecisionOverlaySource.BossCenterAvoidance;
    }

    private static bool ShouldDrawWorldSnapshot(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return snapshot.Source != DecisionOverlaySource.GapCloser ||
               snapshot.State is DecisionOverlayState.Active or DecisionOverlayState.Candidate;
    }

    private static string BuildSnapshotBadge(DecisionOverlaySnapshot snapshot)
    {
        return snapshot.Source switch
        {
            DecisionOverlaySource.AoE when snapshot.State == DecisionOverlayState.Future => "AOE?",
            DecisionOverlaySource.AoE => "AOE",
            DecisionOverlaySource.EscapeLanding => "SAFE",
            DecisionOverlaySource.FinalMovement => "SAFE",
            DecisionOverlaySource.GapCloser => "DASH",
            DecisionOverlaySource.HealerCoverage => "HEAL",
            DecisionOverlaySource.PartyHealerRange => "HEAL",
            DecisionOverlaySource.LeyLines => "LL",
            DecisionOverlaySource.TargetUptime => "RANGE",
            DecisionOverlaySource.BossCenterAvoidance => "CENTER",
            DecisionOverlaySource.NextAction => BuildNextActionBadge(snapshot.Label),
            DecisionOverlaySource.PassageOfArms => "WINGS",
            DecisionOverlaySource.Positionals => snapshot.Label.StartsWith("Rear", StringComparison.OrdinalIgnoreCase)
                ? "REAR"
                : snapshot.Label.StartsWith("Flank", StringComparison.OrdinalIgnoreCase)
                    ? "FLANK"
                    : "FRONT",
            DecisionOverlaySource.RedMageMeleeCombo => "RDM",
            DecisionOverlaySource.StarryMuse => "STAR",
            DecisionOverlaySource.SurvivabilityZone => "ZONE",
            _ => snapshot.Source.ToString()
        };
    }

    private static string BuildNextActionBadge(string label)
    {
        const string Prefix = "Next cast: ";
        var actionName = label.StartsWith(Prefix, StringComparison.Ordinal)
            ? label[Prefix.Length..]
            : label;
        const int MaxLength = 22;
        return actionName.Length <= MaxLength
            ? actionName
            : string.Concat(actionName.AsSpan(0, MaxLength - 3), "...");
    }

    private static string FormatMovementHudText(DecisionOverlaySnapshot snapshot)
    {
        var sourceLabel = MovementHudSourceLabel(snapshot.Source);
        var text = snapshot.Label.StartsWith(sourceLabel, StringComparison.OrdinalIgnoreCase)
            ? snapshot.Label
            : $"{sourceLabel}: {snapshot.Label}";
        var reason = NormalizeMovementHudReason(snapshot.Reason);
        if (reason != null &&
            !text.Contains(reason, StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text} - {reason}";
        }

        return TruncateMovementHudText(text);
    }

    private static string? NormalizeMovementHudReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var trimmed = reason.Trim();
        if (trimmed.Equals("not evaluated", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("not active in combat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("movement management disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static string TruncateMovementHudText(string text)
    {
        const int MaxLength = 78;
        return text.Length <= MaxLength
            ? text
            : string.Concat(text.AsSpan(0, MaxLength - 3), "...");
    }

    private static string MovementHudSourceLabel(DecisionOverlaySource source)
    {
        return source switch
        {
            DecisionOverlaySource.AoE => "AoE",
            DecisionOverlaySource.HealerCoverage => "Healer",
            DecisionOverlaySource.PartyHealerRange => "Healer",
            DecisionOverlaySource.GapCloser => "Dash",
            DecisionOverlaySource.EscapeLanding => "Dash",
            DecisionOverlaySource.FinalMovement => "Safety",
            DecisionOverlaySource.LeyLines => "Ley Lines",
            DecisionOverlaySource.TargetUptime => "Range",
            DecisionOverlaySource.BossCenterAvoidance => "Center",
            DecisionOverlaySource.NextAction => "Action",
            DecisionOverlaySource.PassageOfArms => "Passage",
            DecisionOverlaySource.Positionals => "Positional",
            DecisionOverlaySource.RedMageMeleeCombo => "RDM",
            DecisionOverlaySource.StarryMuse => "Starry",
            DecisionOverlaySource.SurvivabilityZone => "Defensive",
            _ => source.ToString()
        };
    }

    private static string MovementHudStateLabel(DecisionOverlayState state)
    {
        return state switch
        {
            DecisionOverlayState.Active => "now",
            DecisionOverlayState.Candidate => "move",
            DecisionOverlayState.Future => "next",
            DecisionOverlayState.Rejected => "blocked",
            _ => "idle"
        };
    }

    private static int MovementHudStatePriority(DecisionOverlayState state)
    {
        return state switch
        {
            DecisionOverlayState.Active => 0,
            DecisionOverlayState.Candidate => 1,
            DecisionOverlayState.Future => 2,
            DecisionOverlayState.Rejected => 3,
            _ => 4
        };
    }

    private static bool IsMovementHudSource(DecisionOverlaySource source)
    {
        return source is DecisionOverlaySource.AoE
            or DecisionOverlaySource.EscapeLanding
            or DecisionOverlaySource.FinalMovement
            or DecisionOverlaySource.GapCloser
            or DecisionOverlaySource.HealerCoverage
            or DecisionOverlaySource.LeyLines
            or DecisionOverlaySource.NextAction
            or DecisionOverlaySource.PassageOfArms
            or DecisionOverlaySource.PartyHealerRange
            or DecisionOverlaySource.Positionals
            or DecisionOverlaySource.RedMageMeleeCombo
            or DecisionOverlaySource.StarryMuse
            or DecisionOverlaySource.SurvivabilityZone
            or DecisionOverlaySource.TargetUptime
            or DecisionOverlaySource.BossCenterAvoidance;
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
        _ = state;
        _ = source;
        return false;
    }

    private static bool ShouldDrawShapeLabel(DecisionOverlaySource source, DecisionOverlayShape shape)
    {
        if (string.IsNullOrWhiteSpace(shape.Label) || shape.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return source switch
        {
            DecisionOverlaySource.Positionals => false,
            DecisionOverlaySource.TargetUptime => false,
            DecisionOverlaySource.BossCenterAvoidance => false,
            DecisionOverlaySource.GapCloser => false,
            DecisionOverlaySource.NextAction => false,
            DecisionOverlaySource.AoE => false,
            DecisionOverlaySource.HealerCoverage => false,
            DecisionOverlaySource.PartyHealerRange => false,
            DecisionOverlaySource.LeyLines => false,
            _ => true
        };
    }

    private static bool ShouldDrawLineLabel(DecisionOverlaySource source, DecisionOverlayLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Label) || line.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return false;
    }

    private static float LineThickness(DecisionOverlaySource source, DecisionOverlayState state)
    {
        if (source == DecisionOverlaySource.FinalMovement)
        {
            return 4.5f;
        }

        return state switch
        {
            DecisionOverlayState.Active => 3.4f,
            DecisionOverlayState.Candidate => 3.6f,
            DecisionOverlayState.Future => 2.1f,
            DecisionOverlayState.Rejected => 2.2f,
            _ => 1.5f
        };
    }

    private static float ShapeThickness(DecisionOverlaySource source, DecisionOverlayState state)
    {
        if (source == DecisionOverlaySource.FinalMovement)
        {
            return 4f;
        }

        return state switch
        {
            DecisionOverlayState.Active => 3.2f,
            DecisionOverlayState.Candidate => 3.4f,
            DecisionOverlayState.Future => 1.9f,
            DecisionOverlayState.Rejected => 2.4f,
            DecisionOverlayState.Suppressed => 1.4f,
            _ => 2f
        };
    }

    private static float MarkerRadius(DecisionOverlaySource source, DecisionOverlayMarker marker)
    {
        var radius = Math.Max(marker.Radius, 0.25f);
        return source switch
        {
            DecisionOverlaySource.FinalMovement => MathF.Max(radius, 0.55f),
            DecisionOverlaySource.EscapeLanding => MathF.Max(radius, 0.5f),
            DecisionOverlaySource.GapCloser when marker.State == DecisionOverlayState.Future => MathF.Max(radius, 0.45f),
            DecisionOverlaySource.Positionals => MathF.Max(radius, 0.45f),
            DecisionOverlaySource.PassageOfArms => MathF.Max(radius, 0.4f),
            DecisionOverlaySource.SurvivabilityZone => MathF.Max(radius, 0.4f),
            DecisionOverlaySource.StarryMuse => MathF.Max(radius, 0.4f),
            DecisionOverlaySource.PartyHealerRange => MathF.Max(radius, 0.4f),
            DecisionOverlaySource.LeyLines => MathF.Max(radius, 0.4f),
            _ => radius
        };
    }

    private static bool ShouldFillMarker(DecisionOverlayState state, DecisionOverlaySource source)
    {
        return source is (DecisionOverlaySource.FinalMovement
                   or DecisionOverlaySource.EscapeLanding
                   or DecisionOverlaySource.GapCloser
                   or DecisionOverlaySource.PassageOfArms
                   or DecisionOverlaySource.Positionals
                   or DecisionOverlaySource.SurvivabilityZone
                   or DecisionOverlaySource.StarryMuse
                   or DecisionOverlaySource.PartyHealerRange
                   or DecisionOverlaySource.LeyLines) &&
               state is DecisionOverlayState.Active or DecisionOverlayState.Candidate or DecisionOverlayState.Future;
    }

    private static float MarkerThickness(DecisionOverlaySource source, DecisionOverlayState state)
    {
        if (source is DecisionOverlaySource.FinalMovement or DecisionOverlaySource.EscapeLanding)
        {
            return 3f;
        }

        return state == DecisionOverlayState.Suppressed ? 1.5f : 2.2f;
    }

    private static uint MarkerFillColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        var color = ColorVectorFor(state, source);
        color.W = 0.18f;
        return ImGui.GetColorU32(color);
    }

    private static uint BadgeFillColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        return ImGui.GetColorU32(ColorVectorFor(state, source));
    }

    private static uint BadgeTextColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        _ = state;
        _ = source;
        return ImGui.GetColorU32(new Vector4(0.96f, 0.96f, 0.96f, 1f));
    }

    private static uint ShadowColor()
    {
        return ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.68f));
    }

    private static uint ColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        return ImGui.GetColorU32(ColorVectorFor(state, source));
    }

    private static Vector4 ColorVectorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        if (source == DecisionOverlaySource.Positionals)
        {
            return state switch
            {
                DecisionOverlayState.Active => new Vector4(0.20f, 0.95f, 0.35f, 1f),
                DecisionOverlayState.Candidate => new Vector4(1f, 0.18f, 0.12f, 1f),
                DecisionOverlayState.Future => new Vector4(1f, 0.56f, 0.12f, 1f),
                _ => new Vector4(0.58f, 0.58f, 0.58f, 0.82f)
            };
        }

        if (source == DecisionOverlaySource.FinalMovement && state == DecisionOverlayState.Active)
        {
            return new Vector4(0.95f, 1f, 1f, 1f);
        }

        return state switch
        {
            DecisionOverlayState.Active => new Vector4(0.25f, 0.82f, 0.38f, 1f),
            DecisionOverlayState.Candidate => SourceAccent(source, 1f),
            DecisionOverlayState.Future => new Vector4(1f, 0.86f, 0.25f, 1f),
            DecisionOverlayState.Rejected => new Vector4(1f, 0.2f, 0.2f, 1f),
            DecisionOverlayState.Suppressed => new Vector4(0.58f, 0.58f, 0.58f, 0.82f),
            _ => new Vector4(0.55f, 0.55f, 0.55f, 1f)
        };
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
        if (source == DecisionOverlaySource.Positionals)
        {
            alpha = state switch
            {
                DecisionOverlayState.Active => 0.10f,
                DecisionOverlayState.Candidate => 0.12f,
                DecisionOverlayState.Future => 0.07f,
                _ => alpha
            };
        }

        var color = ColorVectorFor(state, source);
        color.W = alpha;
        return ImGui.GetColorU32(color);
    }

    private static Vector4 SourceAccent(DecisionOverlaySource source, float alpha)
    {
        return source switch
        {
            DecisionOverlaySource.AoE => new Vector4(1f, 0.55f, 0.18f, alpha),
            DecisionOverlaySource.HealerCoverage => new Vector4(0.35f, 0.95f, 0.48f, alpha),
            DecisionOverlaySource.PartyHealerRange => new Vector4(0.30f, 0.82f, 0.95f, alpha),
            DecisionOverlaySource.SurvivabilityZone => new Vector4(0.35f, 0.95f, 0.48f, alpha),
            DecisionOverlaySource.PassageOfArms => new Vector4(1f, 0.88f, 0.25f, alpha),
            DecisionOverlaySource.Positionals => new Vector4(0.88f, 0.45f, 1f, alpha),
            DecisionOverlaySource.GapCloser => new Vector4(0.25f, 0.95f, 0.95f, alpha),
            DecisionOverlaySource.EscapeLanding => new Vector4(0.25f, 0.95f, 0.95f, alpha),
            DecisionOverlaySource.TargetUptime => new Vector4(0.35f, 0.68f, 1f, alpha),
            DecisionOverlaySource.LeyLines => new Vector4(0.72f, 0.52f, 1f, alpha),
            DecisionOverlaySource.BossCenterAvoidance => new Vector4(1f, 0.30f, 0.18f, alpha),
            DecisionOverlaySource.NextAction => new Vector4(1f, 0.86f, 0.25f, alpha),
            DecisionOverlaySource.RedMageMeleeCombo => new Vector4(1f, 0.35f, 0.35f, alpha),
            DecisionOverlaySource.FinalMovement => new Vector4(0.95f, 1f, 1f, alpha),
            _ => new Vector4(0.35f, 0.68f, 1f, alpha)
        };
    }
}
