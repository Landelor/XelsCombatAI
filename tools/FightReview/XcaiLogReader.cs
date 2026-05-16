using System.Text.Json;

namespace FightReview;

internal static class XcaiLogReader
{
    public static XcaiLog Read(string path)
    {
        XcaiHeader? header = null;
        var frames = new List<XcaiFrame>();
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = rawLine.TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = ReadString(root, "Type", string.Empty);
            if (type == "header")
            {
                header = ParseHeader(root);
            }
            else if (type == "frame")
            {
                if (header == null)
                {
                    throw new InvalidDataException($"Frame before header at line {lineNumber}.");
                }

                frames.Add(ParseFrame(root));
            }
        }

        if (header == null)
        {
            throw new InvalidDataException("XCAI log does not contain a header.");
        }

        if (header.FrameCount != frames.Count)
        {
            Console.Error.WriteLine($"Warning: header frame count is {header.FrameCount}, parsed {frames.Count}.");
        }

        header = NormalizeHeader(header, frames);
        return new XcaiLog(Path.GetFullPath(path), header, frames);
    }

    private static XcaiHeader NormalizeHeader(XcaiHeader header, IReadOnlyList<XcaiFrame> frames)
    {
        var playerClassJobId = header.PlayerClassJobId != 0
            ? header.PlayerClassJobId
            : FirstNonZero(frames, frame => frame.PlayerClassJobId);
        var territoryType = header.TerritoryType != 0
            ? header.TerritoryType
            : FirstNonZero(frames, frame => frame.TerritoryType);
        var contentFinderConditionId = header.ContentFinderConditionId != 0
            ? header.ContentFinderConditionId
            : FirstNonZero(frames, frame => frame.ContentFinderConditionId);
        var bossModActiveModule = IsNone(header.BossModActiveModule)
            ? LastNonNone(frames, frame => frame.BossModActiveModule)
            : header.BossModActiveModule;
        var bossModActiveZoneModule = IsNone(header.BossModActiveZoneModule)
            ? LastNonNone(frames, frame => frame.BossModActiveZoneModule)
            : header.BossModActiveZoneModule;

        return header with
        {
            PlayerClassJobId = playerClassJobId,
            TerritoryType = territoryType,
            ContentFinderConditionId = contentFinderConditionId,
            BossModActiveModule = bossModActiveModule,
            BossModActiveZoneModule = bossModActiveZoneModule
        };
    }

    private static uint FirstNonZero(IEnumerable<XcaiFrame> frames, Func<XcaiFrame, uint> selector)
    {
        foreach (var frame in frames)
        {
            var value = selector(frame);
            if (value != 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static string LastNonNone(IReadOnlyList<XcaiFrame> frames, Func<XcaiFrame, string> selector)
    {
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var value = selector(frames[i]);
            if (!IsNone(value))
            {
                return value;
            }
        }

        return "<none>";
    }

    private static bool IsNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("<none>", StringComparison.Ordinal);
    }

    private static XcaiHeader ParseHeader(JsonElement root)
    {
        var schemaVersion = ReadInt(root, "SchemaVersion");
        if (schemaVersion != 3)
        {
            throw new InvalidDataException($"Unsupported XCAI schema version {schemaVersion}; this analyzer only supports schema v3.");
        }

        return new XcaiHeader(
            ReadString(root, "LogScope", "combat"),
            ReadString(root, "PluginVersion", "unknown"),
            ReadDateTime(root, "RunStartUtc", ReadDateTime(root, "CombatStartUtc")),
            ReadDateTime(root, "RunEndUtc", ReadDateTime(root, "CombatEndUtc")),
            ReadDateTime(root, "CombatStartUtc"),
            ReadDateTime(root, "CombatEndUtc"),
            ReadFloat(root, "DurationSeconds"),
            ReadInt(root, "FrameCount"),
            ReadUInt(root, "PlayerClassJobId"),
            ReadUInt(root, "TerritoryType"),
            ReadUInt(root, "ContentFinderConditionId"),
            ReadString(root, "BossModActiveModule", "<none>"),
            ReadString(root, "BossModActiveZoneModule", "<none>"),
            ReadConfigString(root, "CombatStyle", "Standard"),
            root.Clone());
    }

    private static XcaiFrame ParseFrame(JsonElement root)
    {
        var frame = Required(root, "Frame");
        var motion = root.TryGetProperty("Motion", out var motionElement) ? motionElement : default;
        var planner = frame.TryGetProperty("MovementPlanner", out var plannerElement) ? ParsePlanner(plannerElement) : EmptyPlanner();
        var bossMod = frame.TryGetProperty("BossModMovement", out var bossModElement) ? ParseBossMod(bossModElement) : EmptyBossMod();

        return new XcaiFrame(
            ReadDateTime(frame, "TimestampUtc"),
            ReadFloat(frame, "T"),
            ReadBool(frame, "InCombat"),
            ReadBool(frame, "IsDead"),
            ReadUInt(frame, "PlayerClassJobId"),
            ReadUInt(frame, "TerritoryType"),
            ReadUInt(frame, "ContentFinderConditionId"),
            ReadVec3(frame, "PlayerPosition"),
            ReadFloat(frame, "PlayerRotation"),
            ReadUInt(frame, "TargetBaseId"),
            ReadULong(frame, "TargetObjectId"),
            ReadVec3(frame, "TargetPosition"),
            ReadFloat(frame, "TargetRotation"),
            ReadFloat(frame, "TargetRadius"),
            ReadFloat(frame, "TargetUptimeRange"),
            ReadBool(frame, "AutomatedMovementSuppressed"),
            ReadString(frame, "ManualMovementInput", "unknown"),
            ParseFacing(frame),
            ReadString(frame, "BossModActiveModule", "<none>"),
            ReadString(frame, "BossModActiveZoneModule", "<none>"),
            ReadString(frame, "Reason", "<none>"),
            ParseTrashPull(frame),
            planner,
            bossMod,
            ParseMobility(frame),
            ParseMotion(motion),
            ReadInt(frame, "Targets"),
            ReadInt(frame, "CurrentHits"),
            ReadInt(frame, "BestHits"),
            ReadString(frame, "ActionName", "<none>"),
            ReadString(frame, "Shape", "<none>"),
            ParseActors(frame),
            frame.Clone());
    }

    private static FacingSnapshot ParseFacing(JsonElement frame)
    {
        if (!frame.TryGetProperty("Facing", out var facing) || facing.ValueKind != JsonValueKind.Object)
        {
            return FacingSnapshot.Empty;
        }

        return new FacingSnapshot(
            ReadString(facing, "Source", "None"),
            ReadString(facing, "Reason", "not logged"),
            ReadNullableFloat(facing, "DesiredRotation"),
            ReadNullableFloat(facing, "CurrentRotation"),
            ReadNullableFloat(facing, "DeltaRadians"),
            ReadBool(facing, "Applied"),
            ReadString(facing, "RejectionReason", string.Empty),
            ReadInt(facing, "ConsensusMembers"));
    }

    private static TrashPullSnapshot ParseTrashPull(JsonElement frame)
    {
        if (!frame.TryGetProperty("TrashPull", out var trashPull) || trashPull.ValueKind != JsonValueKind.Object)
        {
            return TrashPullSnapshot.Empty;
        }

        return new TrashPullSnapshot(
            ReadString(trashPull, "Phase", "None"),
            ReadFloat(trashPull, "Confidence"),
            ReadString(trashPull, "Reason", string.Empty),
            ReadULong(trashPull, "TankObjectId"),
            ReadVec3(trashPull, "TankPosition"),
            ReadVec3(trashPull, "TankVelocity"),
            ReadFloat(trashPull, "TankSpeed"),
            ReadVec3(trashPull, "ProjectedTankPosition"),
            ReadVec3(trashPull, "LeadDestination"),
            ReadBool(trashPull, "LeadCandidateActive"),
            ReadBool(trashPull, "LeadClampApplied"),
            ReadNullableFloat(trashPull, "BehindDistance"),
            ReadVec3(trashPull, "PackCentroid"),
            ReadVec3(trashPull, "PackVelocity"),
            ReadFloat(trashPull, "PackSpeed"),
            ReadNullableFloat(trashPull, "PartyMedianSpeed"),
            ReadInt(trashPull, "DominantTargetCount"),
            ReadInt(trashPull, "StragglerTargetCount"),
            ReadULongArray(trashPull, "DominantTargetIds"),
            ReadULongArray(trashPull, "StragglerTargetIds"),
            ReadString(trashPull, "LeadRejectionReason", "<none>"));
    }

    private static PlannerSnapshot ParsePlanner(JsonElement element)
    {
        return new PlannerSnapshot(
            ReadString(element, "IntentId", "<none>"),
            ReadString(element, "ChosenSource", "<none>"),
            ReadVec3(element, "Destination"),
            ReadNullableFloat(element, "AcceptanceRadius"),
            ReadString(element, "SwitchReason", "none"),
            ReadString(element, "SuppressionReason", "not evaluated"),
            ReadInt(element, "GeneratedCount"),
            ReadInt(element, "AcceptedCount"),
            ReadStringIntDictionary(element, "RejectedByReason"),
            ParseCandidates(element),
            ReadString(element, "ScoreBreakdown", "<none>"),
            ReadString(element, "PathStatus", "None"),
            ReadNullableFloat(element, "PathDistance"),
            ReadNullableFloat(element, "DirectDistance"),
            ReadNullableFloat(element, "ExtraPathDistance"),
            ReadNullableFloat(element, "PathDetourRatio"),
            ReadNullableInt(element, "PathWaypointCount"),
            ReadNullableDouble(element, "PathCacheAgeMilliseconds"),
            ReadVec3(element, "FirstWaypoint"),
            ReadNullableFloat(element, "FirstWaypointDistance"),
            ReadNullableFloat(element, "FirstWaypointYawDelta"),
            ParseVnavmeshRuntime(element),
            ReadString(element, "VnavmeshProbeSource", "<none>"),
            ParseVnavmeshPoint(element, "VnavmeshDestination"),
            ParseLineOfSight(element),
            ReadVec3(element, "BmrForcedMovement"),
            ReadInt(element, "BmrForbiddenZones"),
            ReadBool(element, "BmrMoveRequested"),
            ReadBool(element, "BmrMoveImminent"),
            ParseRouteMemory(element));
    }

    private static RouteMemorySnapshot ParseRouteMemory(JsonElement element)
    {
        if (!element.TryGetProperty("RouteMemory", out var routeMemory) ||
            routeMemory.ValueKind != JsonValueKind.Object)
        {
            return RouteMemorySnapshot.Empty;
        }

        return new RouteMemorySnapshot(
            ReadBool(routeMemory, "Active"),
            ReadString(routeMemory, "State", "inactive"),
            ReadString(routeMemory, "Source", "<none>"),
            ReadString(routeMemory, "Reason", string.Empty),
            ReadVec3(routeMemory, "RouteGoal"),
            ReadVec3(routeMemory, "LocalDestination"),
            ReadVec3(routeMemory, "NextWaypoint"),
            ReadInt(routeMemory, "OffsetSide"),
            ReadFloat(routeMemory, "OffsetDistance"),
            ReadNullableDouble(routeMemory, "RouteAgeMilliseconds") ?? 0d,
            ReadInt(routeMemory, "WaypointIndex"),
            ReadInt(routeMemory, "WaypointCount"),
            ReadString(routeMemory, "VnavStatus", "None"),
            ReadInt(routeMemory, "QueryBudgetUsed"),
            ReadInt(routeMemory, "QueryBudgetLimit"),
            ReadString(routeMemory, "InvalidationReason", "<none>"),
            ReadVec3Array(routeMemory, "TankTrail"));
    }

    private static LineOfSightSnapshot ParseLineOfSight(JsonElement element)
    {
        if (!element.TryGetProperty("LineOfSight", out var lineOfSight) ||
            lineOfSight.ValueKind != JsonValueKind.Object)
        {
            return LineOfSightSnapshot.NotLogged;
        }

        return new LineOfSightSnapshot(
            ReadBool(lineOfSight, "Checked"),
            ReadBool(lineOfSight, "Blocked"),
            ReadBool(lineOfSight, "CombatClear"),
            ReadBool(lineOfSight, "NavigationClear"),
            ReadString(lineOfSight, "Reason", string.Empty),
            ReadVec3(lineOfSight, "BlockedPoint"),
            ReadNullableFloat(lineOfSight, "BlockedDistance"));
    }

    private static BossModSnapshot ParseBossMod(JsonElement element)
    {
        var hints = element.TryGetProperty("HintDetails", out var hintDetails) ? hintDetails : default;
        var bounds = hints.ValueKind == JsonValueKind.Object && hints.TryGetProperty("PathfindMapBounds", out var boundsElement) ? boundsElement : default;

        return new BossModSnapshot(
            ReadString(element, "MovementOverride", "<none>"),
            ReadString(element, "HintSummary", "<none>"),
            ReadString(element, "PlannerSteer", "not logged"),
            ReadString(element, "MechanicWhisper", "not logged"),
            ReadVec3OrXyAsXz(hints, "PathfindMapCenter"),
            ReadNullableFloat(bounds, "Radius"),
            ReadNullableFloat(bounds, "HalfWidth"),
            ReadNullableFloat(bounds, "HalfHeight"),
            ReadNullableInt(hints, "GoalZones"),
            ReadNullableInt(hints, "ForbiddenZones"),
            ReadString(hints, "ImminentSpecialMode", "<none>"),
            ParseSafetyRaster(element));
    }

    private static MobilitySnapshot ParseMobility(JsonElement frame)
    {
        if (!frame.TryGetProperty("MobilityDecision", out var mobility) || mobility.ValueKind != JsonValueKind.Object)
        {
            return MobilitySnapshot.Empty;
        }

        return new MobilitySnapshot(
            ReadString(mobility, "State", "NotChecked"),
            ReadString(mobility, "Intent", "None"),
            ReadString(mobility, "IntentLabel", "none"),
            ReadString(mobility, "ActionName", "<none>"),
            ReadUInt(mobility, "ActionId"),
            ReadVec3(mobility, "Destination"),
            ReadFloat(mobility, "MoveDistance"),
            ReadFloat(mobility, "SafetyGain"),
            ReadFloat(mobility, "UptimeGain"),
            ReadFloat(mobility, "PathGain"),
            ReadString(mobility, "SafetyReason", "not logged"),
            ReadString(mobility, "UptimeReason", "not logged"),
            ReadString(mobility, "PathReason", "not logged"),
            ReadString(mobility, "RiskReason", "not logged"));
    }

    private static SafetyRasterSnapshot ParseSafetyRaster(JsonElement element)
    {
        if (!element.TryGetProperty("SafetyRaster", out var raster) || raster.ValueKind != JsonValueKind.Object)
        {
            return SafetyRasterSnapshot.Unavailable("not logged");
        }

        return new SafetyRasterSnapshot(
            ReadString(raster, "Status", "unavailable"),
            ReadString(raster, "Reason", string.Empty),
            ReadVec3OrXyAsXz(raster, "Center"),
            ReadFloat(raster, "RotationRadians"),
            ReadFloat(raster, "SourceResolution"),
            ReadInt(raster, "SourceWidth"),
            ReadInt(raster, "SourceHeight"),
            Math.Max(1, ReadInt(raster, "CellScale")),
            ReadInt(raster, "Width"),
            ReadInt(raster, "Height"),
            ReadNullableFloat(raster, "MaxG"),
            ReadNullableFloat(raster, "MaxPriority"),
            ReadString(raster, "Encoding", "rle-v1"),
            ReadString(raster, "CellsRle", string.Empty),
            ParseSafetyPoint(raster, "Player"),
            ParseSafetyPoint(raster, "Destination"),
            ParseSafetyPoint(raster, "FirstWaypoint"),
            ParseSafetyPoint(raster, "Target"));
    }

    private static SafetyPointSnapshot ParseSafetyPoint(JsonElement raster, string name)
    {
        if (!raster.TryGetProperty(name, out var point) || point.ValueKind != JsonValueKind.Object)
        {
            return SafetyPointSnapshot.Empty;
        }

        return new SafetyPointSnapshot(
            ReadString(point, "State", "unknown"),
            ReadVec3(point, "Position"),
            ReadNullableInt(point, "GridX"),
            ReadNullableInt(point, "GridY"),
            ReadNullableFloat(point, "PixelMaxG"),
            ReadNullableFloat(point, "PixelPriority"));
    }

    private static MotionSnapshot ParseMotion(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            ? new MotionSnapshot(
                ReadNullableFloat(element, "PlayerStepDistance"),
                ReadNullableFloat(element, "PlayerSpeed"),
                ReadNullableFloat(element, "TargetDistance"),
                ReadNullableFloat(element, "TargetSurfaceDistance"))
            : new MotionSnapshot(null, null, null, null);
    }

    private static IReadOnlyList<CandidateSnapshot> ParseCandidates(JsonElement element)
    {
        if (!element.TryGetProperty("TopCandidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return candidates.EnumerateArray()
            .Select(candidate => new CandidateSnapshot(
                ReadString(candidate, "Source", "<none>"),
                ReadString(candidate, "Reason", string.Empty),
                ReadVec3(candidate, "Destination"),
                ReadBool(candidate, "Accepted"),
                ReadString(candidate, "RejectionReason", string.Empty),
                ReadFloat(candidate, "TotalScore"),
                ReadString(candidate, "PathStatus", "None"),
                ReadNullableFloat(candidate, "PathDistance"),
                ReadNullableFloat(candidate, "DirectDistance"),
                ReadNullableFloat(candidate, "ExtraPathDistance"),
                ReadNullableFloat(candidate, "PathDetourRatio"),
                ReadNullableInt(candidate, "PathWaypointCount"),
                ReadNullableDouble(candidate, "PathCacheAgeMilliseconds"),
                ReadVec3(candidate, "FirstWaypoint"),
                ReadNullableFloat(candidate, "FirstWaypointDistance"),
                ReadNullableFloat(candidate, "FirstWaypointYawDelta"),
                ReadString(candidate, "ScoreBreakdown", string.Empty)))
            .ToArray();
    }

    private static IReadOnlyList<ulong> ReadULongArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out var number))
                {
                    return number;
                }

                return ulong.TryParse(item.GetString(), out var parsed) ? parsed : 0UL;
            })
            .Where(item => item != 0)
            .ToArray();
    }

    private static IReadOnlyList<Vec3> ReadVec3Array(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(Vec3.FromJson)
            .Where(item => item != null)
            .Select(item => item!)
            .ToArray();
    }

    private static VnavmeshRuntimeSnapshot ParseVnavmeshRuntime(JsonElement element)
    {
        if (!element.TryGetProperty("Vnavmesh", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new VnavmeshRuntimeSnapshot(false, null, null, null);
        }

        return new VnavmeshRuntimeSnapshot(
            ReadBool(value, "Ready"),
            ReadNullableBool(value, "AutoLoad"),
            ReadNullableBool(value, "PathfindInProgress"),
            ReadNullableInt(value, "PathfindQueued"));
    }

    private static VnavmeshPointSnapshot? ParseVnavmeshPoint(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new VnavmeshPointSnapshot(
            ReadString(value, "Status", "<none>"),
            ReadVec3(value, "NearestPoint"),
            ReadNullableFloat(value, "NearestPointDistance"),
            ReadVec3(value, "NearestReachablePoint"),
            ReadNullableFloat(value, "NearestReachablePointDistance"),
            ReadVec3(value, "FloorPoint"),
            ReadNullableFloat(value, "FloorPointDistance"));
    }

    private static IReadOnlyList<ActorSnapshot> ParseActors(JsonElement frame)
    {
        if (!frame.TryGetProperty("Actors", out var actors) || actors.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return actors.EnumerateArray()
            .Select(actor => new ActorSnapshot(
                ReadString(actor, "Relation", "nearby"),
                ReadULong(actor, "GameObjectId"),
                ReadUInt(actor, "EntityId"),
                ReadUInt(actor, "BaseId"),
                ReadString(actor, "ObjectKind", "<none>"),
                (byte)ReadInt(actor, "SubKind"),
                ReadUInt(actor, "ClassJobId"),
                (byte)ReadInt(actor, "Level"),
                ReadVec3(actor, "Position") ?? new Vec3(0, 0, 0),
                ReadFloat(actor, "Rotation"),
                ReadFloat(actor, "Radius"),
                ReadBool(actor, "IsTargetable"),
                ReadBool(actor, "IsDead"),
                ReadBool(actor, "InCombat"),
                ReadUInt(actor, "CurrentHp"),
                ReadUInt(actor, "MaxHp"),
                ReadULong(actor, "TargetObjectId"),
                ReadFloat(actor, "DistanceToPlayer")))
            .ToArray();
    }

    private static PlannerSnapshot EmptyPlanner()
    {
        return new PlannerSnapshot("<none>", "<none>", null, null, "none", "not evaluated", 0, 0, new Dictionary<string, int>(), [], "<none>", "None", null, null, null, null, null, null, null, null, null, new VnavmeshRuntimeSnapshot(false, null, null, null), "<none>", null, LineOfSightSnapshot.NotLogged, null, 0, false, false, RouteMemorySnapshot.Empty);
    }

    private static BossModSnapshot EmptyBossMod()
    {
        return new BossModSnapshot("<none>", "<none>", "not logged", "not logged", null, null, null, null, null, null, "<none>", SafetyRasterSnapshot.Unavailable("not logged"));
    }

    private static JsonElement Required(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Missing required property '{name}'.");
    }

    private static string ReadString(JsonElement element, string name, string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static string ReadConfigString(JsonElement element, string name, string fallback)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty("Config", out var config)
            ? ReadString(config, name, fallback)
            : fallback;
    }

    private static DateTime ReadDateTime(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDateTime(out var result)
            ? result
            : throw new InvalidDataException($"Missing or invalid DateTime property '{name}'.");
    }

    private static DateTime ReadDateTime(JsonElement element, string name, DateTime fallback)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDateTime(out var result)
            ? result
            : fallback;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : 0;
    }

    private static int? ReadNullableInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;
    }

    private static uint ReadUInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetUInt32()
            : 0;
    }

    private static ulong ReadULong(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetUInt64()
            : 0;
    }

    private static float ReadFloat(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetSingle()
            : 0f;
    }

    private static float? ReadNullableFloat(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetSingle()
            : null;
    }

    private static double? ReadNullableDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind != JsonValueKind.Null
            ? value.GetDouble()
            : null;
    }

    private static bool? ReadNullableBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static Vec3? ReadVec3(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value)
            ? Vec3.FromJson(value)
            : null;
    }

    private static Vec3? ReadVec3OrXyAsXz(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var vec3 = Vec3.FromJson(value);
        if (vec3 != null)
        {
            return vec3;
        }

        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("X", out var x) &&
               value.TryGetProperty("Y", out var y)
            ? new Vec3(x.GetSingle(), 0f, y.GetSingle())
            : null;
    }

    private static IReadOnlyDictionary<string, int> ReadStringIntDictionary(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>();
        }

        return value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetInt32());
    }
}
