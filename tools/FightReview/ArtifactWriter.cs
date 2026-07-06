using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FightReview;

internal static class ArtifactWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Write(ReviewBundle review, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteAgentImprovement(review, Path.Combine(outputDirectory, "agent.improvement.json"));
    }

    private static void WriteAgentImprovement(ReviewBundle review, string path)
    {
        var uptime = UptimeScoring.Analyze(review.Xcai);
        var runScore = BuildRunScore(review, uptime);
        var sourceSummary = BuildSourceSummary(review.Xcai.Frames);
        var categoryScores = review.Incidents
            .GroupBy(incident => incident.Category, StringComparer.Ordinal)
            .Select(group => new
            {
                Category = group.Key,
                Count = group.Count(),
                Score = group.Sum(incident => IncidentWeight(incident.Severity)),
                HighestSeverity = HighestSeverity(group.Select(incident => incident.Severity))
            })
            .OrderByDescending(category => category.Score)
            .ThenBy(category => category.Category, StringComparer.Ordinal)
            .ToArray();
        var improvementCandidates = review.Incidents
            .GroupBy(incident => incident.SuggestedGoal, StringComparer.Ordinal)
            .Select(group => new
            {
                Priority = CandidatePriority(group),
                Goal = group.Key,
                TotalScore = group.Sum(incident => IncidentWeight(incident.Severity)),
                IncidentCount = group.Count(),
                Categories = group.Select(incident => incident.Category).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                FirstOccurrenceT = group.Min(incident => incident.T),
                CodeAreas = group.SelectMany(incident => CodeAreasForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                TestFocus = group.SelectMany(incident => TestFocusForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                Evidence = group.OrderBy(incident => incident.T).Select(incident => new
                {
                    incident.Id,
                    incident.Category,
                    incident.T,
                    incident.Severity,
                    incident.Evidence,
                    Frames = BuildAgentFrameSlice(review, incident)
                }).ToArray()
            })
            .OrderBy(candidate => CandidatePriorityRank(candidate.Priority))
            .ThenByDescending(candidate => candidate.TotalScore)
            .ThenBy(candidate => candidate.FirstOccurrenceT)
            .ToArray();

        var packet = new
        {
            Type = "agent.improvement",
            SchemaVersion = 1,
            GeneratedUtc = DateTime.UtcNow,
            Review = new
            {
                XcaiLog = review.Xcai.Path,
                review.Xcai.Header.LogScope,
                review.Xcai.Header.RunStartUtc,
                review.Xcai.Header.RunEndUtc,
                review.Xcai.Header.CombatStartUtc,
                review.Xcai.Header.DurationSeconds,
                Job = review.Xcai.Header.PlayerClassJobId,
                review.Xcai.Header.TerritoryType,
                review.Xcai.Header.ContentFinderConditionId,
                review.Xcai.Header.BossModActiveModule,
                review.Xcai.Header.CombatStyle,
                SourceSummary = sourceSummary
            },
            Scores = new
            {
                IncidentCount = review.Incidents.Count,
                HighIncidentCount = review.Incidents.Count(incident => incident.Severity.Equals("high", StringComparison.OrdinalIgnoreCase)),
                MediumIncidentCount = review.Incidents.Count(incident => incident.Severity.Equals("medium", StringComparison.OrdinalIgnoreCase)),
                LowIncidentCount = review.Incidents.Count(incident => incident.Severity.Equals("low", StringComparison.OrdinalIgnoreCase)),
                CategoryScores = categoryScores,
                RunScore = runScore,
                Uptime = uptime.Metrics
            },
            Objectives = new
            {
                Primary = "Maximize BossMod-safe uptime by keeping RSR in useful target range.",
                Rules = new[]
                {
                    "Melee/tank ranged fallback avoids total inactivity but is still missed melee uptime.",
                    "Trash pack movement should improve AoE hit count while preserving ABC.",
                    "Healer uptime includes both target access and visible party heal coverage.",
                    "Normal profile BMR pressure is safety context; Greed profiles should stay useful until BMR requires movement."
                }
            },
            PositiveSignals = uptime.PositiveSignals.OrderByDescending(signal => signal.Weight).ToArray(),
            UptimeNegativeSignals = uptime.NegativeSignals.OrderByDescending(signal => signal.Weight).ToArray(),
            NegativeSignals = review.Incidents.OrderBy(incident => incident.T).Select(incident => new
            {
                incident.Id,
                incident.Category,
                incident.T,
                incident.Severity,
                incident.Evidence,
                incident.SuggestedGoal,
                Frames = BuildAgentFrameSlice(review, incident)
            }).ToArray(),
            ImprovementCandidates = improvementCandidates,
            RouteSegments = BuildAgentRouteSegments(review),
            UptimeSegments = uptime.Segments,
            CodeAreas = review.Incidents.SelectMany(incident => CodeAreasForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            TestFocus = review.Incidents.SelectMany(incident => TestFocusForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(packet, PrettyJsonOptions) + Environment.NewLine, Utf8NoBom);
    }

    private static object[] BuildAgentFrameSlice(ReviewBundle review, Incident incident)
    {
        var start = Math.Clamp(incident.StartFrame, 0, Math.Max(0, review.Xcai.Frames.Count - 1));
        var end = Math.Clamp(incident.EndFrame, start, Math.Max(start, review.Xcai.Frames.Count - 1));
        return review.Xcai.Frames
            .Skip(start)
            .Take(Math.Min(16, end - start + 1))
            .Select(frame => new
            {
                frame.T,
                frame.PlayerPosition,
                frame.TargetPosition,
                frame.TargetBaseId,
                frame.TargetObjectId,
                frame.AutomatedMovementSuppressed,
                frame.ManualMovementInput,
                Planner = new
                {
                    frame.Planner.ChosenSource,
                    frame.Planner.Destination,
                    frame.Planner.FirstWaypoint,
                    frame.Planner.SwitchReason,
                    frame.Planner.SuppressionReason,
                    frame.Planner.PathStatus,
                    frame.Planner.BmrForcedMovement,
                    frame.Planner.BmrForbiddenZones,
                    frame.Planner.BmrMoveRequested,
                    frame.Planner.BmrMoveImminent,
                    frame.Planner.RejectedByReason
                },
                BossMod = new
                {
                    frame.BossMod.MovementOverride,
                    frame.BossMod.HintSummary,
                    frame.BossMod.PlannerSteer,
                    frame.BossMod.MechanicWhisper,
                    frame.BossMod.GoalZones,
                    frame.BossMod.ForbiddenZones,
                    frame.BossMod.ImminentSpecialMode,
                    SafetyPlayer = frame.BossMod.SafetyRaster.Player.State,
                    SafetyDestination = frame.BossMod.SafetyRaster.Destination.State,
                    SafetyWaypoint = frame.BossMod.SafetyRaster.FirstWaypoint.State
                },
                frame.TrashPull,
                frame.Mobility,
                frame.Motion,
                frame.PackTargetCount,
                frame.CurrentHits,
                frame.BestHits,
                frame.ActionName,
                frame.ActionShape
            })
            .Cast<object>()
            .ToArray();
    }

    private static AgentRunScore BuildRunScore(ReviewBundle review, UptimeAnalysis uptime)
    {
        var frames = review.Xcai.Frames;
        var durations = EstimateFrameDurations(review.Xcai.Header, frames);
        var totalSeconds = Math.Max(review.Xcai.Header.DurationSeconds, durations.Sum());
        var combatSeconds = SumSeconds(frames, durations, frame => frame.InCombat);
        var activeMovementSeconds = SumSeconds(frames, durations, HasActiveDestination);
        var manualSuppressedSeconds = SumSeconds(frames, durations, frame => frame.AutomatedMovementSuppressed);
        var bmrPressureSeconds = SumSeconds(frames, durations, HasBmrPressure);
        var generatedAverage = frames.Count == 0 ? 0f : (float)frames.Average(frame => frame.Planner.GeneratedCount);
        var acceptedAverage = frames.Count == 0 ? 0f : (float)frames.Average(frame => frame.Planner.AcceptedCount);
        var routeBudgetAverage = frames.Count == 0 ? 0f : (float)frames.Average(frame => frame.Planner.RouteMemory.QueryBudgetUsed);
        var queryPendingRatio = frames.Count == 0 ? 0f : frames.Count(frame => frame.Planner.Vnavmesh.PathfindInProgress == true) / (float)frames.Count;
        var frameRate = totalSeconds > 0 ? frames.Count / totalSeconds : 0f;
        var destinationChangesPerMinute = totalSeconds > 0
            ? CountDestinationChanges(frames, 1.25f) / (totalSeconds / 60f)
            : 0f;

        var safetyPenalty = review.Incidents.Where(incident => IsSafetyIncident(incident.Category)).Sum(incident => IncidentWeight(incident.Severity));
        var efficiencyPenalty = review.Incidents.Where(incident => IsEfficiencyIncident(incident.Category)).Sum(incident => IncidentWeight(incident.Severity));
        var humanPenalty = review.Incidents.Where(incident => IsHumanIncident(incident.Category)).Sum(incident => IncidentWeight(incident.Severity));
        var manualCorrectionCount = review.Incidents.Count(incident => incident.Category.Equals("manual-correction", StringComparison.Ordinal));
        var resourcePenalty = MathF.Max(0f, generatedAverage - 10f) * 2f +
                              MathF.Max(0f, routeBudgetAverage - 1.5f) * 10f +
                              queryPendingRatio * 20f +
                              review.Incidents.Where(incident => incident.Category.Contains("gap-close", StringComparison.OrdinalIgnoreCase)).Sum(incident => IncidentWeight(incident.Severity)) * 5f +
                              MathF.Max(0f, frameRate - 4.5f) * 5f;

        var safety = ClampScore(100f - (safetyPenalty * 11f));
        var uptimeScore = uptime.Metrics.UptimeScore;
        var efficiency = ClampScore(100f - (efficiencyPenalty * 8f) - MathF.Max(0f, (activeMovementSeconds / MathF.Max(1f, combatSeconds)) - 0.45f) * 20f);
        var humanLikeness = ClampScore(100f - (humanPenalty * 9f) - (manualCorrectionCount * 6f) - MathF.Max(0f, destinationChangesPerMinute - 8f) * 1.5f);
        var resourceDiscipline = ClampScore(100f - resourcePenalty);
        var overall = ClampScore((uptimeScore * 0.35f) + (safety * 0.25f) + (efficiency * 0.20f) + (humanLikeness * 0.15f) + (resourceDiscipline * 0.05f));

        return new AgentRunScore(
            overall,
            uptimeScore,
            safety,
            efficiency,
            humanLikeness,
            resourceDiscipline,
            "Higher is better. Uptime is the primary positive signal, with BMR safety authority preserved and candidate/query cost kept bounded.",
            new AgentRunMetrics(
                totalSeconds,
                combatSeconds,
                MathF.Max(0f, totalSeconds - combatSeconds),
                activeMovementSeconds,
                manualSuppressedSeconds,
                bmrPressureSeconds,
                frameRate,
                generatedAverage,
                acceptedAverage,
                routeBudgetAverage,
                queryPendingRatio,
                destinationChangesPerMinute,
                manualCorrectionCount),
            BuildRunPenalties(safetyPenalty, efficiencyPenalty, humanPenalty, resourcePenalty, uptimeScore));
    }

    private static SourceUsageSummary BuildSourceSummary(IReadOnlyList<XcaiFrame> frames)
    {
        var positionalRsr = 0;
        var positionalNone = 0;
        var aoeRsr = 0;
        var aoeLocal = 0;
        var mobilityChecked = 0;
        var mobilityBmrIpc = 0;
        var mobilityBmrReflection = 0;
        var mobilityLocal = 0;
        var facingChecked = 0;
        var facingBmrIpc = 0;
        var facingBmrReflection = 0;
        var facingLocal = 0;
        var redMageChecked = 0;
        var redMageRsr = 0;
        var redMageNone = 0;
        var targetUptimeChecked = 0;
        var targetUptimeRsr = 0;
        var targetUptimeLocal = 0;
        var targetUptimeNone = 0;
        var trueNorthChecked = 0;
        var trueNorthRsr = 0;
        var trueNorthLocal = 0;
        var trueNorthNone = 0;

        foreach (var frame in frames)
        {
            if (frame.PositionalIntentSource.Equals("RSR reflected", StringComparison.Ordinal))
            {
                positionalRsr++;
            }
            else if (frame.PositionalIntentSource.Equals("none", StringComparison.Ordinal))
            {
                positionalNone++;
            }

            if (!frame.TrueNorthDecisionSource.Equals("none", StringComparison.Ordinal))
            {
                trueNorthChecked++;
            }

            if (frame.TrueNorthDecisionSource.Equals("RSR reflected", StringComparison.Ordinal))
            {
                trueNorthRsr++;
            }
            else if (frame.TrueNorthDecisionSource.Contains("local", StringComparison.Ordinal))
            {
                trueNorthLocal++;
            }
            else if (frame.TrueNorthDecisionSource.Equals("none", StringComparison.Ordinal))
            {
                trueNorthNone++;
            }

            if (frame.ActionSource.Equals("RSR reflected", StringComparison.Ordinal))
            {
                aoeRsr++;
            }
            else if (frame.ActionSource.Equals("local", StringComparison.Ordinal))
            {
                aoeLocal++;
            }

            if (!frame.Mobility.SafetySource.Equals("none", StringComparison.Ordinal))
            {
                mobilityChecked++;
            }

            if (frame.Mobility.SafetySource.Contains("BMR IPC", StringComparison.Ordinal))
            {
                mobilityBmrIpc++;
            }

            if (frame.Mobility.SafetySource.Contains("BMR reflection fallback", StringComparison.Ordinal))
            {
                mobilityBmrReflection++;
            }

            if (frame.Mobility.SafetySource.Contains("local", StringComparison.Ordinal))
            {
                mobilityLocal++;
            }

            if (!frame.Facing.SafetySource.Equals("none", StringComparison.Ordinal))
            {
                facingChecked++;
            }

            if (frame.Facing.SafetySource.Contains("BMR IPC", StringComparison.Ordinal))
            {
                facingBmrIpc++;
            }

            if (frame.Facing.SafetySource.Contains("BMR reflection fallback", StringComparison.Ordinal))
            {
                facingBmrReflection++;
            }

            if (frame.Facing.SafetySource.Contains("local", StringComparison.Ordinal))
            {
                facingLocal++;
            }

            if (!frame.RedMageMelee.NextActionSource.Equals("none", StringComparison.Ordinal))
            {
                redMageChecked++;
            }

            if (frame.RedMageMelee.NextActionSource.Equals("RSR reflected", StringComparison.Ordinal))
            {
                redMageRsr++;
            }
            else if (frame.RedMageMelee.NextActionSource.Equals("none", StringComparison.Ordinal))
            {
                redMageNone++;
            }

            if (!frame.TargetUptimeRangeSource.Equals("none", StringComparison.Ordinal))
            {
                targetUptimeChecked++;
            }

            if (frame.TargetUptimeRangeSource.Equals("RSR reflected", StringComparison.Ordinal))
            {
                targetUptimeRsr++;
            }
            else if (frame.TargetUptimeRangeSource.Contains("local", StringComparison.Ordinal))
            {
                targetUptimeLocal++;
            }
            else if (frame.TargetUptimeRangeSource.Equals("none", StringComparison.Ordinal))
            {
                targetUptimeNone++;
            }
        }

        return new SourceUsageSummary(
            frames.Count,
            positionalRsr,
            positionalNone,
            aoeRsr,
            aoeLocal,
            mobilityChecked,
            mobilityBmrIpc,
            mobilityBmrReflection,
            mobilityLocal,
            facingChecked,
            facingBmrIpc,
            facingBmrReflection,
            facingLocal,
            redMageChecked,
            redMageRsr,
            redMageNone,
            targetUptimeChecked,
            targetUptimeRsr,
            targetUptimeLocal,
            targetUptimeNone,
            trueNorthChecked,
            trueNorthRsr,
            trueNorthLocal,
            trueNorthNone);
    }

    private static IReadOnlyList<AgentRunPenalty> BuildRunPenalties(int safetyPenalty, int efficiencyPenalty, int humanPenalty, float resourcePenalty, float uptimeScore)
    {
        return new[]
            {
                new AgentRunPenalty("uptime", 100f - uptimeScore, "Lost target range, melee comfort, pack hit value, or healer party coverage."),
                new AgentRunPenalty("safety", safetyPenalty, "BMR conflicts, blocked routes, unsafe destinations, or stuck movement."),
                new AgentRunPenalty("efficiency", efficiencyPenalty, "Range loss, late trash engagement, slow pack follow, or missed AoE value."),
                new AgentRunPenalty("human-likeness", humanPenalty, "Destination churn, oscillation, jitter, edge hugging, or manual corrections."),
                new AgentRunPenalty("resource", resourcePenalty, "High candidate count, vnavmesh query pressure, pending queries, or excessive frame rate.")
            }
            .Where(penalty => penalty.WeightedPenalty > 0)
            .ToArray();
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

    private static float SumSeconds(IReadOnlyList<XcaiFrame> frames, IReadOnlyList<float> durations, Func<XcaiFrame, bool> predicate)
    {
        var total = 0f;
        for (var i = 0; i < frames.Count; i++)
        {
            if (predicate(frames[i]))
            {
                total += durations[i];
            }
        }

        return total;
    }

    private static int CountDestinationChanges(IReadOnlyList<XcaiFrame> frames, float threshold)
    {
        Vec3? previous = null;
        var changes = 0;
        foreach (var frame in frames)
        {
            var destination = frame.Planner.Destination;
            if (destination == null)
            {
                continue;
            }

            if (previous != null && Vec3.Distance2D(destination, previous) >= threshold)
            {
                changes++;
            }

            previous = destination;
        }

        return changes;
    }

    private static bool HasActiveDestination(XcaiFrame frame)
    {
        return frame.Planner.Destination != null && frame.Planner.ChosenSource != "<none>";
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

    private static bool IsSpecialBmrMode(string mode)
    {
        return !string.IsNullOrWhiteSpace(mode) &&
               mode != "<none>" &&
               !mode.StartsWith("(Normal,", StringComparison.Ordinal);
    }

    private static bool IsSafetyIncident(string category)
    {
        return category.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("bmr", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("unsafe", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("offmesh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEfficiencyIncident(string category)
    {
        return category.Contains("range", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("late", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("slow", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("aoe", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("gap-close", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("pack", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("tank", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("route-memory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHumanIncident(string category)
    {
        return category.Contains("churn", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("oscillation", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("jitter", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("manual", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("overcommit", StringComparison.OrdinalIgnoreCase);
    }

    private static float ClampScore(float value)
    {
        return Math.Clamp(value, 0f, 100f);
    }

    private static IReadOnlyList<AgentRouteSegment> BuildAgentRouteSegments(ReviewBundle review)
    {
        var frames = review.Xcai.Frames;
        if (frames.Count == 0)
        {
            return [];
        }

        var segments = new List<AgentRouteSegment>();
        var start = 0;
        var source = SegmentSource(frames[0]);
        for (var i = 1; i < frames.Count; i++)
        {
            var nextSource = SegmentSource(frames[i]);
            if (nextSource.Equals(source, StringComparison.Ordinal))
            {
                continue;
            }

            AddAgentRouteSegment(review, segments, start, i - 1, source);
            start = i;
            source = nextSource;
        }

        AddAgentRouteSegment(review, segments, start, frames.Count - 1, source);
        return segments
            .Where(segment => segment.DurationSeconds >= 1f || segment.IncidentCount > 0)
            .OrderBy(segment => segment.StartT)
            .Take(40)
            .ToArray();
    }

    private static void AddAgentRouteSegment(
        ReviewBundle review,
        ICollection<AgentRouteSegment> segments,
        int start,
        int end,
        string source)
    {
        var frames = review.Xcai.Frames;
        var startT = frames[start].T;
        var endT = end + 1 < frames.Count
            ? frames[end + 1].T
            : review.Xcai.Header.DurationSeconds;
        var incidents = review.Incidents
            .Where(incident => incident.T >= startT && incident.T <= endT)
            .ToArray();
        var switchReasons = frames
            .Skip(start)
            .Take(end - start + 1)
            .Select(frame => frame.Planner.SwitchReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason) && !reason.Equals("<none>", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .Take(6)
            .ToArray();
        var rejectionReasons = frames
            .Skip(start)
            .Take(end - start + 1)
            .SelectMany(frame => frame.Planner.RejectedByReason)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .OrderByDescending(group => group.Sum(entry => entry.Value))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(6)
            .Select(group => $"{group.Key}:{group.Sum(entry => entry.Value)}")
            .ToArray();

        segments.Add(new AgentRouteSegment(
            startT,
            endT,
            MathF.Max(0f, endT - startT),
            source,
            end - start + 1,
            incidents.Length,
            incidents.Select(incident => incident.Category).Distinct(StringComparer.Ordinal).OrderBy(category => category, StringComparer.Ordinal).ToArray(),
            switchReasons,
            rejectionReasons));
    }

    private static string SegmentSource(XcaiFrame frame)
    {
        return string.IsNullOrWhiteSpace(frame.Planner.ChosenSource)
            ? "<none>"
            : frame.Planner.ChosenSource;
    }

    private static string CandidatePriority(IGrouping<string, Incident> incidents)
    {
        var score = incidents.Sum(incident => IncidentWeight(incident.Severity));
        return incidents.Any(incident => incident.Severity.Equals("high", StringComparison.OrdinalIgnoreCase)) || score >= 6
            ? "high"
            : incidents.Any(incident => incident.Severity.Equals("medium", StringComparison.OrdinalIgnoreCase)) || score >= 3 ? "medium" : "low";
    }

    private static int CandidatePriorityRank(string priority)
    {
        return priority.Equals("high", StringComparison.OrdinalIgnoreCase)
            ? 0
            : priority.Equals("medium", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static int IncidentWeight(string severity)
    {
        return severity.Equals("high", StringComparison.OrdinalIgnoreCase)
            ? 3
            : severity.Equals("medium", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    private static string HighestSeverity(IEnumerable<string> severities)
    {
        var max = severities.Select(IncidentWeight).DefaultIfEmpty(0).Max();
        return max >= 3 ? "high" : max >= 2 ? "medium" : "low";
    }

    private static IEnumerable<string> CodeAreasForIncident(string category)
    {
        if (category.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("bmr", StringComparison.OrdinalIgnoreCase))
        {
            yield return "XelsCombatAI/Integrations/BossModGoalZoneHook.cs";
            yield return "XelsCombatAI/Integrations/BossModReflectionSafety.cs";
        }

        if (category.Contains("vnavmesh", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("stuck", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("churn", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("jitter", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("range", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("manual", StringComparison.OrdinalIgnoreCase))
        {
            yield return "XelsCombatAI/Runtime/BossModPresetController.cs";
            yield return "XelsCombatAI/Integrations/BossModGoalZoneHook.cs";
        }

        if (category.Contains("trash", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("tank", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("pack", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("route-memory", StringComparison.OrdinalIgnoreCase))
        {
            yield return "XelsCombatAI/Combat/AoePackPositioningController.cs";
            yield return "XelsCombatAI/Combat/TrashPullStateTracker.cs";
            yield return "XelsCombatAI/Integrations/BossModGoalZoneHook.cs";
        }

        if (category.Contains("aoe", StringComparison.OrdinalIgnoreCase))
        {
            yield return "XelsCombatAI/Combat/AoePackPositioningController.cs";
            yield return "XelsCombatAI/Combat/HealerAoePositioningController.cs";
            yield return "XelsCombatAI/Game/JobRangeProvider.cs";
        }

        if (category.Contains("gap-close", StringComparison.OrdinalIgnoreCase))
        {
            yield return "XelsCombatAI/Combat/GapCloserController.cs";
            yield return "XelsCombatAI/Combat/GapCloserDecisionPolicy.cs";
            yield return "XelsCombatAI/Combat/EscapeGapCloserController.cs";
            yield return "XelsCombatAI/Runtime/CombatHistory.cs";
        }

        yield return "tools/FightReview/IncidentDetector.cs";
    }

    private static IEnumerable<string> TestFocusForIncident(string category)
    {
        yield return "tools/FightReview.Tests detector fixture for this incident category";

        if (category.Contains("churn", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("jitter", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Movement intent retention and destination hysteresis";
        }

        if (category.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("bmr", StringComparison.OrdinalIgnoreCase))
        {
            yield return "BossMod safety raster destination and route validation";
        }

        if (category.Contains("trash", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("pack", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("tank", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Trash pull tracker and pack engagement policy";
        }

        if (category.Contains("manual", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Manual suppression lookback slice review";
        }

        if (category.Contains("gap-close", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Gap closer value, charge conservation, and current-GCD timing policy";
        }
    }

    private sealed record AgentRouteSegment(
        float StartT,
        float EndT,
        float DurationSeconds,
        string Source,
        int FrameCount,
        int IncidentCount,
        IReadOnlyList<string> IncidentCategories,
        IReadOnlyList<string> SwitchReasons,
        IReadOnlyList<string> RejectionReasons);

    private sealed record AgentRunScore(
        float Overall,
        float Uptime,
        float Safety,
        float Efficiency,
        float HumanLikeness,
        float ResourceDiscipline,
        string Interpretation,
        AgentRunMetrics Metrics,
        IReadOnlyList<AgentRunPenalty> Penalties);

    private sealed record SourceUsageSummary(
        int FrameCount,
        int PositionalRsrReflectedFrames,
        int PositionalNoneFrames,
        int AoeRsrReflectedFrames,
        int AoeLocalFrames,
        int MobilityCheckedFrames,
        int MobilityBmrIpcFrames,
        int MobilityBmrReflectionFallbackFrames,
        int MobilityLocalFrames,
        int FacingCheckedFrames,
        int FacingBmrIpcFrames,
        int FacingBmrReflectionFallbackFrames,
        int FacingLocalFrames,
        int RedMageCheckedFrames,
        int RedMageRsrReflectedFrames,
        int RedMageNoneFrames,
        int TargetUptimeCheckedFrames,
        int TargetUptimeRsrReflectedFrames,
        int TargetUptimeLocalFrames,
        int TargetUptimeNoneFrames,
        int TrueNorthCheckedFrames,
        int TrueNorthRsrReflectedFrames,
        int TrueNorthLocalFrames,
        int TrueNorthNoneFrames);

    private sealed record AgentRunMetrics(
        float TotalSeconds,
        float CombatSeconds,
        float DowntimeSeconds,
        float ActiveMovementSeconds,
        float ManualSuppressedSeconds,
        float BmrPressureSeconds,
        float LoggedFramesPerSecond,
        float AverageGeneratedCandidates,
        float AverageAcceptedCandidates,
        float AverageRouteQueryBudgetUsed,
        float VnavmeshQueryPendingRatio,
        float DestinationChangesPerMinute,
        int ManualCorrectionCount);

    private sealed record AgentRunPenalty(
        string Name,
        float WeightedPenalty,
        string Meaning);
}
