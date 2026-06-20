using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    BlackMageLeyLinesPositioningController blackMageLeyLinesPositioningController,
    PictomancerStarryMusePositioningController pictomancerStarryMusePositioningController,
    BossCenterAvoidanceController bossCenterAvoidanceController,
    TankBehaviorController tankBehaviorController,
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
    RotationSolverActionReflection rotationSolverActions)
{

    private DecisionOverlayState gapCloserDisplayedState = DecisionOverlayState.Suppressed;
    private DecisionOverlayState gapCloserPendingState = DecisionOverlayState.Suppressed;
    private DateTime gapCloserPendingStateAt = DateTime.MinValue;
    private DateTime nextDrawErrorLog = DateTime.MinValue;
    private readonly Dictionary<DecisionOverlaySource, OverlaySnapshotState> snapshotStates = [];
    private static readonly TimeSpan GapCloserStateDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan VisualFadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan SnapshotHoldDuration = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan SnapshotFadeOutDuration = TimeSpan.FromMilliseconds(450);

    private enum OverlayVisualKind
    {
        ActiveDestination,
        PreviewDestination,
        MovementPath,
        ZoneRing,
        ActionFootprint,
        PositionalArc,
        BlockedMarker,
        ContextMarker,
        CompactCallout
    }

    private enum OverlayVisualProminence
    {
        Passive,
        Preview,
        Active,
        Blocked
    }

    private sealed class OverlaySnapshotState
    {
        public DecisionOverlaySnapshot? Snapshot { get; set; }

        public float Alpha { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    private sealed record OverlayPresentation(
        IReadOnlyList<OverlayVisual> Visuals,
        IReadOnlyList<DecisionOverlaySnapshot> BadgeSnapshots,
        IReadOnlyList<DecisionOverlaySnapshot> CalloutSnapshots);

    private sealed record OverlayVisual(
        OverlayVisualKind Kind,
        OverlayVisualProminence Prominence,
        DecisionOverlaySource Source,
        DecisionOverlayState State,
        Vector3 Anchor,
        DecisionOverlayShape? Shape,
        DecisionOverlayLine? Line,
        DecisionOverlayMarker? Marker,
        string? Label,
        int Rank,
        float Alpha);

    private sealed record OverlayBadge(DecisionOverlayState State, DecisionOverlaySource Source, string Text);

    private sealed record OverlayCallout(
        DecisionOverlayState State,
        DecisionOverlaySource Source,
        Vector3 Anchor,
        string Title,
        string Detail,
        int Rank);

    public void Draw()
    {
        try
        {
            if (!config.ShowDecisionOverlay)
            {
                return;
            }

            if (PluginUiVisibility.ShouldHide(services))
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
        var presentation = this.BuildPresentation(snapshots);

        var badgeGroups = new Dictionary<(int, int), (Vector3 Anchor, List<OverlayBadge> Badges)>();
        foreach (var visual in presentation.Visuals)
        {
            this.DrawVisual(drawList, visual);
        }

        foreach (var snapshot in presentation.BadgeSnapshots)
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

        }

        foreach (var group in badgeGroups.Values)
        {
            this.DrawBadgeGroup(drawList, group.Anchor, group.Badges);
        }

        var callouts = BuildCallouts(presentation.CalloutSnapshots).Take(config.DecisionOverlayDensity == OverlayDensity.Detailed ? 2 : 1).ToArray();
        for (var i = 0; i < callouts.Length; ++i)
        {
            this.DrawCallout(drawList, callouts[i], i);
        }
    }

    private OverlayPresentation BuildPresentation(IReadOnlyList<DecisionOverlaySnapshot> snapshots)
    {
        var visible = this.ApplySnapshotStability(snapshots
            .Where(ShouldDrawWorldSnapshot)
            .OrderBy(snapshot => PresentationRank(snapshot))
            .ToArray());
        var active = SelectProminentActive(visible);
        var preview = SelectProminentPreview(visible);
        var passiveContext = visible
            .Where(snapshot => snapshot != active && snapshot != preview && IsPassiveSnapshot(snapshot))
            .OrderBy(snapshot => PresentationRank(snapshot))
            .Take(config.DecisionOverlayDensity == OverlayDensity.Detailed ? 12 : 4)
            .ToArray();

        var selected = config.DecisionOverlayDensity switch
        {
            OverlayDensity.Minimal => active != null ? [active] : [],
            OverlayDensity.Normal => CombineSnapshots(active, preview, passiveContext),
            OverlayDensity.Detailed => visible,
            _ => CombineSnapshots(active, preview, passiveContext)
        };

        var visuals = selected
            .SelectMany(snapshot => this.BuildVisuals(snapshot, active == snapshot || preview == snapshot))
            .OrderBy(visual => visual.Rank)
            .ThenBy(visual => visual.Kind)
            .ToArray();
        var calloutSnapshots = config.DecisionOverlayDensity switch
        {
            OverlayDensity.Minimal => active != null ? [active] : [],
            OverlayDensity.Normal => active != null
                ? [active]
                : preview != null
                    ? [preview]
                    : passiveContext.Take(1).ToArray(),
            OverlayDensity.Detailed => selected,
            _ => []
        };
        var badgeSnapshots = config.DecisionOverlayDensity switch
        {
            OverlayDensity.Minimal => [],
            OverlayDensity.Normal => selected
                .Where(snapshot => !calloutSnapshots.Contains(snapshot) &&
                                   (snapshot == active || snapshot == preview || IsPassiveSnapshot(snapshot)))
                .OrderBy(snapshot => PresentationRank(snapshot))
                .Take(6)
                .ToArray(),
            OverlayDensity.Detailed => selected
                .Where(snapshot => !calloutSnapshots.Contains(snapshot))
                .ToArray(),
            _ => []
        };

        return new OverlayPresentation(visuals, badgeSnapshots, calloutSnapshots);
    }

    private IReadOnlyList<DecisionOverlaySnapshot> ApplySnapshotStability(IReadOnlyList<DecisionOverlaySnapshot> liveSnapshots)
    {
        var now = DateTime.UtcNow;
        var liveSources = new HashSet<DecisionOverlaySource>();
        foreach (var snapshot in liveSnapshots)
        {
            liveSources.Add(snapshot.Source);
            if (!this.snapshotStates.TryGetValue(snapshot.Source, out var state))
            {
                state = new OverlaySnapshotState();
                this.snapshotStates[snapshot.Source] = state;
            }

            if (state.LastSeenUtc == DateTime.MinValue)
            {
                state.Alpha = 0.45f;
            }
            else
            {
                var elapsed = MathF.Max(0f, (float)(now - state.LastSeenUtc).TotalSeconds);
                state.Alpha = Math.Clamp(state.Alpha + elapsed / (float)VisualFadeDuration.TotalSeconds, 0.45f, 1f);
            }

            state.Snapshot = snapshot;
            state.LastSeenUtc = now;
        }

        foreach (var entry in this.snapshotStates.ToArray())
        {
            if (liveSources.Contains(entry.Key))
            {
                continue;
            }

            var state = entry.Value;
            if (state.Snapshot == null || state.LastSeenUtc == DateTime.MinValue)
            {
                this.snapshotStates.Remove(entry.Key);
                continue;
            }

            var age = now - state.LastSeenUtc;
            if (age > SnapshotHoldDuration + SnapshotFadeOutDuration)
            {
                this.snapshotStates.Remove(entry.Key);
                continue;
            }

            if (age > SnapshotHoldDuration)
            {
                var fadeAge = (float)(age - SnapshotHoldDuration).TotalSeconds;
                state.Alpha = Math.Clamp(1f - fadeAge / (float)SnapshotFadeOutDuration.TotalSeconds, 0f, 1f);
            }
        }

        return this.snapshotStates.Values
            .Where(state => state.Snapshot != null && state.Alpha > 0.05f)
            .Select(state => state.Snapshot!)
            .OrderBy(snapshot => PresentationRank(snapshot))
            .ToArray();
    }

    private static IReadOnlyList<DecisionOverlaySnapshot> CombineSnapshots(
        DecisionOverlaySnapshot? active,
        DecisionOverlaySnapshot? preview,
        IReadOnlyList<DecisionOverlaySnapshot> passiveContext)
    {
        var result = new List<DecisionOverlaySnapshot>(2 + passiveContext.Count);
        if (active != null)
        {
            result.Add(active);
        }

        if (preview != null && preview != active)
        {
            result.Add(preview);
        }

        foreach (var snapshot in passiveContext)
        {
            if (!result.Contains(snapshot))
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    private static DecisionOverlaySnapshot? SelectProminentActive(IReadOnlyList<DecisionOverlaySnapshot> snapshots)
    {
        return snapshots
            .Where(snapshot => snapshot.State != DecisionOverlayState.Future && !IsPassiveSnapshot(snapshot))
            .OrderBy(snapshot => PresentationRank(snapshot))
            .ThenBy(snapshot => snapshot.Source)
            .FirstOrDefault();
    }

    private static DecisionOverlaySnapshot? SelectProminentPreview(IReadOnlyList<DecisionOverlaySnapshot> snapshots)
    {
        return snapshots
            .Where(snapshot => snapshot.State == DecisionOverlayState.Future)
            .OrderBy(snapshot => PresentationRank(snapshot))
            .ThenBy(snapshot => snapshot.Source)
            .FirstOrDefault();
    }

    private IEnumerable<OverlayVisual> BuildVisuals(DecisionOverlaySnapshot snapshot, bool prominent)
    {
        var alpha = this.SnapshotAlpha(snapshot.Source);
        var prominence = VisualProminence(snapshot);
        var rank = PresentationRank(snapshot);
        var passiveAlpha = IsPassiveSnapshot(snapshot) ? 0.42f : 1f;
        var effectiveAlpha = alpha * passiveAlpha;
        var drawPassiveShapes = !IsPassiveSnapshot(snapshot) ||
                                prominent ||
                                config.DecisionOverlayDensity == OverlayDensity.Detailed;

        foreach (var shape in snapshot.Shapes.Where(shape => drawPassiveShapes && shape.State != DecisionOverlayState.Suppressed))
        {
            yield return new(
                VisualKindForShape(snapshot.Source, shape),
                prominence,
                snapshot.Source,
                shape.State,
                ShapeLabelAnchor(shape),
                shape,
                null,
                null,
                CompactLabel(snapshot),
                rank + ShapeRankOffset(snapshot.Source),
                effectiveAlpha);
        }

        foreach (var line in snapshot.Lines.Where(line => line.State != DecisionOverlayState.Suppressed))
        {
            yield return new(
                OverlayVisualKind.MovementPath,
                prominence,
                snapshot.Source,
                line.State,
                line.To,
                null,
                line,
                null,
                null,
                rank,
                effectiveAlpha);
        }

        var destination = DestinationAnchor(snapshot);
        if (destination.HasValue && prominent && snapshot.State != DecisionOverlayState.Rejected)
        {
            yield return new(
                snapshot.State == DecisionOverlayState.Future ? OverlayVisualKind.PreviewDestination : OverlayVisualKind.ActiveDestination,
                prominence,
                snapshot.Source,
                snapshot.State,
                destination.Value,
                null,
                null,
                null,
                CompactLabel(snapshot),
                rank - 2,
                alpha);
        }

        if (snapshot.State == DecisionOverlayState.Rejected)
        {
            var blockedAnchor = destination ?? CalloutAnchor(snapshot);
            if (blockedAnchor.HasValue)
            {
                yield return new(
                    OverlayVisualKind.BlockedMarker,
                    OverlayVisualProminence.Blocked,
                    snapshot.Source,
                    snapshot.State,
                    blockedAnchor.Value,
                    null,
                    null,
                    null,
                    null,
                    rank - 1,
                    alpha);
            }
        }

        if (config.DecisionOverlayDensity == OverlayDensity.Detailed || prominent)
        {
            foreach (var marker in snapshot.Markers.Where(marker => marker.State != DecisionOverlayState.Suppressed))
            {
                if (destination.HasValue && Vector3.Distance(marker.Position, destination.Value) < 0.1f)
                {
                    continue;
                }

                yield return new(
                    marker.State == DecisionOverlayState.Rejected ? OverlayVisualKind.BlockedMarker : OverlayVisualKind.ContextMarker,
                    marker.State == DecisionOverlayState.Rejected ? OverlayVisualProminence.Blocked : prominence,
                    snapshot.Source,
                    marker.State,
                    marker.Position,
                    null,
                    null,
                    marker,
                    marker.Label,
                    rank + 6,
                    effectiveAlpha);
            }
        }
    }

    private float SnapshotAlpha(DecisionOverlaySource source)
    {
        return this.snapshotStates.TryGetValue(source, out var state)
            ? state.Alpha
            : 1f;
    }

    private static OverlayVisualProminence VisualProminence(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.State == DecisionOverlayState.Rejected)
        {
            return OverlayVisualProminence.Blocked;
        }

        if (snapshot.State == DecisionOverlayState.Future)
        {
            return OverlayVisualProminence.Preview;
        }

        return IsPassiveSnapshot(snapshot) ? OverlayVisualProminence.Passive : OverlayVisualProminence.Active;
    }

    private static OverlayVisualKind VisualKindForShape(DecisionOverlaySource source, DecisionOverlayShape shape)
    {
        if (source == DecisionOverlaySource.Positionals)
        {
            return OverlayVisualKind.PositionalArc;
        }

        if (source is DecisionOverlaySource.AoE or DecisionOverlaySource.NextAction)
        {
            return OverlayVisualKind.ActionFootprint;
        }

        return shape.State == DecisionOverlayState.Rejected
            ? OverlayVisualKind.BlockedMarker
            : OverlayVisualKind.ZoneRing;
    }

    private static Vector3? DestinationAnchor(DecisionOverlaySnapshot snapshot)
    {
        var lineDestination = snapshot.Lines.FirstOrDefault(line => line.State != DecisionOverlayState.Suppressed)?.To;
        if (lineDestination.HasValue)
        {
            return lineDestination;
        }

        return snapshot.Markers.FirstOrDefault(marker => marker.State != DecisionOverlayState.Suppressed)?.Position;
    }

    private static bool IsPassiveSnapshot(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.State != DecisionOverlayState.Active)
        {
            return false;
        }

        return snapshot.Source != DecisionOverlaySource.FinalMovement &&
               snapshot.Source != DecisionOverlaySource.GapCloser &&
               snapshot.Lines.All(line => line.State == DecisionOverlayState.Suppressed);
    }

    private static int PresentationRank(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.Source == DecisionOverlaySource.FinalMovement)
        {
            return 0;
        }

        if (snapshot.State == DecisionOverlayState.Rejected)
        {
            return 8 + CalloutRank(snapshot);
        }

        if (snapshot.State == DecisionOverlayState.Future)
        {
            return 100 + CalloutRank(snapshot);
        }

        if (IsPassiveSnapshot(snapshot))
        {
            return 200 + PassiveCalloutRank(snapshot.Source);
        }

        return 20 + CalloutRank(snapshot);
    }

    private static int ShapeRankOffset(DecisionOverlaySource source)
    {
        return source is DecisionOverlaySource.AoE or DecisionOverlaySource.NextAction ? 1 : 4;
    }

    private static string CompactLabel(DecisionOverlaySnapshot snapshot)
    {
        return snapshot.Source switch
        {
            DecisionOverlaySource.FinalMovement => "Safe spot",
            DecisionOverlaySource.AoE => CompactAoeLabel(snapshot),
            DecisionOverlaySource.NextAction => BuildNextActionBadge(snapshot.Label),
            DecisionOverlaySource.Positionals => snapshot.Label.StartsWith("Rear", StringComparison.OrdinalIgnoreCase)
                ? "Rear"
                : snapshot.Label.StartsWith("Flank", StringComparison.OrdinalIgnoreCase)
                    ? "Flank"
                    : "Positional",
            DecisionOverlaySource.GapCloser or DecisionOverlaySource.EscapeLanding => "Dash",
            DecisionOverlaySource.HealerCoverage or DecisionOverlaySource.PartyHealerRange => "Healer range",
            DecisionOverlaySource.PassageOfArms => "Wings",
            DecisionOverlaySource.SurvivabilityZone => "Safe ground",
            DecisionOverlaySource.LeyLines => "Ley Lines",
            DecisionOverlaySource.StarryMuse => "Starry",
            DecisionOverlaySource.BossCenterAvoidance => "Center",
            DecisionOverlaySource.RedMageMeleeCombo => "RDM melee",
            DecisionOverlaySource.TankBehavior => "Tank",
            DecisionOverlaySource.TargetUptime => "Range",
            _ => BuildSnapshotBadge(snapshot)
        };
    }

    private static string CompactAoeLabel(DecisionOverlaySnapshot snapshot)
    {
        var detail = ExtractHitChange(snapshot.Label);
        return detail.Contains(" enemies", StringComparison.Ordinal)
            ? $"AoE {detail[..detail.IndexOf(' ', StringComparison.Ordinal)]}"
            : "AoE";
    }

    private static string AoeOverlayDetail(AoePackOverlaySnapshot snapshot)
    {
        if (string.Equals(snapshot.ActionName, "Pack AoE prep", StringComparison.Ordinal) &&
            snapshot.Radius <= Configuration.InternalMeleeUptimeRange + 0.05f)
        {
            return "close-range pack AoE prep";
        }

        return snapshot.ActionName;
    }

    private static (int Covered, int Total) HealerCoverageDisplayCounts(IBattleChara player, HealerCoverageOverlaySnapshot snapshot)
    {
        var includesPlayer = snapshot.Members.Any(member => Geometry.Distance2D(member, player.Position) <= 0.5f);
        return includesPlayer
            ? (snapshot.CoveredMembers, snapshot.TotalMembers)
            : (snapshot.CoveredMembers + 1, snapshot.TotalMembers + 1);
    }

    private DecisionOverlayState ResolveHealerCoverageState(HealerCoverageOverlaySnapshot snapshot, HealerAoePositioningStatus status)
    {
        if (!config.Enabled || !config.ManageMovement || !config.ManageHealerCoverageZone)
        {
            return DecisionOverlayState.Suppressed;
        }

        if (snapshot.Injected)
        {
            return DecisionOverlayState.Candidate;
        }

        return IsHealerCoverageBlocked(status.LastReason)
            ? DecisionOverlayState.Rejected
            : DecisionOverlayState.Active;
    }

    private static bool IsHealerCoverageBlocked(string reason)
    {
        return reason.Contains("held during slidecast", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("needs intent", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("too far", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("too late", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("forced mechanic movement", StringComparison.OrdinalIgnoreCase);
    }

    private static string HealerCoverageLabel(DecisionOverlayState state, string reason, string coverageLabel)
    {
        if (state == DecisionOverlayState.Active)
        {
            return coverageLabel;
        }

        if (state == DecisionOverlayState.Rejected)
        {
            return reason.Contains("held during slidecast", StringComparison.OrdinalIgnoreCase) ||
                   reason.Contains("needs intent", StringComparison.OrdinalIgnoreCase)
                ? "Healer coverage held"
                : "Healer coverage blocked";
        }

        if (reason.Contains("tankbuster", StringComparison.OrdinalIgnoreCase))
        {
            return "Move for tankbuster coverage";
        }

        if (reason.Contains("party AoE heal", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("critical party coverage", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("urgent healing coverage", StringComparison.OrdinalIgnoreCase))
        {
            return "Move for healing coverage";
        }

        return "Move for healer coverage";
    }

    private static string HealerCoverageDetail(
        string reason,
        HealerCoverageOverlaySnapshot snapshot,
        int coveredMembers,
        int totalMembers)
    {
        if (!string.IsNullOrWhiteSpace(reason) &&
            !reason.Equals("not evaluated", StringComparison.OrdinalIgnoreCase) &&
            !reason.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            return reason;
        }

        if (snapshot.Injected)
        {
            return $"{snapshot.DistanceToCenter:0.0}y to better coverage";
        }

        return coveredMembers >= totalMembers
            ? "party covered"
            : $"{coveredMembers}/{totalMembers} covered";
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
                [new(DecisionOverlayState.Candidate, redMage.CandidateDestination.Value, 0.35f, null)]);
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
                $"Next action area: {nextAction.ActionName}",
                $"{nextAction.Shape} {nextAction.EffectRange:0.#}y",
                5,
                [new(shapeKind, DecisionOverlayState.Future, actionOrigin, nextAction.EffectRange, nextAction.HalfWidth, nextAction.EffectRange, rotation, "next action area")],
                [],
                []);
        }

        var healerCoverage = healerAoePositioningController.Overlay;
        if (healerCoverage != null)
        {
            var healerCoverageStatus = healerAoePositioningController.Status;
            var (displayCoveredMembers, displayTotalMembers) = HealerCoverageDisplayCounts(player, healerCoverage);
            var coverageState = this.ResolveHealerCoverageState(healerCoverage, healerCoverageStatus);
            var coverageLabel = $"Healer coverage: {displayCoveredMembers}/{displayTotalMembers}";
            var coverageReason = coverageState == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageHealerCoverageZone, "Healer coverage zone"))
                : HealerCoverageDetail(
                    healerCoverageStatus.LastReason,
                    healerCoverage,
                    displayCoveredMembers,
                    displayTotalMembers);
            yield return new(
                DecisionOverlaySource.HealerCoverage,
                coverageState,
                HealerCoverageLabel(coverageState, healerCoverageStatus.LastReason, coverageLabel),
                coverageReason,
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
                    new DecisionOverlayMarker(DecisionOverlayState.Active, partyHealerRange.HealerPosition, 0.35f, null),
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
                var label = $"Move for AoE hits: {aoe.CurrentHits}->{aoe.BestHits}";
                yield return new(
                    DecisionOverlaySource.AoE,
                    DecisionOverlayState.Candidate,
                    label,
                    AoeOverlayDetail(aoe),
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
                $"AoE preview: {suggestion.CurrentHits}->{suggestion.BestHits}",
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
                passage.PlayerInCone ? "Stay in Passage wings" : "Move into Passage wings",
                reason ?? passage.PaladinName,
                45,
                [new(DecisionOverlayShapeKind.Cone, state, passage.PaladinPosition, passage.Radius, passage.HalfAngle, passage.Radius, passage.RotationRadians, "protected cone")],
                [new(state, player.Position, passage.PreferredPosition, "")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, passage.PaladinPosition, 0.35f, null),
                    new DecisionOverlayMarker(state, passage.PreferredPosition, 0.35f, null)
                ]);
        }

        survivabilityZonePositioningController.RefreshOverlay();
        var survZone = survivabilityZonePositioningController.Overlay;
        if (survZone != null)
        {
            var state = !config.Enabled || !config.ManageMovement || !config.ManageDefensiveGroundZonePositioning
                ? DecisionOverlayState.Suppressed
                : survZone.PlayerSatisfiesPlan
                    ? DecisionOverlayState.Active
                    : survZone.Injected
                        ? DecisionOverlayState.Candidate
                        : DecisionOverlayState.Future;
            var reason = state == DecisionOverlayState.Suppressed
                ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageDefensiveGroundZonePositioning, "Stand in defensive ground effects"))
                : null;
            var label = survZone.PlayerSatisfiesPlan
                ? $"{survZone.ZoneName}: inside"
                : survZone.PlayerInZone && survZone.TargetRangeConstrained
                    ? $"{survZone.ZoneName}: target range"
                    : survZone.ZoneName;
            var movementLabel = survZone.PlayerSatisfiesPlan
                ? $"Stay in safe ground: {label}"
                : survZone.PlayerInZone
                    ? $"Adjust within safe ground: {label}"
                    : $"Move into safe ground: {label}";
            yield return new(
                DecisionOverlaySource.SurvivabilityZone,
                state,
                movementLabel,
                reason ?? survZone.CasterName,
                44,
                [new(DecisionOverlayShapeKind.Circle, state, survZone.ZoneCenter, survZone.Radius, 0f, 0f, 0f, "defensive ground")],
                survZone.PlayerSatisfiesPlan
                    ? []
                    : [new DecisionOverlayLine(state, player.Position, survZone.PreferredEntryPosition, "")],
                [
                    new DecisionOverlayMarker(DecisionOverlayState.Active, survZone.CasterPosition, 0.35f, survZone.ZoneName[..3]),
                    new DecisionOverlayMarker(state, survZone.PreferredEntryPosition, 0.25f, null)
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
                [new DecisionOverlayMarker(state, starryMuse.PreferredPosition, 0.25f, null)]);
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

        var tankBehavior = this.BuildTankBehaviorSnapshot(player);
        if (tankBehavior != null)
        {
            yield return tankBehavior;
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
                    "Dash landing",
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
                "Boss mechanic move",
                "safe spot from encounter mechanics",
                100,
                [],
                [new(DecisionOverlayState.Active, player.Position, destination, "")],
                [new(DecisionOverlayState.Active, destination, 0.35f, null)]);
        }
    }

    private DecisionOverlaySnapshot? BuildTankBehaviorSnapshot(IBattleChara player)
    {
        var tankOverlay = tankBehaviorController.Overlay;
        if (tankOverlay == null ||
            (!tankOverlay.ConeAwayFromParty && !tankOverlay.IgnoredFrontCone && tankOverlay.LostAggroTargets.Count == 0))
        {
            return null;
        }

        var shapes = new List<DecisionOverlayShape>();
        var lines = new List<DecisionOverlayLine>();
        var markers = new List<DecisionOverlayMarker>();
        var state = tankOverlay.ConeAwayFromParty || tankOverlay.LostAggroTargets.Count > 0
            ? DecisionOverlayState.Candidate
            : DecisionOverlayState.Active;
        var label = tankOverlay.ConeAwayFromParty
            ? "Point cleave away"
            : tankOverlay.LostAggroTargets.Count > 0
                ? "Recover trash aggro"
                : "Ignore tank cleave";

        if (tankOverlay.TargetPosition.HasValue)
        {
            var targetPosition = tankOverlay.TargetPosition.Value;
            var targetRadius = MathF.Max(tankOverlay.TargetRadius, 1f);
            if (tankOverlay.PartyCentroid.HasValue)
            {
                var partyCentroid = tankOverlay.PartyCentroid.Value;
                var rotation = MathF.Atan2(partyCentroid.X - targetPosition.X, partyCentroid.Z - targetPosition.Z);
                shapes.Add(new(
                    DecisionOverlayShapeKind.Cone,
                    DecisionOverlayState.Future,
                    targetPosition,
                    MathF.Max(targetRadius + 8f, 10f),
                    tankOverlay.ConeHalfAngleRadians,
                    MathF.Max(targetRadius + 8f, 10f),
                    rotation,
                    "party cleave risk"));
                markers.Add(new(DecisionOverlayState.Future, partyCentroid, 0.35f, null));
            }

            if (tankOverlay.PreferredTankPosition.HasValue)
            {
                var preferred = tankOverlay.PreferredTankPosition.Value;
                lines.Add(new DecisionOverlayLine(DecisionOverlayState.Candidate, player.Position, preferred, string.Empty));
                markers.Add(new DecisionOverlayMarker(DecisionOverlayState.Candidate, preferred, 0.4f, null));
            }

            if (tankOverlay.IgnoredFrontCone && !tankOverlay.ConeAwayFromParty)
            {
                var rotation = MathF.Atan2(player.Position.X - targetPosition.X, player.Position.Z - targetPosition.Z);
                shapes.Add(new(
                    DecisionOverlayShapeKind.Cone,
                    DecisionOverlayState.Active,
                    targetPosition,
                    MathF.Max(targetRadius + 7f, 9f),
                    tankOverlay.ConeHalfAngleRadians,
                    MathF.Max(targetRadius + 7f, 9f),
                    rotation,
                    "ignored cleave"));
            }
        }

        foreach (var target in tankOverlay.LostAggroTargets)
        {
            markers.Add(new DecisionOverlayMarker(DecisionOverlayState.Candidate, target.Position, MathF.Max(target.Radius, 0.45f), null));
        }

        return new(
            DecisionOverlaySource.TankBehavior,
            state,
            label,
            tankOverlay.Reason,
            47,
            shapes,
            lines,
            markers);
    }

    private DecisionOverlaySnapshot? BuildPositionalSnapshot(IBattleChara player, IBattleChara? target)
    {
        if (!config.Enabled ||
            !config.ManagePositionals ||
            trueNorthActive() ||
            target == null ||
            !PositionalTargetPolicy.CanApplyPositionals(target, services.DataManager) ||
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
        blackMageLeyLinesPositioningController.RefreshOverlay();
        var leyLines = blackMageLeyLinesPositioningController.Overlay;
        if (leyLines == null)
        {
            return null;
        }

        var state = !config.Enabled || !config.ManageMovement || !config.ManageLeylines
            ? DecisionOverlayState.Suppressed
            : leyLines.PlayerInZone
                ? DecisionOverlayState.Active
                : leyLines.Injected
                    ? DecisionOverlayState.Candidate
                    : DecisionOverlayState.Future;
        var reason = state == DecisionOverlayState.Suppressed
            ? this.DisabledReason((config.Enabled, "Enabled"), (config.ManageMovement, "Automate movement"), (config.ManageLeylines, "Stay in Ley Lines"))
            : leyLines.Reason;

        return new(
            DecisionOverlaySource.LeyLines,
            state,
            leyLines.PlayerInZone ? "Stay in Ley Lines" : "Return to Ley Lines",
            reason,
            42,
            [new(DecisionOverlayShapeKind.Circle, state, leyLines.ZoneCenter, leyLines.Radius, 0f, 0f, 0f, "Ley Lines")],
            leyLines.PlayerInZone
                ? []
                : [new DecisionOverlayLine(state, player.Position, leyLines.PreferredPosition, "")],
            [new DecisionOverlayMarker(state, leyLines.PreferredPosition, 0.25f, null)]);
    }

    private static string NormalizeRangeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Equals("not checked", StringComparison.OrdinalIgnoreCase))
        {
            return "desired attack range";
        }

        return reason;
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
            this.ClearSnapshotSource(DecisionOverlaySource.GapCloser);
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

        if (target == null)
        {
            this.ClearSnapshotSource(DecisionOverlaySource.GapCloser);
            return;
        }

        var distanceToTarget = Geometry.DistanceToHitbox(player.Position, player.HitboxRadius, target.Position, target.HitboxRadius);
        if (ShouldSuppressDashSnapshot(useMobilityDecision, mobilityDecision, showingEscapeDash, reason, rawState, distanceToTarget, this.CurrentOverlayEngagementRange()))
        {
            this.gapCloserDisplayedState = DecisionOverlayState.Suppressed;
            this.gapCloserPendingState = DecisionOverlayState.Suppressed;
            this.gapCloserPendingStateAt = DateTime.UtcNow;
            this.ClearSnapshotSource(DecisionOverlaySource.GapCloser);
            return;
        }

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

    private void ClearSnapshotSource(DecisionOverlaySource source)
    {
        this.snapshotStates.Remove(source);
    }

    private float CurrentOverlayEngagementRange()
    {
        var range = targetUptimeRange();
        return range > 0f && range < Configuration.InternalDisabledUptimeRange - 0.5f
            ? MathF.Max(range, CombatConstants.GapCloserDestinationMeleeRange)
            : CombatConstants.GapCloserDestinationMeleeRange;
    }

    private static bool ShouldSuppressDashSnapshot(
        bool useMobilityDecision,
        MobilityDecisionDiagnostics mobilityDecision,
        bool showingEscapeDash,
        string reason,
        DecisionOverlayState rawState,
        float distanceToTarget,
        float engagementRange)
    {
        if (rawState == DecisionOverlayState.Suppressed)
        {
            return true;
        }

        var uptimeDash = !useMobilityDecision || mobilityDecision.Intent.HasFlag(MobilityIntent.Uptime);
        if (!showingEscapeDash && uptimeDash && distanceToTarget <= engagementRange + 0.5f)
        {
            return true;
        }

        if (useMobilityDecision &&
            mobilityDecision.State == MobilityDecisionState.Idle &&
            distanceToTarget <= engagementRange + 0.5f)
        {
            return true;
        }

        return reason.Contains("target within", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("target under", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("current position safe", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("not checked", StringComparison.OrdinalIgnoreCase);
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

    private void DrawVisual(ImDrawListPtr drawList, OverlayVisual visual)
    {
        if (visual.Alpha <= 0.02f)
        {
            return;
        }

        switch (visual.Kind)
        {
            case OverlayVisualKind.ActiveDestination:
            case OverlayVisualKind.PreviewDestination:
                this.DrawDestinationPad(drawList, visual);
                break;
            case OverlayVisualKind.MovementPath:
                if (visual.Line != null)
                {
                    var dashed = visual.Prominence == OverlayVisualProminence.Preview;
                    this.DrawPathLine(
                        drawList,
                        visual.Line.From,
                        visual.Line.To,
                        ColorFor(visual.State, visual.Source, visual.Alpha),
                        LineThickness(visual.Source, visual.State),
                        dashed);
                }

                break;
            case OverlayVisualKind.ActionFootprint:
            case OverlayVisualKind.ZoneRing:
            case OverlayVisualKind.PositionalArc:
                if (visual.Shape != null)
                {
                    this.DrawShape(drawList, visual.Shape, visual.Source, ColorFor(visual.State, visual.Source, visual.Alpha), visual.Alpha);
                }

                break;
            case OverlayVisualKind.BlockedMarker:
                this.DrawBlockedMarker(drawList, visual.Anchor, visual.Source, visual.Alpha);
                break;
            case OverlayVisualKind.ContextMarker:
                if (visual.Marker != null)
                {
                    this.DrawContextMarker(drawList, visual);
                }

                break;
            case OverlayVisualKind.CompactCallout:
                break;
        }
    }

    private void DrawDestinationPad(ImDrawListPtr drawList, OverlayVisual visual)
    {
        var color = ColorFor(visual.State, visual.Source, visual.Alpha);
        var fill = FillColorFor(visual.State, visual.Source, visual.Alpha * 1.8f);
        var radius = visual.Kind == OverlayVisualKind.PreviewDestination ? 0.46f : 0.56f;
        this.DrawCircleFilled(drawList, visual.Anchor, radius * 1.45f, fill);
        if (visual.Kind == OverlayVisualKind.PreviewDestination)
        {
            this.DrawDashedCircle(drawList, visual.Anchor, radius, color, 2.4f);
        }
        else
        {
            this.DrawCircle(drawList, visual.Anchor, radius, color, 3.3f);
            this.DrawCircle(drawList, visual.Anchor, radius * 0.45f, color, 2.2f);
        }

        if (config.DecisionOverlayDensity != OverlayDensity.Minimal && !string.IsNullOrWhiteSpace(visual.Label))
        {
            this.DrawLabel(drawList, visual.Anchor, visual.Label, color);
        }
    }

    private void DrawContextMarker(ImDrawListPtr drawList, OverlayVisual visual)
    {
        var marker = visual.Marker!;
        var markerRadius = MarkerRadius(visual.Source, marker);
        var markerColor = ColorFor(marker.State, visual.Source, visual.Alpha);
        if (ShouldFillMarker(marker.State, visual.Source))
        {
            this.DrawCircleFilled(drawList, marker.Position, markerRadius, MarkerFillColorFor(marker.State, visual.Source, visual.Alpha));
        }

        this.DrawCircle(drawList, marker.Position, markerRadius, markerColor, thickness: MarkerThickness(visual.Source, marker.State));
        if (marker.Label != null && config.DecisionOverlayDensity == OverlayDensity.Detailed)
        {
            this.DrawLabel(drawList, marker.Position, marker.Label, markerColor);
        }
    }

    private void DrawBlockedMarker(ImDrawListPtr drawList, Vector3 anchor, DecisionOverlaySource source, float alpha)
    {
        var color = ColorFor(DecisionOverlayState.Rejected, source, alpha);
        this.DrawDashedCircle(drawList, anchor, 0.52f, color, 3f);
        this.DrawCross(drawList, anchor, 0.34f, color, 2.6f);
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

    private void DrawShape(ImDrawListPtr drawList, DecisionOverlayShape shape, DecisionOverlaySource source, uint color, float alpha = 1f)
    {
        var fillColor = FillColorFor(shape.State, source, alpha);
        switch (shape.Kind)
        {
            case DecisionOverlayShapeKind.Circle:
                if (ShouldFillShape(shape.State, source) || source is DecisionOverlaySource.AoE or DecisionOverlaySource.NextAction)
                {
                    this.DrawCircleFilled(drawList, shape.Origin, shape.Radius, fillColor);
                }
                if (shape.State == DecisionOverlayState.Future)
                {
                    this.DrawDashedCircle(drawList, shape.Origin, shape.Radius, color, ShapeThickness(source, shape.State));
                }
                else
                {
                    this.DrawCircle(drawList, shape.Origin, shape.Radius, color, ShapeThickness(source, shape.State));
                }
                break;
            case DecisionOverlayShapeKind.Cone:
                if (ShouldFillShape(shape.State, source) || source is DecisionOverlaySource.AoE or DecisionOverlaySource.NextAction or DecisionOverlaySource.PassageOfArms or DecisionOverlaySource.TankBehavior)
                {
                    this.DrawConeFilled(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, fillColor);
                }
                if (source == DecisionOverlaySource.Positionals)
                {
                    this.DrawPositionalArc(drawList, shape, color);
                    break;
                }

                this.DrawCone(drawList, shape.Origin, shape.Radius, shape.RotationRadians, shape.HalfWidth, color, ShapeThickness(source, shape.State), shape.State == DecisionOverlayState.Future);
                break;
            case DecisionOverlayShapeKind.Rectangle:
                if (ShouldFillShape(shape.State, source) || source is DecisionOverlaySource.AoE or DecisionOverlaySource.NextAction)
                {
                    this.DrawRectangleFilled(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, fillColor);
                }
                this.DrawRectangle(drawList, shape.Origin, shape.RotationRadians, shape.Length, shape.HalfWidth, color, ShapeThickness(source, shape.State), shape.State == DecisionOverlayState.Future);
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

    private void DrawCone(ImDrawListPtr drawList, Vector3 center, float radius, float rotation, float halfWidth, uint color, float thickness, bool dashed = false)
    {
        var left = center + Direction(rotation - halfWidth) * radius;
        var right = center + Direction(rotation + halfWidth) * radius;
        this.DrawLine(drawList, center, left, color, thickness, dashed: dashed);
        this.DrawLine(drawList, center, right, color, thickness, dashed: dashed);

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

            if (previous.HasValue && (!dashed || i % 3 != 1))
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

    private void DrawRectangle(ImDrawListPtr drawList, Vector3 origin, float rotation, float length, float halfWidth, uint color, float thickness, bool dashed = false)
    {
        var forward = Direction(rotation);
        var side = new Vector3(forward.Z, 0f, -forward.X);
        var p1 = origin + side * halfWidth;
        var p2 = origin - side * halfWidth;
        var p3 = origin + forward * length - side * halfWidth;
        var p4 = origin + forward * length + side * halfWidth;
        this.DrawLine(drawList, p1, p2, color, thickness, dashed: dashed);
        this.DrawLine(drawList, p2, p3, color, thickness, dashed: dashed);
        this.DrawLine(drawList, p3, p4, color, thickness, dashed: dashed);
        this.DrawLine(drawList, p4, p1, color, thickness, dashed: dashed);
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

    private void DrawLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness, bool arrow = false, bool dashed = false)
    {
        if (this.Project(from, out var fromScreen) && this.Project(to, out var toScreen))
        {
            if (arrow)
            {
                DrawArrowLine(drawList, fromScreen, toScreen, color, thickness);
                return;
            }

            if (dashed)
            {
                DrawDashedScreenLine(drawList, fromScreen, toScreen, color, thickness);
                return;
            }

            DrawScreenLine(drawList, fromScreen, toScreen, color, thickness);
        }
    }

    private void DrawPathLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness, bool dashed)
    {
        if (!this.Project(from, out var fromScreen) || !this.Project(to, out var toScreen))
        {
            return;
        }

        var direction = toScreen - fromScreen;
        var length = direction.Length();
        if (length <= 3f)
        {
            return;
        }

        direction /= length;
        var start = fromScreen + direction * MathF.Min(16f, length * 0.18f);
        var end = toScreen - direction * MathF.Min(18f, length * 0.22f);
        if (dashed)
        {
            DrawDashedScreenLine(drawList, start, end, color, thickness);
        }
        else
        {
            DrawScreenLine(drawList, start, end, color, thickness);
        }

        DrawPathChevron(drawList, start, end, color, thickness);
    }

    private void DrawDashedCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color, float thickness)
    {
        const int Segments = 48;
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

            if (previous.HasValue && i % 4 < 2)
            {
                DrawScreenLine(drawList, previous.Value, screen, color, thickness);
            }

            previous = screen;
        }
    }

    private void DrawCross(ImDrawListPtr drawList, Vector3 center, float radius, uint color, float thickness)
    {
        this.DrawLine(drawList, center + new Vector3(-radius, 0f, -radius), center + new Vector3(radius, 0f, radius), color, thickness);
        this.DrawLine(drawList, center + new Vector3(-radius, 0f, radius), center + new Vector3(radius, 0f, -radius), color, thickness);
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

    private static IEnumerable<OverlayCallout> BuildCallouts(IReadOnlyList<DecisionOverlaySnapshot> snapshots)
    {
        var current = snapshots
            .Select(BuildCallout)
            .Where(callout => callout != null && callout.State != DecisionOverlayState.Future)
            .Cast<OverlayCallout>()
            .OrderBy(callout => callout.Rank)
            .ThenByDescending(callout => callout.State == DecisionOverlayState.Candidate)
            .ThenBy(callout => callout.Source)
            .FirstOrDefault();
        if (current != null)
        {
            yield return current;
        }

        var next = snapshots
            .Select(BuildCallout)
            .Where(callout => callout != null && callout.State == DecisionOverlayState.Future)
            .Cast<OverlayCallout>()
            .OrderBy(callout => callout.Rank)
            .ThenBy(callout => callout.Source)
            .FirstOrDefault();
        if (next != null)
        {
            yield return next;
        }
    }

    private static OverlayCallout? BuildCallout(DecisionOverlaySnapshot snapshot)
    {
        var anchor = CalloutAnchor(snapshot);
        if (!ShouldDrawWorldSnapshot(snapshot) || !anchor.HasValue)
        {
            return null;
        }

        var (title, detail) = BuildCalloutText(snapshot);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new OverlayCallout(
            snapshot.State,
            snapshot.Source,
            anchor.Value,
            title,
            detail,
            CalloutRank(snapshot));
    }

    private static Vector3? CalloutAnchor(DecisionOverlaySnapshot snapshot)
    {
        return snapshot.Markers.FirstOrDefault(marker => marker.State != DecisionOverlayState.Suppressed)?.Position ??
               snapshot.Lines.FirstOrDefault(line => line.State != DecisionOverlayState.Suppressed)?.To ??
               snapshot.Shapes.FirstOrDefault(shape => shape.State != DecisionOverlayState.Suppressed)?.Origin;
    }

    private static (string Title, string Detail) BuildCalloutText(DecisionOverlaySnapshot snapshot)
    {
        return snapshot.Source switch
        {
            DecisionOverlaySource.FinalMovement => ("Boss mechanic move", "Move to the safe spot"),
            DecisionOverlaySource.AoE => BuildAoeCallout(snapshot),
            DecisionOverlaySource.GapCloser => BuildDashCallout(snapshot),
            DecisionOverlaySource.EscapeLanding => ("Dash landing", "Safe landing preview"),
            DecisionOverlaySource.HealerCoverage => snapshot.State == DecisionOverlayState.Active
                ? ("Healer coverage", ReadableDetail(snapshot.Reason, "Party is covered"))
                : snapshot.State == DecisionOverlayState.Rejected
                    ? (snapshot.Label, ReadableDetail(snapshot.Reason, "Routine coverage movement is held"))
                    : (snapshot.Label, ReadableDetail(snapshot.Reason, "Better party coverage")),
            DecisionOverlaySource.PartyHealerRange => snapshot.State == DecisionOverlayState.Active
                ? ("Healer range", ReadableDetail(snapshot.Reason, "Inside healing range"))
                : snapshot.State == DecisionOverlayState.Rejected
                    ? ("Healer range blocked", ReadableDetail(snapshot.Reason, "Too far to adjust safely"))
                    : ("Move to healer range", ReadableDetail(snapshot.Reason, "Get closer to healing range")),
            DecisionOverlaySource.PassageOfArms => snapshot.State == DecisionOverlayState.Active
                ? ("Passage wings", "Stay inside protection")
                : ("Move to Passage wings", ReadableDetail(snapshot.Reason, "Protected cone")),
            DecisionOverlaySource.SurvivabilityZone => snapshot.State == DecisionOverlayState.Active
                ? ("Safe ground", ReadableDetail(snapshot.Reason, "Stay in the zone"))
                : ("Move to safe ground", ReadableDetail(snapshot.Reason, "Defensive ground effect")),
            DecisionOverlaySource.StarryMuse => snapshot.State == DecisionOverlayState.Active
                ? ("Starry Muse", "Stay in the circle")
                : ("Return to Starry Muse", "Pictomancer damage circle"),
            DecisionOverlaySource.LeyLines => snapshot.State == DecisionOverlayState.Active
                ? ("Ley Lines", "Stay in the circle")
                : ("Return to Ley Lines", ReadableDetail(snapshot.Reason, "Use the return path")),
            DecisionOverlaySource.Positionals => BuildPositionalCallout(snapshot),
            DecisionOverlaySource.TargetUptime => snapshot.State == DecisionOverlayState.Active
                ? ("Attack range", ReadableDetail(snapshot.Reason, "Target is in range"))
                : ("Move to attack range", ReadableDetail(snapshot.Reason, "Target is out of range")),
            DecisionOverlaySource.NextAction => ("Next action area", BuildNextActionDetail(snapshot)),
            DecisionOverlaySource.RedMageMeleeCombo => ("Red Mage melee", ReadableDetail(snapshot.Reason, "Move into combo range")),
            DecisionOverlaySource.TankBehavior => BuildTankBehaviorCallout(snapshot),
            DecisionOverlaySource.BossCenterAvoidance => snapshot.State == DecisionOverlayState.Future
                ? ("Avoid boss center", ReadableDetail(snapshot.Reason, "Prefer a more natural spot"))
                : ("Move out of boss center", ReadableDetail(snapshot.Reason, "Avoid standing inside the target")),
            _ => (snapshot.Label, ReadableDetail(snapshot.Reason, string.Empty))
        };
    }

    private static (string Title, string Detail) BuildAoeCallout(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.Label.Contains("attack range", StringComparison.OrdinalIgnoreCase))
        {
            return ("Move to attack range", ReadableDetail(snapshot.Reason, "Target is out of range"));
        }

        var detail = ExtractHitChange(snapshot.Label);
        return snapshot.State == DecisionOverlayState.Future
            ? ("AoE position preview", detail)
            : ("Move for AoE", detail);
    }

    private static (string Title, string Detail) BuildDashCallout(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.State == DecisionOverlayState.Rejected)
        {
            return ("Dash blocked", ReadableDetail(snapshot.Reason, "No safe dash"));
        }

        if (snapshot.Label.Contains("used", StringComparison.OrdinalIgnoreCase))
        {
            return ("Dash used", ReadableDetail(snapshot.Reason, "Moving now"));
        }

        return ("Dash ready", ReadableDetail(snapshot.Reason, "Safe dash option"));
    }

    private static (string Title, string Detail) BuildTankBehaviorCallout(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.Label.Contains("aggro", StringComparison.OrdinalIgnoreCase))
        {
            return ("Tank aggro", ReadableDetail(snapshot.Reason, "Recover loose trash"));
        }

        if (snapshot.Label.Contains("ignore", StringComparison.OrdinalIgnoreCase))
        {
            return ("Tank cleave", ReadableDetail(snapshot.Reason, "Holding tank position"));
        }

        return ("Tank cleave", ReadableDetail(snapshot.Reason, "Point cleave away from party"));
    }

    private static (string Title, string Detail) BuildPositionalCallout(DecisionOverlaySnapshot snapshot)
    {
        var title = snapshot.Label.StartsWith("Rear", StringComparison.OrdinalIgnoreCase)
            ? "Rear positional"
            : snapshot.Label.StartsWith("Flank", StringComparison.OrdinalIgnoreCase)
                ? "Flank positional"
                : "Positional";
        return snapshot.State == DecisionOverlayState.Active
            ? (title, "Already on the correct side")
            : (title, ReadableDetail(snapshot.Reason, "Move to the correct side"));
    }

    private static string BuildNextActionDetail(DecisionOverlaySnapshot snapshot)
    {
        const string Prefix = "Next action area: ";
        var action = snapshot.Label.StartsWith(Prefix, StringComparison.Ordinal)
            ? snapshot.Label[Prefix.Length..]
            : snapshot.Label;
        var shape = ReadableDetail(snapshot.Reason, "Action area");
        return $"{action} - {shape}";
    }

    private static string ExtractHitChange(string label)
    {
        var colon = label.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && colon + 1 < label.Length)
        {
            var change = label[(colon + 1)..].Replace("+RSR", string.Empty, StringComparison.Ordinal).Trim();
            if (!string.IsNullOrWhiteSpace(change))
            {
                return $"{change} enemies hit";
            }
        }

        return "Better enemy coverage";
    }

    private static string ReadableDetail(string? detail, string fallback)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return fallback;
        }

        if (detail.StartsWith("disabled:", StringComparison.OrdinalIgnoreCase))
        {
            return detail;
        }

        return detail;
    }

    private static int CalloutRank(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.Source == DecisionOverlaySource.FinalMovement)
        {
            return 0;
        }

        if (snapshot.State == DecisionOverlayState.Future)
        {
            return snapshot.Source switch
            {
                DecisionOverlaySource.NextAction => 40,
                DecisionOverlaySource.AoE => 41,
                DecisionOverlaySource.EscapeLanding => 42,
                DecisionOverlaySource.LeyLines => 43,
                DecisionOverlaySource.PassageOfArms => 44,
                DecisionOverlaySource.SurvivabilityZone => 45,
                DecisionOverlaySource.StarryMuse => 46,
                DecisionOverlaySource.PartyHealerRange => 47,
                DecisionOverlaySource.Positionals => 48,
                DecisionOverlaySource.BossCenterAvoidance => 49,
                DecisionOverlaySource.TankBehavior => 50,
                _ => 60
            };
        }

        if (snapshot.State == DecisionOverlayState.Active &&
            snapshot.Source != DecisionOverlaySource.FinalMovement &&
            snapshot.Source != DecisionOverlaySource.GapCloser)
        {
            return 90 + PassiveCalloutRank(snapshot.Source);
        }

        return snapshot.Source switch
        {
            DecisionOverlaySource.AoE => 10,
            DecisionOverlaySource.PartyHealerRange => 12,
            DecisionOverlaySource.HealerCoverage => 13,
            DecisionOverlaySource.PassageOfArms => 14,
            DecisionOverlaySource.SurvivabilityZone => 15,
            DecisionOverlaySource.StarryMuse => 16,
            DecisionOverlaySource.LeyLines => 17,
            DecisionOverlaySource.BossCenterAvoidance => 18,
            DecisionOverlaySource.TankBehavior => 19,
            DecisionOverlaySource.Positionals => 20,
            DecisionOverlaySource.TargetUptime => 21,
            DecisionOverlaySource.GapCloser => 30,
            DecisionOverlaySource.RedMageMeleeCombo => 31,
            _ => 80
        };
    }

    private static int PassiveCalloutRank(DecisionOverlaySource source)
    {
        return source switch
        {
            DecisionOverlaySource.PartyHealerRange => 0,
            DecisionOverlaySource.HealerCoverage => 1,
            DecisionOverlaySource.PassageOfArms => 2,
            DecisionOverlaySource.SurvivabilityZone => 3,
            DecisionOverlaySource.StarryMuse => 4,
            DecisionOverlaySource.LeyLines => 5,
            DecisionOverlaySource.Positionals => 6,
            DecisionOverlaySource.TargetUptime => 7,
            _ => 20
        };
    }

    private void DrawCallout(ImDrawListPtr drawList, OverlayCallout callout, int slot)
    {
        if (!this.Project(callout.Anchor, out var anchorScreen))
        {
            return;
        }

        var titleSize = ImGui.CalcTextSize(callout.Title);
        var detailSize = string.IsNullOrWhiteSpace(callout.Detail)
            ? Vector2.Zero
            : ImGui.CalcTextSize(callout.Detail);
        var padding = new Vector2(8f, 5f);
        var lineGap = string.IsNullOrWhiteSpace(callout.Detail) ? 0f : 2f;
        var textWidth = MathF.Max(titleSize.X, detailSize.X);
        var textHeight = titleSize.Y + (string.IsNullOrWhiteSpace(callout.Detail) ? 0f : detailSize.Y + lineGap);
        var size = new Vector2(textWidth + padding.X * 2f + 5f, textHeight + padding.Y * 2f);
        var displaySize = ImGui.GetIO().DisplaySize;
        var pos = anchorScreen + new Vector2(-size.X * 0.5f, -size.Y - 48f - slot * (size.Y + 10f));

        if (pos.Y < 8f)
        {
            pos.Y = anchorScreen.Y + 22f + slot * (size.Y + 10f);
        }

        pos.X = Math.Clamp(pos.X, 8f, MathF.Max(8f, displaySize.X - size.X - 8f));
        pos.Y = Math.Clamp(pos.Y, 8f, MathF.Max(8f, displaySize.Y - size.Y - 8f));

        var bg = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.70f));
        var border = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f));
        var accent = ColorFor(callout.State, callout.Source);
        drawList.AddLine(anchorScreen, new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y), border, 1.2f);
        drawList.AddRectFilled(pos, pos + size, bg, 5f);
        drawList.AddRectFilled(pos, pos + new Vector2(5f, size.Y), accent, 5f);
        drawList.AddRect(pos, pos + size, border, 5f);

        var textPos = pos + new Vector2(padding.X + 5f, padding.Y);
        drawList.AddText(textPos, accent, callout.Title);
        if (!string.IsNullOrWhiteSpace(callout.Detail))
        {
            drawList.AddText(
                textPos + new Vector2(0f, titleSize.Y + lineGap),
                ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 0.92f, 1f)),
                callout.Detail);
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

    private static void DrawArrowLine(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length <= 1f)
        {
            return;
        }

        direction /= length;
        var tipInset = Math.Clamp(thickness * 1.2f, 2f, 5f);
        var tip = to - direction * tipInset;
        drawList.AddLine(from, tip, ShadowColor(), thickness + 2.5f);
        drawList.AddLine(from, tip, color, thickness);

        var headLength = Math.Clamp(thickness * 4.8f, 11f, 18f);
        var headWidth = Math.Clamp(thickness * 2.2f, 5f, 9f);
        DrawArrowChevron(drawList, tip, direction, headLength, headWidth, ShadowColor(), thickness + 2.5f);
        DrawArrowChevron(drawList, tip, direction, headLength, headWidth, color, thickness);

        if (length > 110f)
        {
            var mid = from + direction * (length * 0.58f);
            DrawArrowChevron(drawList, mid, direction, headLength * 0.62f, headWidth * 0.62f, ShadowColor(), MathF.Max(1.5f, thickness + 1.6f));
            DrawArrowChevron(drawList, mid, direction, headLength * 0.62f, headWidth * 0.62f, color, MathF.Max(1.2f, thickness * 0.72f));
        }
    }

    private static void DrawPathChevron(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length <= 14f)
        {
            return;
        }

        direction /= length;
        var headLength = Math.Clamp(thickness * 4f, 9f, 15f);
        var headWidth = Math.Clamp(thickness * 1.8f, 4f, 8f);
        var tip = from + direction * (length * 0.62f);
        DrawArrowChevron(drawList, tip, direction, headLength, headWidth, ShadowColor(), thickness + 2.2f);
        DrawArrowChevron(drawList, tip, direction, headLength, headWidth, color, thickness);
    }

    private static void DrawScreenLine(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
    {
        drawList.AddLine(from, to, ShadowColor(), thickness + 2.5f);
        drawList.AddLine(from, to, color, thickness);
    }

    private static void DrawDashedScreenLine(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
    {
        var direction = to - from;
        var length = direction.Length();
        if (length <= 1f)
        {
            return;
        }

        direction /= length;
        const float Dash = 10f;
        const float Gap = 7f;
        for (var offset = 0f; offset < length; offset += Dash + Gap)
        {
            var start = from + direction * offset;
            var end = from + direction * MathF.Min(length, offset + Dash);
            DrawScreenLine(drawList, start, end, color, thickness);
        }
    }

    private static void DrawArrowChevron(ImDrawListPtr drawList, Vector2 tip, Vector2 direction, float length, float width, uint color, float thickness)
    {
        var side = new Vector2(-direction.Y, direction.X);
        var basePoint = tip - direction * length;
        drawList.AddLine(basePoint + side * width, tip, color, thickness);
        drawList.AddLine(basePoint - side * width, tip, color, thickness);
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
                   or DecisionOverlaySource.BossCenterAvoidance
                   or DecisionOverlaySource.TankBehavior;
    }

    private static bool ShouldDrawWorldSnapshot(DecisionOverlaySnapshot snapshot)
    {
        if (snapshot.State == DecisionOverlayState.Suppressed)
        {
            return false;
        }

        return snapshot.Source != DecisionOverlaySource.GapCloser ||
               snapshot.State is DecisionOverlayState.Active or DecisionOverlayState.Candidate or DecisionOverlayState.Rejected;
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
            DecisionOverlaySource.TankBehavior => "TANK",
            _ => snapshot.Source.ToString()
        };
    }

    private static string BuildNextActionBadge(string label)
    {
        const string Prefix = "Next action area: ";
        var actionName = label.StartsWith(Prefix, StringComparison.Ordinal)
            ? label[Prefix.Length..]
            : label;
        const int MaxLength = 22;
        return actionName.Length <= MaxLength
            ? actionName
            : string.Concat(actionName.AsSpan(0, MaxLength - 3), "...");
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
            DecisionOverlaySource.TankBehavior => false,
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
            DecisionOverlaySource.TankBehavior => MathF.Max(radius, 0.45f),
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
                   or DecisionOverlaySource.TankBehavior
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
        return MarkerFillColorFor(state, source, 1f);
    }

    private static uint MarkerFillColorFor(DecisionOverlayState state, DecisionOverlaySource source, float alphaMultiplier)
    {
        var color = ColorVectorFor(state, source);
        color.W = Math.Clamp(0.18f * alphaMultiplier, 0f, 0.28f);
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

    private static uint ColorFor(DecisionOverlayState state, DecisionOverlaySource source, float alphaMultiplier)
    {
        var color = ColorVectorFor(state, source);
        color.W = Math.Clamp(color.W * alphaMultiplier, 0f, 1f);
        return ImGui.GetColorU32(color);
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
            DecisionOverlayState.Active => SourceAccent(source, 1f),
            DecisionOverlayState.Candidate => SourceAccent(source, 1f),
            DecisionOverlayState.Future => new Vector4(1f, 0.86f, 0.25f, 1f),
            DecisionOverlayState.Rejected => new Vector4(1f, 0.2f, 0.2f, 1f),
            DecisionOverlayState.Suppressed => new Vector4(0.58f, 0.58f, 0.58f, 0.82f),
            _ => new Vector4(0.55f, 0.55f, 0.55f, 1f)
        };
    }

    private static uint FillColorFor(DecisionOverlayState state, DecisionOverlaySource source)
    {
        return FillColorFor(state, source, 1f);
    }

    private static uint FillColorFor(DecisionOverlayState state, DecisionOverlaySource source, float alphaMultiplier)
    {
        var alpha = state switch
        {
            DecisionOverlayState.Active => 0.11f,
            DecisionOverlayState.Candidate => 0.09f,
            DecisionOverlayState.Future => 0.055f,
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
        color.W = Math.Clamp(alpha * alphaMultiplier, 0f, 0.22f);
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
            DecisionOverlaySource.TankBehavior => new Vector4(1f, 0.58f, 0.16f, alpha),
            DecisionOverlaySource.FinalMovement => new Vector4(0.95f, 1f, 1f, alpha),
            _ => new Vector4(0.35f, 0.68f, 1f, alpha)
        };
    }
}
