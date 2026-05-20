using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XelsCombatAI.Combat;

internal sealed class ManualCorrectionFeedback
{
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(3);
    private const float DestinationAssociationRadius = 10f;
    private const float AdvisoryWeightMultiplier = 0.35f;
    private readonly Dictionary<string, FeedbackEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private string status = "<none>";

    public string Status => this.status;

    public void Reset()
    {
        this.entries.Clear();
        this.status = "<none>";
    }

    public void RecordManualMovement(Vector3 playerPosition, string goalSources, BossModMovementDiagnostics movement, DateTime now)
    {
        this.Prune(now);
        var sources = ParseSources(goalSources);
        if (sources.Length == 0)
        {
            this.status = "manual input without active advisory source";
            return;
        }

        if (!movement.NavigationDestinationPosition.HasValue)
        {
            this.status = "manual input without active destination";
            return;
        }

        var player2 = new Vector2(playerPosition.X, playerPosition.Z);
        var destination = movement.NavigationDestinationPosition.Value;
        var distance = Vector2.Distance(player2, destination);
        if (distance > DestinationAssociationRadius)
        {
            this.status = $"manual input ignored; destination {distance:0.0}y away";
            return;
        }

        foreach (var source in sources)
        {
            this.entries[source] = new FeedbackEntry(now.Add(FeedbackDuration), destination);
        }

        this.status = $"lowering {string.Join(", ", sources)} after manual input";
    }

    public BossModGoalContribution[] Apply(IReadOnlyList<BossModGoalContribution> contributions, Vector3? playerPosition, DateTime now)
    {
        this.Prune(now);
        if (this.entries.Count == 0 || contributions.Count == 0)
        {
            return contributions.ToArray();
        }

        var adjusted = false;
        var result = new BossModGoalContribution[contributions.Count];
        for (var i = 0; i < contributions.Count; i++)
        {
            var contribution = contributions[i];
            if (contribution.ScoreMode != BossModGoalScoreMode.Advisory ||
                !this.TryFindEntry(contribution.Label, out var entry) ||
                !IsStillNearby(playerPosition, entry.Destination))
            {
                result[i] = contribution;
                continue;
            }

            result[i] = contribution with { AdvisoryWeight = contribution.AdvisoryWeight * AdvisoryWeightMultiplier };
            adjusted = true;
        }

        if (adjusted)
        {
            this.status = $"active: {string.Join(", ", this.entries.Keys)}";
        }

        return result;
    }

    private bool TryFindEntry(string label, out FeedbackEntry entry)
    {
        if (this.entries.TryGetValue(label, out entry))
        {
            return true;
        }

        foreach (var pair in this.entries)
        {
            if (label.Contains(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                entry = pair.Value;
                return true;
            }
        }

        entry = default;
        return false;
    }

    private void Prune(DateTime now)
    {
        foreach (var source in this.entries.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            this.entries.Remove(source);
        }

        if (this.entries.Count == 0 && this.status.StartsWith("active:", StringComparison.Ordinal))
        {
            this.status = "<none>";
        }
    }

    private static bool IsStillNearby(Vector3? playerPosition, Vector2 destination)
    {
        if (!playerPosition.HasValue)
        {
            return true;
        }

        var player2 = new Vector2(playerPosition.Value.X, playerPosition.Value.Z);
        return Vector2.Distance(player2, destination) <= DestinationAssociationRadius * 1.5f;
    }

    private static string[] ParseSources(string goalSources)
    {
        if (string.IsNullOrWhiteSpace(goalSources) ||
            string.Equals(goalSources, "<none>", StringComparison.OrdinalIgnoreCase) ||
            goalSources.Contains("casting", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return goalSources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(source =>
                source.Length > 0 &&
                !string.Equals(source, "<none>", StringComparison.OrdinalIgnoreCase) &&
                !source.Contains("mechanic", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private readonly record struct FeedbackEntry(DateTime ExpiresAtUtc, Vector2 Destination);
}
