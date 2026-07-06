namespace FightReview;

internal static class UptimeScoring
{
    private const float MeleePreferredRange = 3.0f;
    private const float MeleeFallbackRange = 15.0f;
    private const float RangedPreferredRange = 25.0f;
    private const float HealerPartyRange = 30.0f;
    private const float RangeSlack = 0.5f;

    public static UptimeAnalysis Analyze(XcaiLog log)
    {
        var profile = UptimeJobProfile.For(log.Header.PlayerClassJobId);
        var frames = log.Frames;
        var durations = EstimateFrameDurations(log.Header, frames);

        var targetObservedSeconds = 0f;
        var anyUptimeSeconds = 0f;
        var preferredUptimeSeconds = 0f;
        var fallbackUptimeSeconds = 0f;
        var weightedTargetUptimeSeconds = 0f;
        var outOfRangeSeconds = 0f;
        var avoidableOutOfRangeSeconds = 0f;
        var bmrPressureSeconds = 0f;
        var bmrPressureInRangeSeconds = 0f;
        var greedPressureSeconds = 0f;
        var greedPressureInRangeSeconds = 0f;
        var normalPressureOutOfRangeSeconds = 0f;
        var packOpportunitySeconds = 0f;
        var weightedPackHitSeconds = 0f;
        var healerCoverageSeconds = 0f;
        var weightedHealerCoverageSeconds = 0f;

        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            var duration = durations[i];
            var state = ClassifyFrame(log.Header, profile, frame);

            if (state.TargetObserved)
            {
                targetObservedSeconds += duration;
                weightedTargetUptimeSeconds += state.TargetUptimeValue * duration;
                if (state.InAnyRange)
                {
                    anyUptimeSeconds += duration;
                }

                if (state.InPreferredRange)
                {
                    preferredUptimeSeconds += duration;
                }
                else if (state.InFallbackRange)
                {
                    fallbackUptimeSeconds += duration;
                }
                else
                {
                    outOfRangeSeconds += duration;
                    if (state.AvoidableOutOfRange)
                    {
                        avoidableOutOfRangeSeconds += duration;
                    }
                }
            }

            if (state.BmrPressure)
            {
                bmrPressureSeconds += duration;
                if (state.InAnyRange)
                {
                    bmrPressureInRangeSeconds += duration;
                }

                if (state.GreedStyle)
                {
                    greedPressureSeconds += duration;
                    if (state.InAnyRange)
                    {
                        greedPressureInRangeSeconds += duration;
                    }
                }
                else if (state.TargetObserved && !state.InAnyRange)
                {
                    normalPressureOutOfRangeSeconds += duration;
                }
            }

            if (state.PackOpportunity)
            {
                packOpportunitySeconds += duration;
                weightedPackHitSeconds += state.PackHitValue * duration;
            }

            if (state.HealerPartyCoverage.HasValue)
            {
                healerCoverageSeconds += duration;
                weightedHealerCoverageSeconds += state.HealerPartyCoverage.Value * duration;
            }
        }

        var targetUptimePercent = Percent(weightedTargetUptimeSeconds, targetObservedSeconds);
        var packHitEfficiencyPercent = packOpportunitySeconds > 0
            ? Percent(weightedPackHitSeconds, packOpportunitySeconds)
            : 100f;
        var healerCoveragePercent = healerCoverageSeconds > 0
            ? Percent(weightedHealerCoverageSeconds, healerCoverageSeconds)
            : 100f;
        var uptimeScore = profile.IsHealer
            ? ClampScore((targetUptimePercent * 0.62f) + (packHitEfficiencyPercent * 0.18f) + (healerCoveragePercent * 0.20f))
            : ClampScore((targetUptimePercent * 0.72f) + (packHitEfficiencyPercent * 0.28f));

