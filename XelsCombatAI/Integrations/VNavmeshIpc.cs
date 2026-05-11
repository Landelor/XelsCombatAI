using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XelsCombatAI.Integrations;

internal enum VNavmeshPathStatus
{
    Unavailable,
    Pending,
    Reachable,
    Unreachable
}

internal sealed record VNavmeshPathResult(
    VNavmeshPathStatus Status,
    string Reason,
    float? PathDistance,
    Vector3? FirstWaypoint,
    int WaypointCount,
    double CacheAgeMilliseconds);

internal sealed record VNavmeshRouteResult(
    VNavmeshPathStatus Status,
    string Reason,
    IReadOnlyList<Vector3> Waypoints,
    float? PathDistance,
    Vector3? FirstWaypoint,
    int WaypointCount,
    double CacheAgeMilliseconds);

internal sealed record VNavmeshRuntimeDiagnostics(
    bool Ready,
    bool? AutoLoad,
    bool? PathfindInProgress,
    int? PathfindQueued);

internal sealed record VNavmeshPointDiagnostics(
    string Status,
    Vector3? NearestPoint,
    float? NearestPointDistance,
    Vector3? NearestReachablePoint,
    float? NearestReachablePointDistance,
    Vector3? FloorPoint,
    float? FloorPointDistance);

internal sealed class VNavmeshIpc
{
    private static readonly TimeSpan ReachabilityCacheDuration = TimeSpan.FromSeconds(2);
    private const float ReachabilityStartTolerance = 5f;
    private const float ReachabilityDestinationTolerance = 1.5f;
    private const int MaxReachabilityChecks = 64;
    private const float PointProbeHalfExtentXz = 5f;
    private const float PointProbeHalfExtentY = 5f;
    private const float FloorProbeHalfExtentXz = 5f;

    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?> pathfind;
    private readonly ICallGateSubscriber<bool> isAutoLoad;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<int> pathfindNumQueued;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPoint;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestReachablePoint;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly object reachabilityLock = new();
    private readonly List<ReachabilityCheck> reachabilityChecks = [];

    public VNavmeshIpc(IDalamudPluginInterface pluginInterface)
    {
        this.isReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.pathfind = pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?>("vnavmesh.Nav.Pathfind");
        this.isAutoLoad = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsAutoLoad");
        this.pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        this.pathfindNumQueued = pluginInterface.GetIpcSubscriber<int>("vnavmesh.Nav.PathfindNumQueued");
        this.nearestPoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        this.nearestReachablePoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        this.pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
    }

