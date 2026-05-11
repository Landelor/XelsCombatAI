using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using XelsCombatAI.Integrations;

namespace XelsCombatAI.Combat;

internal readonly record struct TargetSnapshot(ulong InstanceId, Vector2 Position, float Radius);

internal sealed class GoalPlan(
    RsrAoeShape shape,
    TargetSnapshot[] targets,
    TargetSnapshot primaryTarget,
    float radius,
    float range,
    float halfWidth,
    int minHits = 2)
{
    private static readonly MethodInfo ScoreFromWPosMethod = typeof(GoalPlan).GetMethod(nameof(ScoreFromWPos), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Vector2[] Directions =
    [
        new(1f, 0f),
        new(0.9238795f, 0.3826834f),
        new(0.7071068f, 0.7071068f),
        new(0.3826834f, 0.9238795f),
        new(0f, 1f),
        new(-0.3826834f, 0.9238795f),
        new(-0.7071068f, 0.7071068f),
        new(-0.9238795f, 0.3826834f),
        new(-1f, 0f),
        new(-0.9238795f, -0.3826834f),
        new(-0.7071068f, -0.7071068f),
        new(-0.3826834f, -0.9238795f),
        new(0f, -1f),
        new(0.3826834f, -0.9238795f),
        new(0.7071068f, -0.7071068f),
        new(0.9238795f, -0.3826834f)
    ];
    private readonly Vector2 centroid = CalculateCentroid(targets, primaryTarget);

    public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField)
    {
        return this.CreateGoalDelegate(wposType, xField, zField, minHits - 1);
    }

    public Delegate CreateGoalDelegate(Type wposType, FieldInfo xField, FieldInfo zField, int scoreBaselineHits)
    {
        var parameter = Expression.Parameter(wposType, "p");
        var call = Expression.Call(
            Expression.Constant(this),
            ScoreFromWPosMethod,
            Expression.Field(parameter, xField),
            Expression.Field(parameter, zField),
            Expression.Constant(scoreBaselineHits));
        var delegateType = typeof(Func<,>).MakeGenericType(wposType, typeof(float));
        return Expression.Lambda(delegateType, call, parameter).Compile();
    }

    public int ScoreHits(Vector2 origin)
    {
        return shape switch
        {
            RsrAoeShape.Circle => this.ScoreCircle(origin),
            RsrAoeShape.Cone => this.ScoreCone(origin),
            RsrAoeShape.StraightLine => this.ScoreLine(origin),
            _ => 0
        };
    }

    public CandidateScore FindBestCandidate(Vector2 playerPosition, Func<Vector2, bool>? candidateAllowed = null)
    {
        var best = this.ScoreCandidate(playerPosition);
        var bestPosition = playerPosition;
        var bestPreference = this.CandidatePreference(playerPosition, playerPosition);
        foreach (var candidate in this.GenerateCandidates(playerPosition))
        {
            if (candidateAllowed != null && !candidateAllowed(candidate))
            {
                continue;
            }

            var score = this.ScoreCandidate(candidate);
            var preference = this.CandidatePreference(candidate, playerPosition);
            if (score.Hits > best.Hits ||
                (score.Hits == best.Hits &&
                 (preference > bestPreference + 0.05f ||
                  MathF.Abs(preference - bestPreference) <= 0.05f &&
                  Vector2.DistanceSquared(candidate, playerPosition) < Vector2.DistanceSquared(bestPosition, playerPosition))))
            {
                best = score;
                bestPosition = candidate;
                bestPreference = preference;
            }
        }

        return best with { Position = bestPosition };
    }

    public AoePackOverlaySnapshot CreateOverlay(RsrAoeActionSnapshot action, Vector2 candidate, int currentHits, int bestHits, float y)
    {
        var targetMarkers = new List<AoePackOverlayTarget>(targets.Length);
        foreach (var target in targets)
        {
            targetMarkers.Add(new(
                new Vector3(target.Position.X, y, target.Position.Y),
                target.Radius,
                this.TargetHit(target, candidate),
                false));
        }

        return new(
            action.AdjustedActionId,
            action.ActionName,
            action.Shape.ToString(),
            new Vector3(candidate.X, y, candidate.Y),
            new Vector3(primaryTarget.Position.X, y, primaryTarget.Position.Y),
            radius,
            halfWidth,
            currentHits,
            bestHits,
            targetMarkers);
    }

    private float ScoreFromWPos(float x, float z, int scoreBaselineHits)
    {
        var pos = new Vector2(x, z);
        var hits = this.ScoreHits(pos);
        if (hits < minHits || hits <= scoreBaselineHits)
        {
            return 0f;
        }

        var maxImprovement = Math.Max(1, targets.Length - scoreBaselineHits);
        return Math.Clamp((hits - scoreBaselineHits) / (float)maxImprovement, 0f, GoalZoneScorePolicy.AoeRepositionPreference);
    }

    private CandidateScore ScoreCandidate(Vector2 position)
    {
        return new(position, this.ScoreHits(position));
    }

    private IEnumerable<Vector2> GenerateCandidates(Vector2 playerPosition)
    {
        yield return playerPosition;
        yield return this.centroid;
        foreach (var target in targets)
        {
            yield return target.Position;
        }

        var distance0 = Math.Min(3f, range);
        var distance1 = Math.Min(6f, range);
        var distance2 = Math.Min(radius, range);
        foreach (var candidate in GenerateCandidatesForAnchor(primaryTarget.Position, distance0, distance1, distance2, range))
        {
            yield return candidate;
        }

        foreach (var candidate in GenerateCandidatesForAnchor(this.centroid, distance0, distance1, distance2, range))
        {
            yield return candidate;
        }
    }

    private static IEnumerable<Vector2> GenerateCandidatesForAnchor(Vector2 anchor, float distance0, float distance1, float distance2, float distance3)
    {
        if (distance0 > 0.1f)
        {
            foreach (var direction in Directions)
            {
                yield return anchor - direction * distance0;
            }
        }

        if (distance1 > 0.1f)
        {
            foreach (var direction in Directions)
            {
                yield return anchor - direction * distance1;
            }
        }

        if (distance2 > 0.1f)
        {
            foreach (var direction in Directions)
            {
                yield return anchor - direction * distance2;
            }
        }

        if (distance3 > 0.1f)
        {
            foreach (var direction in Directions)
            {
                yield return anchor - direction * distance3;
            }
        }
    }

    private static Vector2 CalculateCentroid(TargetSnapshot[] targets, TargetSnapshot primaryTarget)
    {
        var total = Vector2.Zero;
        foreach (var target in targets)
        {
            total += target.Position;
        }

        return targets.Length == 0 ? primaryTarget.Position : total / targets.Length;
    }

    private float CandidatePreference(Vector2 candidate, Vector2 playerPosition)
    {
        if (shape == RsrAoeShape.Circle)
        {
            return 0f;
        }

        var toCentroid = this.centroid - candidate;
        var toPrimary = primaryTarget.Position - candidate;
        var aimIntoPack = 0f;
        if (toCentroid.LengthSquared() > 0.01f && toPrimary.LengthSquared() > 0.01f)
        {
            aimIntoPack = (Vector2.Dot(Vector2.Normalize(toCentroid), Vector2.Normalize(toPrimary)) + 1f) * 0.5f;
        }

        var centerDistance = Vector2.Distance(candidate, this.centroid);
        var outsidePack = Math.Clamp((centerDistance - 1.5f) / 6f, 0f, 1f);
        var nearestSurface = float.MaxValue;
        foreach (var target in targets)
        {
            nearestSurface = MathF.Min(nearestSurface, Vector2.Distance(candidate, target.Position) - target.Radius);
        }

        var hitboxPenalty = nearestSurface switch
        {
            < 0.25f => 1.25f,
            < 1.25f => 0.45f,
            _ => 0f
        };
        var travelPenalty = Math.Clamp(Vector2.Distance(candidate, playerPosition) / 30f, 0f, 1f) * 0.25f;
        return (aimIntoPack * 0.75f) + (outsidePack * 0.55f) - hitboxPenalty - travelPenalty;
    }

    private int ScoreCircle(Vector2 origin)
    {
        var count = 0;
        foreach (var target in targets)
        {
            if (this.TargetHit(target, origin))
            {
                ++count;
            }
        }

        return count;
    }

    private int ScoreCone(Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
        {
            return 0;
        }

        var direction = Vector2.Normalize(toPrimary);
        // halfWidth is the half-angle in radians (π/3 = 60°, matching RSR's hardcoded _alpha).
        var cosHalfAngle = MathF.Cos(halfWidth);
        var count = 0;
        foreach (var target in targets)
        {
            var toTarget = target.Position - origin;
            if (this.TargetHitInCone(target, origin, direction, cosHalfAngle, toTarget))
            {
                ++count;
            }
        }

        return count;
    }

    private int ScoreLine(Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
        {
            return 0;
        }

        var direction = Vector2.Normalize(toPrimary);
        var count = 0;
        foreach (var target in targets)
        {
            if (this.TargetHitInLine(target, origin, direction))
            {
                ++count;
            }
        }

        return count;
    }

    private bool TargetHit(TargetSnapshot target, Vector2 origin)
    {
        return shape switch
        {
            RsrAoeShape.Circle => this.TargetHitInCircle(target, origin),
            RsrAoeShape.Cone => this.TargetHitInCone(target, origin),
            RsrAoeShape.StraightLine => this.TargetHitInLine(target, origin),
            _ => false
        };
    }

    private bool TargetHitInCircle(TargetSnapshot target, Vector2 origin)
    {
        var effective = radius + target.Radius;
        return Vector2.DistanceSquared(target.Position, origin) <= effective * effective;
    }

    private bool TargetHitInCone(TargetSnapshot target, Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        if (primaryDistanceSq <= 0.01f || primaryDistanceSq > effectiveRange * effectiveRange)
        {
            return false;
        }

        var cosHalfAngle = MathF.Cos(halfWidth);
        return this.TargetHitInCone(target, origin, Vector2.Normalize(toPrimary), cosHalfAngle, target.Position - origin);
    }

    private bool TargetHitInCone(TargetSnapshot target, Vector2 origin, Vector2 direction, float cosHalfAngle, Vector2 toTarget)
    {
        var effective = radius + target.Radius;
        if (toTarget.LengthSquared() > effective * effective)
        {
            return false;
        }

        var length = toTarget.Length();
        return length > 0.01f && Vector2.Dot(toTarget / length, direction) >= cosHalfAngle;
    }

    private bool TargetHitInLine(TargetSnapshot target, Vector2 origin)
    {
        var toPrimary = primaryTarget.Position - origin;
        var primaryDistanceSq = toPrimary.LengthSquared();
        var effectiveRange = radius + primaryTarget.Radius;
        return primaryDistanceSq > 0.01f &&
               primaryDistanceSq <= effectiveRange * effectiveRange &&
               this.TargetHitInLine(target, origin, Vector2.Normalize(toPrimary));
    }

    private bool TargetHitInLine(TargetSnapshot target, Vector2 origin, Vector2 direction)
    {
        var delta = target.Position - origin;
        var front = Vector2.Dot(delta, direction);
        var side = Math.Abs(delta.X * direction.Y - delta.Y * direction.X);
        return front >= 0f && front <= radius + target.Radius && side <= halfWidth + target.Radius;
    }

    public readonly record struct CandidateScore(Vector2 Position, int Hits);
}
