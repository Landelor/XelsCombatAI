using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FightReview;

internal static class ArtifactWriter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Write(ReviewBundle review, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteNormalized(review, Path.Combine(outputDirectory, "fight.normalized.jsonl"));
        WriteIncidents(review, Path.Combine(outputDirectory, "incidents"));
        WriteReport(review, Path.Combine(outputDirectory, "fight.report.md"));
        WriteAgentImprovement(review, Path.Combine(outputDirectory, "agent.improvement.json"));
        WriteHtml(review, Path.Combine(outputDirectory, "fight.html"));
    }

    private static void WriteNormalized(ReviewBundle review, string path)
    {
        var uptime = UptimeScoring.Analyze(review.Xcai);
        using var writer = new StreamWriter(path, false, Utf8NoBom);
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            Type = "header",
            GeneratedUtc = DateTime.UtcNow,
            Xcai = review.Xcai.Header,
            Bmr = review.Bmr with { Events = Array.Empty<BmrEvent>() },
            Match = review.Match,
            Uptime = uptime
        }, JsonOptions));

        foreach (var frame in review.Xcai.Frames)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                Type = "xcai-frame",
                frame.TimestampUtc,
                frame.T,
                frame.PlayerPosition,
                frame.TargetBaseId,
                frame.TargetObjectId,
                frame.TargetPosition,
                frame.AutomatedMovementSuppressed,
                frame.ManualMovementInput,
                frame.Facing,
                frame.BossModActiveModule,
                frame.BossModActiveZoneModule,
                frame.AoeReason,
                frame.TrashPull,
                frame.Planner,
                frame.BossMod,
                frame.Mobility,
                frame.Motion,
                frame.PackTargetCount,
                frame.CurrentHits,
                frame.BestHits,
                frame.ActionName,
                frame.ActionShape,
                frame.Actors
            }, JsonOptions));
        }

        foreach (var bmrEvent in review.Bmr.Events)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                Type = "bmr-event",
                Event = bmrEvent
            }, JsonOptions));
        }
    }

    private static void WriteIncidents(ReviewBundle review, string incidentsDirectory)
    {
        Directory.CreateDirectory(incidentsDirectory);
        foreach (var oldIncident in Directory.EnumerateFiles(incidentsDirectory, "*.json"))
        {
            File.Delete(oldIncident);
        }

        foreach (var incident in review.Incidents)
        {
            var slice = review.Xcai.Frames
                .Skip(incident.StartFrame)
                .Take(Math.Max(0, incident.EndFrame - incident.StartFrame + 1))
                .Select(frame => new
                {
                    frame.TimestampUtc,
                    frame.T,
                    frame.PlayerPosition,
                    frame.TargetPosition,
                    frame.TargetBaseId,
                    frame.AutomatedMovementSuppressed,
                    frame.ManualMovementInput,
                    frame.Facing,
                    frame.BossModActiveModule,
                    frame.BossModActiveZoneModule,
                    frame.AoeReason,
                    frame.TrashPull,
                    frame.Planner,
                    frame.BossMod,
                    frame.Mobility,
                    frame.Motion,
                    frame.PackTargetCount,
                    frame.CurrentHits,
                    frame.BestHits,
                    frame.ActionName,
                    frame.ActionShape
                })
                .ToArray();
            var path = Path.Combine(incidentsDirectory, $"{Sanitize(incident.Id)}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Incident = incident, Frames = slice }, PrettyJsonOptions) + Environment.NewLine, Utf8NoBom);
        }
    }

    private static void WriteReport(ReviewBundle review, string path)
    {
        var sb = new StringBuilder();
        var uptime = UptimeScoring.Analyze(review.Xcai);
        sb.AppendLine("# Fight Review");
        sb.AppendLine();
        sb.AppendLine("Review premise: uptime is the primary positive signal: staying in usable target range gives RSR freedom to act, melee/tank ranged fallback is partial uptime, melee range is better, trash pack positions should hit more targets, and healer uptime is scored together with visible party heal coverage. BMR remains the safety authority. Normal profile BMR pressure is safety context; Greed profiles are expected to preserve uptime until BMR actually requires movement. Fight review still flags danger, downtime, unhuman-like movement, manual corrections, vnavmesh issues, and poor recovery.");
        sb.AppendLine();
        sb.AppendLine($"- XCAI log: `{review.Xcai.Path}`");
        sb.AppendLine($"- BMR replay: `{review.Bmr.Path}`");
        sb.AppendLine($"- Log scope: `{review.Xcai.Header.LogScope}`");
        sb.AppendLine($"- Run start UTC: `{review.Xcai.Header.RunStartUtc:O}`");
        sb.AppendLine($"- Combat start UTC: `{review.Xcai.Header.CombatStartUtc:O}`");
        sb.AppendLine($"- Duration: `{review.Xcai.Header.DurationSeconds:0.0}s`");
        sb.AppendLine($"- Job: `{review.Xcai.Header.PlayerClassJobId}`");
        sb.AppendLine($"- Territory/CFCID: `{review.Xcai.Header.TerritoryType}/{review.Xcai.Header.ContentFinderConditionId}`");
        sb.AppendLine($"- BMR module: `{review.Xcai.Header.BossModActiveModule}`");
        sb.AppendLine($"- Combat style: `{review.Xcai.Header.CombatStyle}`");
        sb.AppendLine($"- Match confidence: `{review.Match.Confidence:0.00}`");
        foreach (var evidence in review.Match.Evidence)
        {
            sb.AppendLine($"  - {evidence}");
        }

        var runScore = BuildRunScore(review, uptime);
        sb.AppendLine();
        sb.AppendLine("## Run Score");
        sb.AppendLine();
        sb.AppendLine($"- Overall: `{runScore.Overall:0.0}/100`");
        sb.AppendLine($"- Uptime: `{runScore.Uptime:0.0}/100`");
        sb.AppendLine($"- Safety: `{runScore.Safety:0.0}/100`");
        sb.AppendLine($"- Efficiency: `{runScore.Efficiency:0.0}/100`");
        sb.AppendLine($"- Human-likeness: `{runScore.HumanLikeness:0.0}/100`");
        sb.AppendLine($"- Resource discipline: `{runScore.ResourceDiscipline:0.0}/100`");
        sb.AppendLine($"- Job role: `{uptime.Job.Role}`");
        sb.AppendLine($"- Target weighted uptime: `{uptime.Metrics.TargetWeightedUptimePercent:0.0}%`");
        sb.AppendLine($"- Preferred range ratio: `{uptime.Metrics.PreferredUptimeRatio:P0}`");
        sb.AppendLine($"- Fallback range seconds: `{uptime.Metrics.FallbackUptimeSeconds:0.0}`");
        sb.AppendLine($"- Avoidable out-of-range seconds: `{uptime.Metrics.AvoidableOutOfRangeSeconds:0.0}`");
        sb.AppendLine($"- Trash hit efficiency: `{uptime.Metrics.PackHitEfficiencyPercent:0.0}%`");
        if (uptime.Job.IsHealer)
        {
            sb.AppendLine($"- Healer party coverage: `{uptime.Metrics.HealerPartyCoveragePercent:0.0}%`");
        }

        sb.AppendLine($"- Combat seconds: `{runScore.Metrics.CombatSeconds:0.0}`");
        sb.AppendLine($"- Active movement seconds: `{runScore.Metrics.ActiveMovementSeconds:0.0}`");
        sb.AppendLine($"- Average generated candidates/frame: `{runScore.Metrics.AverageGeneratedCandidates:0.0}`");
        sb.AppendLine($"- Average route-query budget/frame: `{runScore.Metrics.AverageRouteQueryBudgetUsed:0.00}`");
        sb.AppendLine($"- Manual correction count: `{runScore.Metrics.ManualCorrectionCount}`");

        sb.AppendLine();
        sb.AppendLine("## Uptime Signals");
        foreach (var signal in uptime.PositiveSignals.OrderByDescending(signal => signal.Weight).Take(8))
        {
            sb.AppendLine($"- Positive `{signal.Category}` weight `{signal.Weight:0.0}`: {signal.Evidence}");
        }

        foreach (var signal in uptime.NegativeSignals.OrderByDescending(signal => signal.Weight).Take(8))
        {
            sb.AppendLine($"- Negative `{signal.Category}` weight `{signal.Weight:0.0}`: {signal.Evidence} Goal: {signal.SuggestedGoal}");
        }

        sb.AppendLine();
        sb.AppendLine("## Incidents");
        if (review.Incidents.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("No incidents detected by the current heuristics.");
        }
        else
        {
            foreach (var incident in review.Incidents)
            {
                sb.AppendLine();
                sb.AppendLine($"### T+{incident.T:0.00}s `{incident.Category}` ({incident.Severity})");
                sb.AppendLine();
                sb.AppendLine($"Evidence: {incident.Evidence}");
                sb.AppendLine();
                sb.AppendLine($"Suggested goal: {incident.SuggestedGoal}");
                sb.AppendLine();
                sb.AppendLine($"Slice: `incidents/{Sanitize(incident.Id)}.json`");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Improvement Goals");
        foreach (var goal in review.Incidents.Select(i => i.SuggestedGoal).Distinct(StringComparer.Ordinal).OrderBy(goal => goal, StringComparer.Ordinal))
        {
            sb.AppendLine($"- {goal}");
        }

        sb.AppendLine();
        sb.AppendLine("## Config Option Candidates");
        sb.AppendLine();
        sb.AppendLine("If an incident pattern cannot be corrected cleanly with existing settings, treat it as a candidate for a new user-facing option. Communicate the candidate and ask before implementation; do not silently add config surface as part of a behavior fix.");

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    private static void WriteAgentImprovement(ReviewBundle review, string path)
    {
        var uptime = UptimeScoring.Analyze(review.Xcai);
        var runScore = BuildRunScore(review, uptime);
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
                    Slice = $"incidents/{Sanitize(incident.Id)}.json"
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
                BmrReplay = review.Bmr.Path,
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
                MatchConfidence = review.Match.Confidence,
                MatchEvidence = review.Match.Evidence
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
                    "Melee/tank ranged fallback is useful partial uptime; melee range is better.",
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
                Slice = $"incidents/{Sanitize(incident.Id)}.json"
            }).ToArray(),
            ImprovementCandidates = improvementCandidates,
            RouteSegments = BuildAgentRouteSegments(review),
            UptimeSegments = uptime.Segments,
            CodeAreas = review.Incidents.SelectMany(incident => CodeAreasForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            TestFocus = review.Incidents.SelectMany(incident => TestFocusForIncident(incident.Category)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Artifacts = new
            {
                Normalized = "fight.normalized.jsonl",
                Report = "fight.report.md",
                Html = "fight.html",
                Incidents = "incidents/*.json"
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(packet, PrettyJsonOptions) + Environment.NewLine, Utf8NoBom);
    }

    private static void WriteHtml(ReviewBundle review, string path)
    {
        var durationSeconds = Math.Max(
            review.Xcai.Header.DurationSeconds,
            review.Xcai.Frames.LastOrDefault()?.T ?? 0f);
        var frames = review.Xcai.Frames.Select(frame => new
        {
            frame.T,
            frame.TimestampUtc,
            frame.PlayerPosition,
            TargetObjectId = frame.TargetObjectId.ToString(CultureInfo.InvariantCulture),
            frame.TargetPosition,
            frame.TargetRadius,
            frame.AutomatedMovementSuppressed,
            frame.ManualMovementInput,
            frame.Facing,
            frame.BossModActiveModule,
            frame.BossModActiveZoneModule,
            frame.AoeReason,
            frame.TrashPull,
            frame.Planner,
            frame.BossMod,
            frame.Mobility,
            frame.Motion,
            frame.PackTargetCount,
            frame.CurrentHits,
            frame.BestHits,
            frame.ActionName,
            frame.ActionShape,
            Actors = frame.Actors.Select(actor => new
            {
                Id = actor.GameObjectId.ToString(CultureInfo.InvariantCulture),
                actor.Relation,
                actor.EntityId,
                actor.BaseId,
                actor.ObjectKind,
                actor.SubKind,
                actor.ClassJobId,
                actor.Level,
                actor.Position,
                actor.Rotation,
                actor.Radius,
                actor.IsTargetable,
                actor.IsDead,
                actor.InCombat,
                actor.CurrentHp,
                actor.MaxHp,
                TargetObjectId = actor.TargetObjectId.ToString(CultureInfo.InvariantCulture),
                actor.DistanceToPlayer
            })
        }).ToArray();
        var mechanics = review.Bmr.Events
            .Where(IsHtmlMechanicEvent)
            .Select(evt => new
            {
                evt.Type,
                T = RelativeBmrSeconds(evt.Timestamp, review.Xcai.Header.CombatStartUtc),
                SourceId = evt.SourceId.ToString(CultureInfo.InvariantCulture),
                evt.SourceOid,
                TargetId = evt.TargetId?.ToString(CultureInfo.InvariantCulture),
                evt.TargetOid,
                ActionRaw = evt.ActionRaw.ToString(CultureInfo.InvariantCulture),
                evt.Label
            })
            .Where(evt => evt.T >= -5d && evt.T <= durationSeconds + 5d)
            .Take(1200)
            .ToArray();
        var encounters = review.Bmr.Encounters
            .Select(encounter => new
            {
                encounter.Index,
                InstanceId = encounter.InstanceId.ToString(CultureInfo.InvariantCulture),
                encounter.Oid,
                encounter.Zone,
                StartT = RelativeBmrSeconds(encounter.Start, review.Xcai.Header.CombatStartUtc),
                EndT = RelativeBmrSeconds(encounter.End, review.Xcai.Header.CombatStartUtc),
                States = encounter.States.Select(state => new
                {
                    state.Id,
                    Name = string.IsNullOrWhiteSpace(state.Name)
                        ? state.Id.ToString(CultureInfo.InvariantCulture)
                        : state.Name,
                    state.Comment,
                    ExitT = RelativeBmrSeconds(state.Exit, review.Xcai.Header.CombatStartUtc)
                }).ToArray(),
                Phases = encounter.Phases.Select(phase => new
                {
                    phase.Id,
                    phase.LastStateId,
                    ExitT = RelativeBmrSeconds(phase.Exit, review.Xcai.Header.CombatStartUtc)
                }).ToArray()
            })
            .Where(encounter => encounter.EndT >= -5d && encounter.StartT <= durationSeconds + 5d)
            .ToArray();
        var framesJson = JsonSerializer.Serialize(frames, JsonOptions);
        var incidentsJson = JsonSerializer.Serialize(review.Incidents, JsonOptions);
        var mechanicsJson = JsonSerializer.Serialize(mechanics, JsonOptions);
        var encountersJson = JsonSerializer.Serialize(encounters, JsonOptions);
        var combatStyleJson = JsonSerializer.Serialize(review.Xcai.Header.CombatStyle, JsonOptions);
        var durationJson = JsonSerializer.Serialize(durationSeconds, JsonOptions);

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>XCAI Fight Review</title>
<style>
body{margin:0;font-family:system-ui,Segoe UI,sans-serif;background:#111;color:#eee}
main{display:grid;grid-template-columns:minmax(0,1fr) 360px;height:100vh}
#map{width:100%;height:100%;display:block;background:#151515}
#timeline{width:100%;height:112px;display:block;background:#151515;border:1px solid #333;border-radius:6px}
aside{padding:16px;border-left:1px solid #333;overflow:auto}
h1{font-size:22px;margin:0 0 12px}
h2{font-size:16px;margin:18px 0 8px}
input[type=range]{width:100%}
.row{margin:10px 0}
.layers{display:grid;grid-template-columns:1fr 1fr;gap:5px 10px;font-size:12px;color:#ddd}
.layers label{display:flex;align-items:center;gap:6px}
.layers input{width:auto}
.legend{display:grid;grid-template-columns:1fr 1fr;gap:6px;font-size:12px;color:#ccc}
.swatch{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:6px;vertical-align:-1px}
.detail{font-size:13px;line-height:1.45}
.nearby-event{border-left:3px solid #777;padding-left:7px;margin:5px 0;color:#ddd}
.incident{border:1px solid #444;padding:8px;margin:8px 0;border-radius:6px}
.high{border-color:#e05252}.medium{border-color:#e0aa52}.low{border-color:#6fa8dc}
code{color:#a6e3a1}
</style>
</head>
<body>
<main>
<canvas id="map"></canvas>
<aside>
<h1>Fight Review</h1>
<div class="row">Combat style: <code id="combatStyle"></code></div>
<div class="row"><input id="scrub" type="range" min="0" max="0" value="0"></div>
<canvas id="timeline"></canvas>
<div class="row layers">
<label><input id="layerSafety" type="checkbox" checked>Safety</label>
<label><input id="layerGoals" type="checkbox" checked>Goals</label>
<label><input id="layerParty" type="checkbox" checked>Party</label>
<label><input id="layerEnemies" type="checkbox" checked>Enemies</label>
<label><input id="layerPlanner" type="checkbox" checked>Planner</label>
<label><input id="layerIncidents" type="checkbox" checked>Incidents</label>
</div>
<div class="row legend">
<div><span class="swatch" style="background:#4aa3ff"></span>Player</div>
<div><span class="swatch" style="background:#76e083"></span>Party</div>
<div><span class="swatch" style="background:#ff6961"></span>Enemies</div>
<div><span class="swatch" style="background:#ffdd66"></span>Target</div>
<div><span class="swatch" style="background:#7dd3fc"></span>Route step</div>
<div><span class="swatch" style="background:#b83232"></span>Active no-go</div>
<div><span class="swatch" style="background:#f59f00"></span>Future no-go</div>
<div><span class="swatch" style="background:#f59f00"></span>BMR avoid/move</div>
<div><span class="swatch" style="background:#c084fc"></span>Mechanics</div>
</div>
<div id="details"></div>
<h2>Incidents</h2>
<div id="incidents"></div>
</aside>
</main>
<script>
const frames = {{framesJson}};
const incidents = {{incidentsJson}};
const mechanics = {{mechanicsJson}};
const encounters = {{encountersJson}};
const combatStyle = {{combatStyleJson}};
const duration = Math.max(1, {{durationJson}});
document.getElementById('combatStyle').textContent = combatStyle;
const canvas = document.getElementById('map');
const ctx = canvas.getContext('2d');
const timeline = document.getElementById('timeline');
const tctx = timeline.getContext('2d');
const scrub = document.getElementById('scrub');
const layerSafety = document.getElementById('layerSafety');
const layerGoals = document.getElementById('layerGoals');
const layerParty = document.getElementById('layerParty');
const layerEnemies = document.getElementById('layerEnemies');
const layerPlanner = document.getElementById('layerPlanner');
const layerIncidents = document.getElementById('layerIncidents');
scrub.max = Math.max(0, frames.length - 1);
const labelById = new Map();
let partyLabel = 1;
for (const f of frames) {
  for (const actor of f.Actors || []) {
    const key = actor.Id || `${actor.BaseId}:${actor.EntityId}`;
    if (labelById.has(key)) continue;
    if (actor.Relation === 'player') labelById.set(key, 'Player');
    else if (actor.Relation === 'party') labelById.set(key, `Party-${partyLabel++}`);
    else if (actor.Relation === 'target') labelById.set(key, `Target-${actor.BaseId || actor.EntityId || 'unknown'}`);
    else if (actor.Relation === 'targeting-player') labelById.set(key, `Aggro-${actor.BaseId || actor.EntityId || 'unknown'}`);
    else labelById.set(key, `Enemy-${actor.BaseId || actor.EntityId || 'unknown'}`);
  }
}
function sizeCanvas(target){ target.width = Math.max(1, Math.floor(target.clientWidth * devicePixelRatio)); target.height = Math.max(1, Math.floor(target.clientHeight * devicePixelRatio)); }
function resize(){ sizeCanvas(canvas); sizeCanvas(timeline); draw(); }
addEventListener('resize', resize);
scrub.addEventListener('input', draw);
for (const layer of [layerSafety, layerGoals, layerParty, layerEnemies, layerPlanner, layerIncidents]) layer.addEventListener('change', draw);
function pos2(p){ return p && {x:p.X, y:p.Z}; }
function bounds(){
  const pts = [];
  for (const f of frames){
    if(f.PlayerPosition) pts.push(pos2(f.PlayerPosition));
    if(f.TargetPosition) pts.push(pos2(f.TargetPosition));
    if(f.Planner?.Destination) pts.push(pos2(f.Planner.Destination));
    if(f.Planner?.RouteMemory?.RouteGoal) pts.push(pos2(f.Planner.RouteMemory.RouteGoal));
    if(f.Planner?.RouteMemory?.LocalDestination) pts.push(pos2(f.Planner.RouteMemory.LocalDestination));
    for (const p of f.Planner?.RouteMemory?.TankTrail || []) pts.push(pos2(p));
    for (const actor of f.Actors || []) if(actor.Position) pts.push(pos2(actor.Position));
    const r = safetyRaster(f);
    if(r){
      pts.push(pos2(sourceGridToWorld(r,0,0)));
      pts.push(pos2(sourceGridToWorld(r,r.SourceWidth,0)));
      pts.push(pos2(sourceGridToWorld(r,r.SourceWidth,r.SourceHeight)));
      pts.push(pos2(sourceGridToWorld(r,0,r.SourceHeight)));
    }
  }
  if(!pts.length) return {minX:-10,maxX:10,minY:-10,maxY:10};
  let minX=Math.min(...pts.map(p=>p.x)), maxX=Math.max(...pts.map(p=>p.x)), minY=Math.min(...pts.map(p=>p.y)), maxY=Math.max(...pts.map(p=>p.y));
  const pad = Math.max(5, (maxX-minX+maxY-minY)/12); return {minX:minX-pad,maxX:maxX+pad,minY:minY-pad,maxY:maxY+pad};
}
const b = bounds();
function map(p){ const w=canvas.width,h=canvas.height; return {x:(p.x-b.minX)/(b.maxX-b.minX)*w, y:h-(p.y-b.minY)/(b.maxY-b.minY)*h}; }
function circle(p,r,color,width=2){ const m=map(pos2(p)); const scale=canvas.width/(b.maxX-b.minX); ctx.strokeStyle=color; ctx.lineWidth=width*devicePixelRatio; ctx.beginPath(); ctx.arc(m.x,m.y,Math.max(2,r*scale),0,Math.PI*2); ctx.stroke(); }
function dot(p,color,r=4){ const m=map(pos2(p)); ctx.fillStyle=color; ctx.beginPath(); ctx.arc(m.x,m.y,r*devicePixelRatio,0,Math.PI*2); ctx.fill(); }
function line(a,b,color,width=2){ const aa=map(pos2(a)), bb=map(pos2(b)); ctx.strokeStyle=color; ctx.lineWidth=width*devicePixelRatio; ctx.beginPath(); ctx.moveTo(aa.x,aa.y); ctx.lineTo(bb.x,bb.y); ctx.stroke(); }
function labelAt(p,label,color='#ddd'){ const m=map(pos2(p)); ctx.fillStyle=color; ctx.font=`${12*devicePixelRatio}px system-ui`; ctx.fillText(label,m.x+7*devicePixelRatio,m.y-7*devicePixelRatio); }
function polygon(points,color){ if(points.length < 3) return; ctx.fillStyle=color; ctx.beginPath(); const first=map(pos2(points[0])); ctx.moveTo(first.x, first.y); for(let i=1;i<points.length;i++){ const p=map(pos2(points[i])); ctx.lineTo(p.x,p.y); } ctx.closePath(); ctx.fill(); }
function actorLabel(actor){ return labelById.get(actor.Id || `${actor.BaseId}:${actor.EntityId}`) || actor.Relation || 'Actor'; }
function actorAt(index,id){ return (frames[index]?.Actors || []).find(actor => actor.Id === id); }
function previousActor(index,id){
  for(let i=index-1;i>=Math.max(0,index-16);i--){ const actor = actorAt(i,id); if(actor) return actor; }
  return null;
}
function arrow(from,to,color){
  const a=map(pos2(from)), z=map(pos2(to));
  const dx=z.x-a.x, dy=z.y-a.y, len=Math.hypot(dx,dy);
  if(len < 6*devicePixelRatio) return;
  const ux=dx/len, uy=dy/len, head=8*devicePixelRatio;
  ctx.strokeStyle=color; ctx.fillStyle=color; ctx.lineWidth=2*devicePixelRatio;
  ctx.beginPath(); ctx.moveTo(a.x,a.y); ctx.lineTo(z.x,z.y); ctx.stroke();
  ctx.beginPath();
  ctx.moveTo(z.x,z.y);
  ctx.lineTo(z.x-ux*head-uy*head*.55,z.y-uy*head+ux*head*.55);
  ctx.lineTo(z.x-ux*head+uy*head*.55,z.y-uy*head-ux*head*.55);
  ctx.closePath(); ctx.fill();
}
function safetyRaster(frame){ const r = frame?.BossMod?.SafetyRaster; return r?.Status === 'captured' && r.Width > 0 && r.Height > 0 ? r : null; }
function decodeRle(rle,count){
  const out = [];
  if(rle){
    for(const run of rle.split(',')){
      if(!run) continue;
      const parts = run.split(':');
      const state = Number(parts[0]), n = Number(parts[1]);
      for(let i=0;i<n && out.length<count;i++) out.push(state);
    }
  }
  while(out.length<count) out.push(0);
  return out;
}
function rasterCells(r){ if(!r._cells) r._cells = decodeRle(r.CellsRle || '', r.Width * r.Height); return r._cells; }
function sourceGridToWorld(r,gx,gy){
  const dx = (gx - r.SourceWidth / 2) * r.SourceResolution;
  const dz = (gy - r.SourceHeight / 2) * r.SourceResolution;
  const sin = Math.sin(r.RotationRadians || 0), cos = Math.cos(r.RotationRadians || 0);
  return {X:(r.Center?.X || 0) + dx * cos + dz * sin, Y:0, Z:(r.Center?.Z || 0) - dx * sin + dz * cos};
}
function cellColor(code){
  if(code === 1) return 'rgba(20,24,31,.78)';
  if(code === 2) return 'rgba(184,50,50,.48)';
  if(code === 3) return 'rgba(245,159,0,.34)';
  if(code === 4) return 'rgba(245,191,66,.20)';
  if(code === 5) return 'rgba(96,180,106,.24)';
  return null;
}
function stateLabel(code){
  return code === 1 ? 'blocked' : code === 2 ? 'active-danger' : code === 3 ? 'future-danger' : code === 4 ? 'avoid-buffer' : code === 5 ? 'goal' : 'safe';
}
function isNoGo(code){ return code === 1 || code === 2 || code === 3; }
function drawSafetyRaster(frame){
  if(!layerSafety.checked) return;
  const r = safetyRaster(frame);
  if(!r) return;
  const cells = rasterCells(r);
  for(let y=0;y<r.Height;y++){
    for(let x=0;x<r.Width;x++){
      const code = cells[y*r.Width+x] || 0;
      if(code === 0 || (code === 5 && !layerGoals.checked)) continue;
      const color = cellColor(code);
      if(!color) continue;
      const x0=x*r.CellScale, y0=y*r.CellScale, x1=Math.min(r.SourceWidth,(x+1)*r.CellScale), y1=Math.min(r.SourceHeight,(y+1)*r.CellScale);
      polygon([sourceGridToWorld(r,x0,y0), sourceGridToWorld(r,x1,y0), sourceGridToWorld(r,x1,y1), sourceGridToWorld(r,x0,y1)], color);
    }
  }
  ctx.strokeStyle = 'rgba(255,255,255,.32)';
  ctx.lineWidth = 1.2 * devicePixelRatio;
  for(let y=0;y<r.Height;y++){
    for(let x=0;x<r.Width;x++){
      const code = cells[y*r.Width+x] || 0;
      if(!isNoGo(code)) continue;
      const x0=x*r.CellScale, y0=y*r.CellScale, x1=Math.min(r.SourceWidth,(x+1)*r.CellScale), y1=Math.min(r.SourceHeight,(y+1)*r.CellScale);
      const neighbors = [
        [x, y-1, sourceGridToWorld(r,x0,y0), sourceGridToWorld(r,x1,y0)],
        [x+1, y, sourceGridToWorld(r,x1,y0), sourceGridToWorld(r,x1,y1)],
        [x, y+1, sourceGridToWorld(r,x1,y1), sourceGridToWorld(r,x0,y1)],
        [x-1, y, sourceGridToWorld(r,x0,y1), sourceGridToWorld(r,x0,y0)]
      ];
      for(const [nx,ny,a,bp] of neighbors){
        const ncode = nx < 0 || nx >= r.Width || ny < 0 || ny >= r.Height ? 0 : cells[ny*r.Width+nx] || 0;
        if(isNoGo(ncode)) continue;
        const aa=map(pos2(a)), bb=map(pos2(bp));
        ctx.beginPath(); ctx.moveTo(aa.x,aa.y); ctx.lineTo(bb.x,bb.y); ctx.stroke();
      }
    }
  }
}
function drawActorTrail(id,color,idx,width=1){
  let prev = null;
  for(let i=0;i<=idx;i++){
    const actor = actorAt(i,id);
    if(actor?.Position && prev?.Position) line(prev.Position, actor.Position, color, width);
    if(actor?.Position) prev = actor;
  }
}
function mechanicColor(type){
  if(type === 'cast') return '#ff6b6b';
  if(type === 'icon') return '#ffd43b';
  if(type === 'tether') return '#66d9e8';
  return '#c084fc';
}
function hasSpecialModePressure(frame){
  const mode = frame?.BossMod?.ImminentSpecialMode || '<none>';
  return mode !== '<none>' && !mode.startsWith('(Normal,');
}
function hasBmrPressure(frame){
  return !!(frame?.Planner?.BmrForcedMovement ||
    (frame?.Planner?.BmrForbiddenZones ?? 0) > 0 ||
    frame?.Planner?.BmrMoveRequested ||
    frame?.Planner?.BmrMoveImminent ||
    (frame?.BossMod?.ForbiddenZones ?? 0) > 0 ||
    hasSpecialModePressure(frame));
}
function tToX(t){ return Math.max(0, Math.min(timeline.width, (t / duration) * timeline.width)); }
function drawTimeline(frame){
  tctx.clearRect(0,0,timeline.width,timeline.height);
  const rows = {enc:14*devicePixelRatio, safety:34*devicePixelRatio, mech:62*devicePixelRatio, inc:90*devicePixelRatio};
  tctx.font = `${11*devicePixelRatio}px system-ui`;
  tctx.fillStyle = '#aaa';
  tctx.fillText('BMR', 6*devicePixelRatio, rows.enc);
  tctx.fillText('Avoid/Move', 6*devicePixelRatio, rows.safety);
  tctx.fillText('Mechanics', 6*devicePixelRatio, rows.mech);
  tctx.fillText('Incidents', 6*devicePixelRatio, rows.inc);
  tctx.strokeStyle = '#333';
  for (const y of Object.values(rows)){ tctx.beginPath(); tctx.moveTo(72*devicePixelRatio,y); tctx.lineTo(timeline.width,y); tctx.stroke(); }
  for (const encounter of encounters){
    const x1=tToX(encounter.StartT), x2=tToX(encounter.EndT);
    tctx.fillStyle = '#5c6bc055';
    tctx.fillRect(x1, rows.enc-10*devicePixelRatio, Math.max(2*devicePixelRatio,x2-x1), 9*devicePixelRatio);
  }
  let safetyStart = null;
  for (let i=0;i<frames.length;i++){
    const active = hasBmrPressure(frames[i]);
    if(active && safetyStart === null) safetyStart = frames[i].T;
    if((!active || i === frames.length-1) && safetyStart !== null){
      const end = active ? frames[i].T : frames[Math.max(0,i-1)].T;
      tctx.fillStyle = '#f59f0088';
      tctx.fillRect(tToX(safetyStart), rows.safety-10*devicePixelRatio, Math.max(2*devicePixelRatio,tToX(end)-tToX(safetyStart)), 9*devicePixelRatio);
      safetyStart = null;
    }
  }
  for (const evt of mechanics){
    const x=tToX(evt.T);
    tctx.strokeStyle = mechanicColor(evt.Type);
    tctx.beginPath(); tctx.moveTo(x, rows.mech-12*devicePixelRatio); tctx.lineTo(x, rows.mech+6*devicePixelRatio); tctx.stroke();
  }
  for (const incident of incidents){
    const x=tToX(incident.T);
    tctx.fillStyle = incident.Severity === 'high' ? '#e05252' : incident.Severity === 'medium' ? '#e0aa52' : '#6fa8dc';
    tctx.beginPath();
    tctx.moveTo(x, rows.inc-12*devicePixelRatio);
    tctx.lineTo(x-5*devicePixelRatio, rows.inc+2*devicePixelRatio);
    tctx.lineTo(x+5*devicePixelRatio, rows.inc+2*devicePixelRatio);
    tctx.closePath(); tctx.fill();
  }
  if(frame){
    const x=tToX(frame.T);
    tctx.strokeStyle = '#fff';
    tctx.lineWidth = 2*devicePixelRatio;
    tctx.beginPath(); tctx.moveTo(x, 0); tctx.lineTo(x, timeline.height); tctx.stroke();
  }
}
function esc(value){ return String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function nearMechanics(t){
  return mechanics
    .filter(evt => Math.abs(evt.T - t) <= 5)
    .sort((a,b) => Math.abs(a.T - t) - Math.abs(b.T - t))
    .slice(0, 8);
}
function activeEncounter(t){
  return encounters.find(encounter => t >= encounter.StartT && t <= encounter.EndT);
}
function activeState(encounter,t){
  if(!encounter) return null;
  return (encounter.States || []).find(state => t <= state.ExitT) || null;
}
function isGreedStyle(){ return /^Greed/.test(combatStyle || ''); }
function closestFrameIndex(t){
  let best = 0, bestDt = Infinity;
  for(let i=0;i<frames.length;i++){ const dt=Math.abs((frames[i]?.T ?? 0)-t); if(dt<bestDt){ best=i; bestDt=dt; } }
  return best;
}
function drawIncidentMarkers(){
  if(!layerIncidents.checked) return;
  for(const incident of incidents){
    const f = frames[closestFrameIndex(incident.T)];
    if(!f?.PlayerPosition) continue;
    const p = map(pos2(f.PlayerPosition));
    ctx.fillStyle = incident.Severity === 'high' ? '#e05252' : incident.Severity === 'medium' ? '#e0aa52' : '#6fa8dc';
    ctx.beginPath();
    ctx.moveTo(p.x, p.y - 9*devicePixelRatio);
    ctx.lineTo(p.x - 6*devicePixelRatio, p.y + 6*devicePixelRatio);
    ctx.lineTo(p.x + 6*devicePixelRatio, p.y + 6*devicePixelRatio);
    ctx.closePath(); ctx.fill();
  }
}
function drawTrashPull(frame){
  if(!layerPlanner.checked || !frame?.TrashPull) return;
  const tp = frame.TrashPull;
  if(tp.PackCentroid){
    circle(tp.PackCentroid, 1.2, '#3ddc97', 2);
    labelAt(tp.PackCentroid, `Pack ${tp.DominantTargetCount || 0}`, '#9ff2ca');
  }
  if(tp.TankPosition){
    dot(tp.TankPosition, '#76e083', 6);
    labelAt(tp.TankPosition, 'Tank', '#b7f5c0');
  }
  if(tp.TankPosition && tp.ProjectedTankPosition){
    line(tp.TankPosition, tp.ProjectedTankPosition, '#76e083aa', 2);
    dot(tp.ProjectedTankPosition, '#76e083', 3);
  }
  if(tp.LeadDestination){
    if(frame.PlayerPosition) line(frame.PlayerPosition, tp.LeadDestination, '#3ddc97', 2);
    dot(tp.LeadDestination, tp.LeadClampApplied ? '#ffd166' : '#3ddc97', 5);
    labelAt(tp.LeadDestination, tp.LeadClampApplied ? 'Lead clamped' : 'Tank lead', tp.LeadClampApplied ? '#ffe4a3' : '#9ff2ca');
  }
}
function drawRouteMemory(frame){
  if(!layerPlanner.checked || !frame?.Planner?.RouteMemory) return;
  const rm = frame.Planner.RouteMemory;
  const trail = rm.TankTrail || [];
  for(let i=1;i<trail.length;i++) line(trail[i-1], trail[i], '#76e08399', 2);
  if(rm.RouteGoal){
    dot(rm.RouteGoal, '#7dd3fc', 4);
    labelAt(rm.RouteGoal, rm.Source === 'tank-trail' ? 'Route goal' : 'Fallback goal', '#bae6fd');
  }
  if(rm.LocalDestination){
    if(frame.PlayerPosition) line(frame.PlayerPosition, rm.LocalDestination, '#7dd3fc', 2);
    dot(rm.LocalDestination, rm.Source === 'vnav-fallback' ? '#facc15' : '#7dd3fc', 5);
    labelAt(rm.LocalDestination, rm.Source === 'vnav-fallback' ? 'Route fallback' : 'Local step', rm.Source === 'vnav-fallback' ? '#fde68a' : '#bae6fd');
  }
  if(rm.NextWaypoint){
    dot(rm.NextWaypoint, '#93c5fd', 4);
    if(rm.LocalDestination) line(rm.LocalDestination, rm.NextWaypoint, '#93c5fd99', 1);
  }
}
function medianPartySpeed(index){
  if(index <= 0) return null;
  const prev = frames[index-1], cur = frames[index];
  const dt = (cur?.T ?? 0) - (prev?.T ?? 0);
  if(dt <= 0) return null;
  const prevParty = new Map((prev.Actors || []).filter(a => a.Relation === 'party').map(a => [a.Id, a]));
  const speeds = (cur.Actors || []).filter(a => a.Relation === 'party' && prevParty.has(a.Id)).map(a => {
    const p = prevParty.get(a.Id);
    const dx = a.Position.X - p.Position.X, dz = a.Position.Z - p.Position.Z;
    return Math.hypot(dx,dz) / dt;
  }).filter(v => v > .2 && v < 12).sort((a,b) => a-b);
  return speeds.length ? speeds[Math.floor(speeds.length/2)] : null;
}
function draw(){
  ctx.clearRect(0,0,canvas.width,canvas.height);
  const idx = Number(scrub.value), frame = frames[idx];
  drawSafetyRaster(frame);
  for(let i=1;i<frames.length;i++){ if(frames[i-1].PlayerPosition && frames[i].PlayerPosition) line(frames[i-1].PlayerPosition, frames[i].PlayerPosition, '#4aa3ff28', 1); }
  for(let i=1;i<=idx;i++){ if(frames[i-1].PlayerPosition && frames[i].PlayerPosition) line(frames[i-1].PlayerPosition, frames[i].PlayerPosition, '#4aa3ff88', 2); }
  if(layerParty.checked) for (const [id,label] of labelById.entries()) if(label.startsWith('Party-')) drawActorTrail(id, '#76e08366', idx, 1);
  if(frame?.TargetPosition) circle(frame.TargetPosition, frame.TargetRadius || 1, '#ffdd66');
  for(const actor of frame?.Actors || []){
    if(layerParty.checked && actor.Relation === 'party'){
      dot(actor.Position, '#76e083', 4);
      labelAt(actor.Position, actorLabel(actor), '#b7f5c0');
      const prev = previousActor(idx, actor.Id);
      if(prev) arrow(prev.Position, actor.Position, '#76e083aa');
    }
    if(layerEnemies.checked && (actor.Relation === 'nearby' || actor.Relation === 'target' || actor.Relation === 'targeting-player')){
      circle(actor.Position, actor.Radius || 0.5, actor.IsDead ? '#777' : '#ff6961', 1);
      if(actor.Relation === 'target' || actor.Relation === 'targeting-player') labelAt(actor.Position, actorLabel(actor), '#ffb3ad');
    }
  }
  if(layerPlanner.checked && frame?.Planner?.Destination){ line(frame.PlayerPosition, frame.Planner.Destination, '#ffffff'); dot(frame.Planner.Destination, '#fff', 5); }
  if(layerPlanner.checked && frame?.Planner?.FirstWaypoint){ line(frame.PlayerPosition, frame.Planner.FirstWaypoint, '#c0c0ff', 1); dot(frame.Planner.FirstWaypoint, '#c0c0ff', 4); }
  if(layerPlanner.checked && frame?.Planner?.LineOfSight?.BlockedPoint){
    dot(frame.Planner.LineOfSight.BlockedPoint, '#ff4f5e', 5);
    labelAt(frame.Planner.LineOfSight.BlockedPoint, 'LOS blocked', '#ff9aa4');
  }
  if(layerPlanner.checked && frame?.Mobility?.Destination && frame?.Mobility?.State !== 'NotChecked'){
    line(frame.PlayerPosition, frame.Mobility.Destination, '#00d4aa', 2);
    dot(frame.Mobility.Destination, '#00d4aa', 5);
    labelAt(frame.Mobility.Destination, `Dash ${frame.Mobility.IntentLabel || ''}`, '#9ff2e0');
  }
  drawTrashPull(frame);
  drawRouteMemory(frame);
  if(frame?.PlayerPosition){
    dot(frame.PlayerPosition, '#4aa3ff', 6);
    labelAt(frame.PlayerPosition, 'Player', '#9fd0ff');
    if(idx > 0 && frames[idx-1]?.PlayerPosition) arrow(frames[idx-1].PlayerPosition, frame.PlayerPosition, '#4aa3ffaa');
  }
  drawIncidentMarkers();
  drawTimeline(frame);
  const encounter = activeEncounter(frame?.T ?? 0);
  const state = activeState(encounter, frame?.T ?? 0);
  const mechanicRows = frame ? nearMechanics(frame.T).map(evt => `<div class="nearby-event" style="border-color:${mechanicColor(evt.Type)}">T${evt.T >= frame.T ? '+' : ''}${(evt.T-frame.T).toFixed(1)}s ${esc(evt.Type)} <code>${esc(evt.Label)}</code> source OID <code>${evt.SourceOid}</code></div>`).join('') : '';
  const greedNote = isGreedStyle() && hasBmrPressure(frame) ? ' | Greed may intentionally delay movement for uptime' : '';
  const raster = safetyRaster(frame);
  const partySpeed = medianPartySpeed(idx);
  const safetyRow = raster ? `<div>Safety: player <code>${esc(raster.Player?.State)}</code> | dest <code>${esc(raster.Destination?.State)}</code> | waypoint <code>${esc(raster.FirstWaypoint?.State)}</code></div>` : `<div>Safety: <code>${esc(frame?.BossMod?.SafetyRaster?.Reason || 'not logged')}</code></div>`;
  const los = frame?.Planner?.LineOfSight;
  const losRow = los ? `<div>LOS: <code>${los.Checked ? (los.Blocked ? 'blocked' : 'clear') : 'not checked'}</code> | combat: ${los.CombatClear ? 'clear' : 'blocked'} | nav: ${los.NavigationClear ? 'clear' : 'blocked'}${los.BlockedDistance != null ? ` | block: ${los.BlockedDistance.toFixed(1)}y` : ''}</div>` : '';
  const mobility = frame?.Mobility || {};
  const mobilityRow = `<div>Dash: <code>${esc(mobility.State || 'NotChecked')}</code> ${esc(mobility.IntentLabel || 'none')} | ${esc(mobility.ActionName || '<none>')} | safety +${(mobility.SafetyGain ?? 0).toFixed(1)}y | uptime +${(mobility.UptimeGain ?? 0).toFixed(1)} | ${esc(mobility.RiskReason || 'not logged')}</div>`;
  const facing = frame?.Facing || {};
  const facingDelta = facing.DeltaRadians == null ? 'n/a' : `${(Math.abs(facing.DeltaRadians) * 180 / Math.PI).toFixed(0)}deg`;
  const facingRow = `<div>Facing: <code>${esc(facing.Source || 'None')}</code> ${esc(facing.Reason || 'not logged')} | ${facing.Applied ? 'applied' : esc(facing.RejectionReason || 'not applied')} | delta ${facingDelta} | allies ${facing.ConsensusMembers ?? 0}</div>`;
  const tp = frame?.TrashPull || {};
  const trashRow = `<div>Trash pull: <code>${esc(tp.Phase || 'None')}</code> confidence ${(tp.Confidence ?? 0).toFixed(2)} | behind ${tp.BehindDistance?.toFixed?.(1) ?? 'n/a'}y | tank ${tp.TankSpeed?.toFixed?.(1) ?? '0.0'}y/s | pack ${tp.PackSpeed?.toFixed?.(1) ?? '0.0'}y/s</div><div>Tank lead: <code>${tp.LeadCandidateActive ? 'active' : esc(tp.LeadRejectionReason || 'inactive')}</code>${tp.LeadClampApplied ? ' | clamped behind tank' : ''}</div>`;
  const rm = frame?.Planner?.RouteMemory || {};
  const routeRow = `<div>Route memory: <code>${rm.Active ? esc(rm.Source || 'active') : esc(rm.State || 'inactive')}</code> | ${esc(rm.Reason || 'not logged')} | offset ${rm.OffsetSide ?? 0}:${(rm.OffsetDistance ?? 0).toFixed(1)}y | vnav <code>${esc(rm.VnavStatus || 'None')}</code> | budget ${rm.QueryBudgetUsed ?? 0}/${rm.QueryBudgetLimit ?? 0}</div>`;
  document.getElementById('details').innerHTML = frame ? `<div class="detail"><h2>T+${frame.T.toFixed(2)}s</h2><div>Planner: <code>${esc(frame.Planner.ChosenSource)}</code> ${esc(frame.Planner.SwitchReason)}</div><div>Suppress: <code>${esc(frame.Planner.SuppressionReason)}</code></div><div>Manual: <code>${frame.AutomatedMovementSuppressed}</code> ${esc(frame.ManualMovementInput)}</div><div>Range: ${frame.Motion.TargetSurfaceDistance?.toFixed?.(1) ?? 'n/a'} | speed: ${frame.Motion.PlayerSpeed?.toFixed?.(1) ?? 'n/a'} | party: ${partySpeed?.toFixed?.(1) ?? 'n/a'}</div>${trashRow}${routeRow}${safetyRow}${losRow}${mobilityRow}${facingRow}<div>AoE hits: ${frame.CurrentHits}/${frame.BestHits} | pack: ${frame.PackTargetCount}</div><div>Action: <code>${esc(frame.ActionName || 'n/a')}</code> ${esc(frame.ActionShape || '')}</div><div>BMR: <code>${esc(frame.BossModActiveModule || frame.BossModActiveZoneModule || encounter?.Oid || 'none')}</code>${state ? ` | state: <code>${esc(state.Name)}</code>` : ''}</div><div>BMR avoid/move pressure: <code>${hasBmrPressure(frame) ? 'yes' : 'no'}</code> | forbidden: ${frame.BossMod?.ForbiddenZones ?? 0}/${frame.Planner?.BmrForbiddenZones ?? 0} | move: ${frame.Planner?.BmrMoveRequested || frame.Planner?.BmrMoveImminent ? 'yes' : 'no'}${greedNote}</div><div>Planner steer: <code>${esc(frame.BossMod?.PlannerSteer || 'not logged')}</code></div><div>Mechanic whisper: <code>${esc(frame.BossMod?.MechanicWhisper || 'not logged')}</code></div><h2>Nearby Mechanics</h2>${mechanicRows || '<div class="nearby-event">No aligned BMR cast/icon/tether within 5s.</div>'}</div>` : '';
}
document.getElementById('incidents').innerHTML = incidents.map(i => `<div class="incident ${i.Severity}"><b>T+${i.T.toFixed(2)} ${i.Category}</b><br>${i.Evidence}<br><code>${i.SuggestedGoal}</code></div>`).join('');
resize();
</script>
</body>
</html>
""";

        File.WriteAllText(path, html, Utf8NoBom);
    }

    private static bool IsHtmlMechanicEvent(BmrEvent evt)
    {
        return evt.Type is "cast" or "icon" or "tether";
    }

    private static double RelativeBmrSeconds(DateTime timestamp, DateTime combatStartUtc)
    {
        var eventTime = timestamp.Kind == DateTimeKind.Utc
            ? timestamp.ToLocalTime()
            : timestamp;
        var startTime = combatStartUtc.Kind == DateTimeKind.Utc
            ? combatStartUtc.ToLocalTime()
            : combatStartUtc;
        return (eventTime - startTime).TotalSeconds;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static AgentRunScore BuildRunScore(ReviewBundle review, UptimeAnalysis uptime)
    {
        var frames = review.Xcai.Frames;
        var durations = EstimateFrameDurations(frames);
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

    private static float[] EstimateFrameDurations(IReadOnlyList<XcaiFrame> frames)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var durations = new float[frames.Count];
        for (var i = 0; i < frames.Count - 1; i++)
        {
            durations[i] = Math.Clamp(frames[i + 1].T - frames[i].T, 0f, 2f);
        }

        durations[^1] = frames.Count > 1 ? durations[^2] : 0f;
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
        var endT = frames[end].T;
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