        var metrics = new UptimeMetrics(
            uptimeScore,
            targetObservedSeconds,
            anyUptimeSeconds,
            preferredUptimeSeconds,
            fallbackUptimeSeconds,
            weightedTargetUptimeSeconds,
            outOfRangeSeconds,
            avoidableOutOfRangeSeconds,
            bmrPressureSeconds,
            bmrPressureInRangeSeconds,
            greedPressureSeconds,
            greedPressureInRangeSeconds,
            normalPressureOutOfRangeSeconds,
            packOpportunitySeconds,
            weightedPackHitSeconds,
            targetObservedSeconds > 0 ? anyUptimeSeconds / targetObservedSeconds : 1f,
            targetObservedSeconds > 0 ? preferredUptimeSeconds / targetObservedSeconds : 1f,
            targetUptimePercent,
            packHitEfficiencyPercent,
            healerCoveragePercent);

        return new UptimeAnalysis(
            profile,
            metrics,
            BuildPositiveSignals(log.Header, profile, metrics),
            BuildNegativeSignals(log.Header, profile, metrics),
            BuildSegments(log.Header, profile, frames, durations));
    }

    private static UptimeFrameState ClassifyFrame(XcaiHeader header, UptimeJobProfile profile, XcaiFrame frame)
    {
        var surfaceDistance = TargetSurfaceDistance(frame);
        var targetObserved = frame.InCombat &&
                             !frame.IsDead &&
                             frame.TargetObjectId != 0 &&
                             surfaceDistance.HasValue;
        var preferredRange = profile.PrefersMeleeRange
            ? MeleePreferredRange
            : MathF.Max(profile.PreferredSurfaceRange, frame.EngagementRange);
        var fallbackRange = profile.PrefersMeleeRange
            ? MathF.Max(MeleeFallbackRange, frame.EngagementRange)
            : preferredRange;
        var inPreferredRange = targetObserved && surfaceDistance!.Value <= preferredRange + RangeSlack;
        var inFallbackRange = targetObserved && !inPreferredRange && surfaceDistance!.Value <= fallbackRange + RangeSlack;
        var inAnyRange = inPreferredRange || inFallbackRange;
        var targetUptimeValue = inPreferredRange
            ? 1f
            : inFallbackRange && !profile.PrefersMeleeRange ? profile.FallbackUptimeValue : 0f;
        var bmrPressure = HasBmrPressure(frame);
        var forcedMovement = HasForcedBmrMovement(frame);
        var greedStyle = IsGreedStyle(header.CombatStyle);
        var avoidableOutOfRange = targetObserved &&
                                  !inAnyRange &&
                                  !frame.AutomatedMovementSuppressed &&
                                  !frame.IsDead &&
                                  (!bmrPressure || (greedStyle && !forcedMovement));
        var packOpportunity = IsPackOpportunity(frame);
        var packHitValue = packOpportunity ? PackHitValue(frame) : 1f;
        var healerCoverage = profile.IsHealer ? HealerPartyCoverage(frame) : null;

        return new UptimeFrameState(
            targetObserved,
            inAnyRange,
            inPreferredRange,
            inFallbackRange,
            targetUptimeValue,
            avoidableOutOfRange,
            bmrPressure,
            forcedMovement,
            greedStyle,
            packOpportunity,
            packHitValue,
            healerCoverage);
    }

    private static IReadOnlyList<UptimeSignal> BuildPositiveSignals(XcaiHeader header, UptimeJobProfile profile, UptimeMetrics metrics)
    {
        var signals = new List<UptimeSignal>();
        if (metrics.TargetObservedSeconds > 0 && metrics.AnyTargetUptimeRatio >= 0.85f)
        {
            signals.Add(new UptimeSignal(
                "target-uptime",
                metrics.AnyTargetUptimeSeconds,
                metrics.AnyTargetUptimeSeconds,
                $"Target was in usable range for {metrics.AnyTargetUptimeRatio:P0} of observed target time.",
                "Preserve range-first decisions that keep RSR able to act."));
        }

        if (metrics.PackOpportunitySeconds > 0 && metrics.PackHitEfficiencyPercent >= 85f)
        {
            signals.Add(new UptimeSignal(
                "trash-pack-hit-quality",
                metrics.WeightedPackHitSeconds,
                metrics.PackOpportunitySeconds,
                $"Trash AoE hit efficiency was {metrics.PackHitEfficiencyPercent:0}% across {metrics.PackOpportunitySeconds:0.0}s of logged pack opportunities.",
                "Preserve pack positions and target choices that let AoE hit most available enemies."));
        }

        if (IsGreedStyle(header.CombatStyle) && metrics.GreedPressureSeconds > 0)
        {
            var ratio = metrics.GreedPressureInRangeSeconds / MathF.Max(1f, metrics.GreedPressureSeconds);
            if (ratio >= 0.70f)
            {
                signals.Add(new UptimeSignal(
                    "greed-pressure-uptime",
                    metrics.GreedPressureInRangeSeconds,
                    metrics.GreedPressureInRangeSeconds,
                    $"Greed profile kept usable range for {ratio:P0} of BMR-pressure time.",
                    "Complement greedy BMR timing by staying useful until BMR actually requires movement."));
            }
        }
        else if (metrics.NormalPressureOutOfRangeSeconds > 0)
        {
            signals.Add(new UptimeSignal(
                "normal-profile-bmr-context",
                MathF.Min(metrics.NormalPressureOutOfRangeSeconds, 5f),
                metrics.NormalPressureOutOfRangeSeconds,
                $"Normal profile had {metrics.NormalPressureOutOfRangeSeconds:0.0}s out of range while BMR pressure was active; this is safety context, not automatically uptime failure.",
                "Do not chase uptime through normal-profile BMR safety movement; review recovery after pressure clears."));
        }

        if (profile.IsHealer && metrics.HealerPartyCoveragePercent >= 80f)
        {
            signals.Add(new UptimeSignal(
                "healer-party-coverage",
                metrics.HealerPartyCoveragePercent / 10f,
                metrics.TargetObservedSeconds,
                $"Visible party coverage averaged {metrics.HealerPartyCoveragePercent:0}%.",
                "Preserve healer positions that keep target uptime while maintaining party heal reach."));
        }

        return signals;
    }

    private static IReadOnlyList<UptimeSignal> BuildNegativeSignals(XcaiHeader header, UptimeJobProfile profile, UptimeMetrics metrics)
    {
        var signals = new List<UptimeSignal>();
        if (metrics.AvoidableOutOfRangeSeconds >= 1f)
        {
            signals.Add(new UptimeSignal(
                "avoidable-target-downtime",
                metrics.AvoidableOutOfRangeSeconds * 3f,
                metrics.AvoidableOutOfRangeSeconds,
                $"Observed {metrics.AvoidableOutOfRangeSeconds:0.0}s outside usable target range when BMR/manual context did not explain it.",
                "Generate or retain a safe re-engage plan sooner so RSR has an actionable target range."));
        }

        if (profile.PrefersMeleeRange && metrics.TargetObservedSeconds > 0)
        {
            var fallbackRatio = metrics.FallbackUptimeSeconds / metrics.TargetObservedSeconds;
            if (fallbackRatio >= 0.25f)
            {
                signals.Add(new UptimeSignal(
                    "melee-ranged-fallback-missed-gcd",
                    metrics.FallbackUptimeSeconds,
                    metrics.FallbackUptimeSeconds,
                    $"Melee/tank spent {fallbackRatio:P0} of target time in ranged fallback range instead of melee range.",
                    "Treat ranged fallback as a missed melee uptime window; it avoids total inactivity but should not count as successful melee uptime."));
            }
        }

        if (metrics.PackOpportunitySeconds >= 1f && metrics.PackHitEfficiencyPercent < 75f)
        {
            signals.Add(new UptimeSignal(
                "trash-pack-hit-loss",
                (100f - metrics.PackHitEfficiencyPercent) / 10f,
                metrics.PackOpportunitySeconds,
                $"Trash AoE hit efficiency was {metrics.PackHitEfficiencyPercent:0}% across {metrics.PackOpportunitySeconds:0.0}s of pack opportunities.",
                "Prefer safe pack positions and targets that increase hit count without disrupting BMR safety."));
        }

        if (profile.IsHealer && metrics.HealerPartyCoveragePercent < 70f)
        {
            signals.Add(new UptimeSignal(
                "healer-party-coverage-loss",
                (70f - metrics.HealerPartyCoveragePercent) / 5f,
                metrics.TargetObservedSeconds,
                $"Visible party coverage averaged {metrics.HealerPartyCoveragePercent:0}%.",
                "For healers, score target uptime together with reachable party coverage so DPS uptime does not strand healing range."));
        }

        if (IsGreedStyle(header.CombatStyle) && metrics.GreedPressureSeconds >= 1f)
        {
            var ratio = metrics.GreedPressureInRangeSeconds / MathF.Max(1f, metrics.GreedPressureSeconds);
            if (ratio < 0.55f)
            {
                signals.Add(new UptimeSignal(
                    "greed-pressure-uptime-loss",
                    (metrics.GreedPressureSeconds - metrics.GreedPressureInRangeSeconds) * 2f,
                    metrics.GreedPressureSeconds - metrics.GreedPressureInRangeSeconds,
                    $"Greed profile kept usable range for only {ratio:P0} of BMR-pressure time.",
                    "When BMR Greed timing allows staying in, avoid moving out early unless BMR forced movement or manual input requires it."));
            }
        }

        return signals;
    }

    private static IReadOnlyList<UptimeSegment> BuildSegments(XcaiHeader header, UptimeJobProfile profile, IReadOnlyList<XcaiFrame> frames, IReadOnlyList<float> durations)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var segments = new List<UptimeSegment>();
        var start = 0;
        var key = SegmentKey(header, profile, frames[0]);
        for (var i = 1; i < frames.Count; i++)
        {
            var nextKey = SegmentKey(header, profile, frames[i]);
            if (nextKey.Equals(key))
            {
                continue;
            }

            AddSegment(header, profile, frames, durations, segments, start, i - 1, key);
            start = i;
            key = nextKey;
        }

        AddSegment(header, profile, frames, durations, segments, start, frames.Count - 1, key);
        return segments
            .Where(segment => segment.DurationSeconds >= 1f || segment.TargetUptimeValue < 0.75f || segment.PackHitValue < 0.75f)
            .OrderBy(segment => segment.StartT)
            .Take(60)
            .ToArray();
    }

    private static void AddSegment(
        XcaiHeader header,
        UptimeJobProfile profile,
        IReadOnlyList<XcaiFrame> frames,
        IReadOnlyList<float> durations,
        ICollection<UptimeSegment> segments,
        int start,
        int end,
        UptimeSegmentKey key)
    {
        var duration = 0f;
        var targetValue = 0f;
        var packValue = 0f;
        var packSeconds = 0f;
        for (var i = start; i <= end; i++)
        {
            var state = ClassifyFrame(header, profile, frames[i]);
            duration += durations[i];
            targetValue += state.TargetUptimeValue * durations[i];
            if (state.PackOpportunity)
            {
                packValue += state.PackHitValue * durations[i];
                packSeconds += durations[i];
            }
        }

        segments.Add(new UptimeSegment(
            frames[start].T,
            frames[end].T,
            duration,
            key.PlannerSource,
            key.TargetState,
            key.PackState,
            key.BmrPressure,
            duration > 0 ? targetValue / duration : 1f,
            packSeconds > 0 ? packValue / packSeconds : 1f));
    }

    private static UptimeSegmentKey SegmentKey(XcaiHeader header, UptimeJobProfile profile, XcaiFrame frame)
    {
        var state = ClassifyFrame(header, profile, frame);
        var targetState = !state.TargetObserved
            ? "no-target"
            : state.InPreferredRange
                ? "preferred-range"
                : state.InFallbackRange
                    ? "fallback-range"
                    : state.AvoidableOutOfRange ? "avoidable-out-of-range" : "context-out-of-range";
        var packState = !state.PackOpportunity
            ? "no-pack-opportunity"
            : state.PackHitValue >= 0.85f ? "good-pack-hits" : "lost-pack-hits";

        return new UptimeSegmentKey(
            string.IsNullOrWhiteSpace(frame.Planner.ChosenSource) ? "<none>" : frame.Planner.ChosenSource,
            targetState,
            packState,
            state.BmrPressure);
    }

    private static float? TargetSurfaceDistance(XcaiFrame frame)
    {
        if (frame.Motion.TargetSurfaceDistance.HasValue)
        {
            return frame.Motion.TargetSurfaceDistance.Value;
        }

        return frame.PlayerPosition != null && frame.TargetPosition != null
            ? MathF.Max(0f, Vec3.Distance2D(frame.PlayerPosition, frame.TargetPosition) - frame.TargetRadius)
            : null;
    }

    private static bool IsPackOpportunity(XcaiFrame frame)
    {
        return frame.BestHits >= 2 ||
               frame.CurrentHits >= 2 ||
               (frame.PackTargetCount >= 3 && !frame.ActionName.Equals("<none>", StringComparison.Ordinal));
    }

    private static float PackHitValue(XcaiFrame frame)
    {
        var best = Math.Max(frame.BestHits, frame.CurrentHits);
        if (best <= 1)
        {
            return 1f;
        }

        return Math.Clamp(frame.CurrentHits / (float)best, 0f, 1f);
    }

    private static float? HealerPartyCoverage(XcaiFrame frame)
    {
        if (frame.PlayerPosition == null)
        {
            return null;
        }

        var party = frame.Actors
            .Where(actor => actor.Relation.Equals("party", StringComparison.Ordinal))
            .ToArray();
        if (party.Length == 0)
        {
            return null;
        }

        var covered = party.Count(actor => Vec3.Distance2D(frame.PlayerPosition, actor.Position) <= HealerPartyRange);
        return covered / (float)party.Length;
    }

    private static bool HasBmrPressure(XcaiFrame frame)
    {
        return frame.Planner.BmrForcedMovement != null ||
               frame.Planner.BmrForbiddenZones > 0 ||
               frame.Planner.BmrMoveRequested ||
               frame.Planner.BmrMoveImminent ||
               frame.BossMod.ForbiddenZones.GetValueOrDefault() > 0 ||
               IsSpecialBmrMode(frame.BossMod.ImminentSpecialMode);
    }

    private static bool HasForcedBmrMovement(XcaiFrame frame)
    {
        return frame.Planner.BmrForcedMovement != null ||
               frame.Planner.BmrMoveRequested ||
               frame.Planner.BmrMoveImminent;
    }

    private static bool IsSpecialBmrMode(string mode)
    {
        return !string.IsNullOrWhiteSpace(mode) &&
               mode != "<none>" &&
               !mode.StartsWith("(Normal,", StringComparison.Ordinal);
    }

    private static bool IsGreedStyle(string combatStyle)
    {
        return combatStyle.StartsWith("Greed", StringComparison.OrdinalIgnoreCase);
    }

    private static float[] EstimateFrameDurations(XcaiHeader header, IReadOnlyList<XcaiFrame> frames)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var durations = new float[frames.Count];
        for (var i = 0; i < frames.Count - 1; i++)
        {
            durations[i] = Math.Max(0f, frames[i + 1].T - frames[i].T);
        }

        durations[^1] = frames.Count > 1 && durations[^2] <= 2f
            ? durations[^2]
            : Math.Max(0f, header.DurationSeconds - frames[^1].T);
        return durations;
    }

    private static float Percent(float value, float total)
    {
        return total > 0 ? Math.Clamp(value / total * 100f, 0f, 100f) : 100f;
    }

    private static float ClampScore(float value)
    {
        return Math.Clamp(value, 0f, 100f);
    }

    private sealed record UptimeFrameState(
        bool TargetObserved,
        bool InAnyRange,
        bool InPreferredRange,
        bool InFallbackRange,
        float TargetUptimeValue,
        bool AvoidableOutOfRange,
        bool BmrPressure,
        bool ForcedMovement,
        bool GreedStyle,
        bool PackOpportunity,
        float PackHitValue,
        float? HealerPartyCoverage);

    private sealed record UptimeSegmentKey(
        string PlannerSource,
        string TargetState,
        string PackState,
        bool BmrPressure);
}