    public bool IsReady()
    {
        try
        {
            return this.isReady.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to)
    {
        try
        {
            return this.pathfind.InvokeFunc(from, to, false);
        }
        catch
        {
            return null;
        }
    }

    public VNavmeshPathResult GetPathResult(Vector3 from, Vector3 to)
    {
        var route = this.GetRouteResult(from, to);
        return new(
            route.Status,
            route.Reason,
            route.PathDistance,
            route.FirstWaypoint,
            route.WaypointCount,
            route.CacheAgeMilliseconds);
    }

    public VNavmeshRouteResult GetRouteResult(Vector3 from, Vector3 to)
    {
        if (!this.IsReady())
        {
            return new(
                VNavmeshPathStatus.Unavailable,
                "vnavmesh unavailable",
                [],
                null,
                null,
                0,
                0d);
        }

        var now = DateTime.UtcNow;
        lock (this.reachabilityLock)
        {
            this.PruneReachabilityChecks(now);
            var check = this.GetOrCreateReachabilityCheck(from, to, now);
            if (check == null)
            {
                return new(
                    VNavmeshPathStatus.Unavailable,
                    "vnavmesh pathfind unavailable",
                    [],
                    null,
                    null,
                    0,
                    0d);
            }

            var cacheAge = (now - check.CreatedAt).TotalMilliseconds;
            if (!check.Task.IsCompleted)
            {
                return new(
                    VNavmeshPathStatus.Pending,
                    "vnavmesh reachability pending",
                    [],
                    null,
                    null,
                    0,
                    cacheAge);
            }

            if (!check.Task.IsCompletedSuccessfully || check.Task.Result.Count == 0)
            {
                return new(
                    VNavmeshPathStatus.Unreachable,
                    "vnavmesh destination unreachable",
                    [],
                    null,
                    null,
                    0,
                    cacheAge);
            }

            var path = check.Task.Result;
            var trimmed = TrimPathFromCurrent(from, to, path);
            return new(
                VNavmeshPathStatus.Reachable,
                "vnavmesh destination reachable",
                trimmed.Waypoints,
                trimmed.PathDistance,
                trimmed.FirstWaypoint,
                trimmed.Waypoints.Count,
                cacheAge);
        }
    }

    public bool TryValidateReachable(Vector3 from, Vector3 to, out string reason, bool failWhilePending = true)
    {
        var result = this.GetPathResult(from, to);
        reason = result.Reason;
        return result.Status switch
        {
            VNavmeshPathStatus.Unavailable => true,
            VNavmeshPathStatus.Pending => !failWhilePending,
            VNavmeshPathStatus.Reachable => true,
            _ => false
        };
    }

    public VNavmeshRuntimeDiagnostics GetRuntimeDiagnostics()
    {
        return new(
            this.IsReady(),
            TryInvokeStruct(() => this.isAutoLoad.InvokeFunc()),
            TryInvokeStruct(() => this.pathfindInProgress.InvokeFunc()),
            TryInvokeStruct(() => this.pathfindNumQueued.InvokeFunc()));
    }

    public VNavmeshPointDiagnostics GetPointDiagnostics(Vector3 point)
    {
        if (!this.IsReady())
        {
            return new("Unavailable", null, null, null, null, null, null);
        }

        var nearest = TryInvokeNullable(() => this.nearestPoint.InvokeFunc(point, PointProbeHalfExtentXz, PointProbeHalfExtentY));
        var nearestReachable = TryInvokeNullable(() => this.nearestReachablePoint.InvokeFunc(point, PointProbeHalfExtentXz, PointProbeHalfExtentY));
        var floor = TryInvokeNullable(() => this.pointOnFloor.InvokeFunc(point, false, FloorProbeHalfExtentXz));
        var status = nearest.HasValue || nearestReachable.HasValue || floor.HasValue
            ? "Ready"
            : "NoMeshPoint";

        return new(
            status,
            nearest,
            nearest.HasValue ? Distance2D(point, nearest.Value) : null,
            nearestReachable,
            nearestReachable.HasValue ? Distance2D(point, nearestReachable.Value) : null,
            floor,
            floor.HasValue ? Distance2D(point, floor.Value) : null);
    }

    private ReachabilityCheck? GetOrCreateReachabilityCheck(Vector3 from, Vector3 to, DateTime now)
    {
        var check = this.reachabilityChecks.FirstOrDefault(entry =>
            Distance2D(entry.From, from) <= ReachabilityStartTolerance &&
            Distance2D(entry.To, to) <= ReachabilityDestinationTolerance);
        if (check != null)
        {
            return check;
        }

        var task = this.Pathfind(from, to);
        if (task == null)
        {
            return null;
        }

        check = new ReachabilityCheck(from, to, now, task);
        this.reachabilityChecks.Add(check);
        if (this.reachabilityChecks.Count > MaxReachabilityChecks)
        {
            this.reachabilityChecks.RemoveAt(0);
        }

        return check;
    }

    private void PruneReachabilityChecks(DateTime now)
    {
        this.reachabilityChecks.RemoveAll(entry => entry.Task.IsCompleted && now - entry.CreatedAt > ReachabilityCacheDuration);
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static T? TryInvokeStruct<T>(Func<T> invoke) where T : struct
    {
        try
        {
            return invoke();
        }
        catch
        {
            return null;
        }
    }

    private static T? TryInvokeNullable<T>(Func<T?> invoke) where T : struct
    {
        try
        {
            return invoke();
        }
        catch
        {
            return null;
        }
    }

    private static TrimmedPath TrimPathFromCurrent(Vector3 from, Vector3 to, IReadOnlyList<Vector3> path)
    {
        var points = new List<Vector3>(path.Count + 1);
        points.AddRange(path);
        if (ShouldReversePath(from, to, points))
        {
            points.Reverse();
        }

        if (points.Count == 0 || Distance2D(points[^1], to) > 0.25f)
        {
            points.Add(to);
        }

        if (points.Count == 0)
        {
            return new([to], Distance2D(from, to), to);
        }

        var nextIndex = FindNextWaypointIndex(from, points);
        while (nextIndex < points.Count - 1 && Distance2D(from, points[nextIndex]) <= 0.5f)
        {
            ++nextIndex;
        }

        var firstWaypoint = points[Math.Clamp(nextIndex, 0, points.Count - 1)];
        var waypoints = new List<Vector3>(points.Count - nextIndex);
        for (var i = nextIndex; i < points.Count; ++i)
        {
            waypoints.Add(points[i]);
        }

        var total = Distance2D(from, firstWaypoint);
        for (var i = nextIndex + 1; i < points.Count; ++i)
        {
            total += Distance2D(points[i - 1], points[i]);
        }

        return new(waypoints, total, firstWaypoint);
    }

    private static bool ShouldReversePath(Vector3 from, Vector3 to, IReadOnlyList<Vector3> points)
    {
        if (points.Count < 2)
        {
            return false;
        }

        var first = points[0];
        var last = points[^1];
        return Distance2D(last, from) < Distance2D(first, from) &&
               Distance2D(first, to) < Distance2D(last, to);
    }

    private static int FindNextWaypointIndex(Vector3 from, IReadOnlyList<Vector3> points)
    {
        if (points.Count <= 1)
        {
            return 0;
        }

        var bestDistanceSq = float.MaxValue;
        var bestNextIndex = 1;
        for (var i = 0; i < points.Count - 1; ++i)
        {
            var distanceSq = DistanceToSegmentSq2D(from, points[i], points[i + 1], out var t);
            if (distanceSq >= bestDistanceSq)
            {
                continue;
            }

            bestDistanceSq = distanceSq;
            bestNextIndex = t > 0.85f && i + 2 < points.Count
                ? i + 2
                : i + 1;
        }

        return Math.Clamp(bestNextIndex, 0, points.Count - 1);
    }

    private static float DistanceToSegmentSq2D(Vector3 point, Vector3 a, Vector3 b, out float t)
    {
        var ax = a.X;
        var az = a.Z;
        var bx = b.X;
        var bz = b.Z;
        var px = point.X;
        var pz = point.Z;
        var abx = bx - ax;
        var abz = bz - az;
        var lenSq = (abx * abx) + (abz * abz);
        if (lenSq <= 0.0001f)
        {
            t = 0f;
            var dx0 = px - ax;
            var dz0 = pz - az;
            return (dx0 * dx0) + (dz0 * dz0);
        }

        t = Math.Clamp(((px - ax) * abx + (pz - az) * abz) / lenSq, 0f, 1f);
        var closestX = ax + (abx * t);
        var closestZ = az + (abz * t);
        var dx = px - closestX;
        var dz = pz - closestZ;
        return (dx * dx) + (dz * dz);
    }

    private sealed record ReachabilityCheck(Vector3 From, Vector3 To, DateTime CreatedAt, Task<List<Vector3>> Task);

    private sealed record TrimmedPath(IReadOnlyList<Vector3> Waypoints, float PathDistance, Vector3 FirstWaypoint);
}