internal sealed record UptimeAnalysis(
    UptimeJobProfile Job,
    UptimeMetrics Metrics,
    IReadOnlyList<UptimeSignal> PositiveSignals,
    IReadOnlyList<UptimeSignal> NegativeSignals,
    IReadOnlyList<UptimeSegment> Segments);

internal sealed record UptimeJobProfile(
    string Role,
    bool PrefersMeleeRange,
    bool IsHealer,
    float PreferredSurfaceRange,
    float FallbackSurfaceRange,
    float FallbackUptimeValue)
{
    public static UptimeJobProfile For(uint classJobId)
    {
        return classJobId switch
        {
            1 or 19 or 3 or 21 or 32 or 37 => new("tank", true, false, MeleePreferredRange, MeleeFallbackRange, 0.55f),
            2 or 20 or 4 or 22 or 29 or 30 or 34 or 39 or 41 => new("melee-dps", true, false, MeleePreferredRange, MeleeFallbackRange, 0.55f),
            6 or 24 or 28 or 33 or 40 => new("healer", false, true, RangedPreferredRange, RangedPreferredRange, 1.0f),
            5 or 23 or 31 or 38 => new("physical-ranged", false, false, RangedPreferredRange, RangedPreferredRange, 1.0f),
            7 or 25 or 26 or 27 or 35 or 36 or 42 => new("magic-ranged", false, false, RangedPreferredRange, RangedPreferredRange, 1.0f),
            _ => new("unknown", false, false, RangedPreferredRange, RangedPreferredRange, 1.0f)
        };
    }

    private const float MeleePreferredRange = 3.0f;
    private const float MeleeFallbackRange = 15.0f;
    private const float RangedPreferredRange = 25.0f;
}

internal sealed record UptimeMetrics(
    float UptimeScore,
    float TargetObservedSeconds,
    float AnyTargetUptimeSeconds,
    float PreferredUptimeSeconds,
    float FallbackUptimeSeconds,
    float WeightedTargetUptimeSeconds,
    float OutOfRangeSeconds,
    float AvoidableOutOfRangeSeconds,
    float BmrPressureSeconds,
    float BmrPressureInRangeSeconds,
    float GreedPressureSeconds,
    float GreedPressureInRangeSeconds,
    float NormalPressureOutOfRangeSeconds,
    float PackOpportunitySeconds,
    float WeightedPackHitSeconds,
    float AnyTargetUptimeRatio,
    float PreferredUptimeRatio,
    float TargetWeightedUptimePercent,
    float PackHitEfficiencyPercent,
    float HealerPartyCoveragePercent);

internal sealed record UptimeSignal(
    string Category,
    float Weight,
    float Seconds,
    string Evidence,
    string SuggestedGoal);

internal sealed record UptimeSegment(
    float StartT,
    float EndT,
    float DurationSeconds,
    string PlannerSource,
    string TargetState,
    string PackState,
    bool BmrPressure,
    float TargetUptimeValue,
    float PackHitValue);
