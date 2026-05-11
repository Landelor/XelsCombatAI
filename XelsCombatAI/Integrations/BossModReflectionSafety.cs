using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal sealed class BossModReflectionSafety
{
    private const string BossModPluginTypeName = "BossMod.Plugin";
    private const string BossModWPosTypeName = "BossMod.WPos";
    private const string BossModHintsTypeName = "BossMod.AIHints";
    private const string BossModActionDefinitionsTypeName = "BossMod.ActionDefinitions";
    private const string BossModNormalMovementTypeName = "BossMod.Autorotation.MiscAI.NormalMovement";
    private const double DashLockSeconds = 0.8d;
    private const int MaxLineCheckSamples = 256;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private object? bossModPlugin;
    private FieldInfo? hintsField;
    private FieldInfo? imminentSpecialModeField;
    private FieldInfo? forcedMovementField;
    private FieldInfo? wposXField;
    private FieldInfo? wposZField;
    private ConstructorInfo? wposConstructor;
    private MethodInfo? isDashDangerousMethod;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private string status = "unresolved";

    public BossModReflectionSafety(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public string Status => this.status;
    public string Diagnostics => string.Join(
        "; ",
        $"Status={this.status}",
        $"PluginCached={this.bossModPlugin != null}",
        $"HintsField={this.hintsField != null}",
        $"ImminentSpecialModeField={this.imminentSpecialModeField != null}",
        $"ForcedMovementField={this.forcedMovementField != null}",
        $"WPosConstructor={this.wposConstructor != null}",
        $"IsDashDangerousMethod={this.isDashDangerousMethod != null}",
        $"NextResolveUtc={this.nextResolveAttempt:O}");

    public void Reset()
    {
        this.bossModPlugin = null;
        this.hintsField = null;
        this.imminentSpecialModeField = null;
        this.forcedMovementField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.wposConstructor = null;
        this.isDashDangerousMethod = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.status = "unresolved";
    }

    public bool TryIsDashSafe(Vector3 from, Vector3 to, out string reason)
    {
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                this.ResetWithStatus("BMR hints unavailable");
                reason = this.status;
                return false;
            }

            if (this.HasImminentDashBlockingMode(hints))
            {
                reason = "BMR imminent movement lock";
                return false;
            }

            var fromWPos = this.CreateWPos(from);
            var toWPos = this.CreateWPos(to);
            var dangerous = (bool)(this.isDashDangerousMethod!.Invoke(null, [fromWPos, toWPos, hints]) ?? true);
            if (dangerous)
            {
                reason = "BMR reports dash destination dangerous";
                return false;
            }

            reason = "safe";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod dash safety.");
            this.ResetWithStatus("BMR reflection query failed");
            reason = this.status;
            return false;
        }
    }

    public bool TryIsPositionSafe(Vector3 position, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                this.ResetWithStatus("BMR hints unavailable");
                reason = this.status;
                return false;
            }

            var wpos = this.CreateWPos(position);
            safe = !(bool)(this.isDashDangerousMethod!.Invoke(null, [wpos, wpos, hints]) ?? true);
            reason = safe ? "safe" : "BMR reports current position dangerous";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod position safety.");
            this.ResetWithStatus("BMR reflection query failed");
            reason = this.status;
            return false;
        }
    }

    public bool TryCanAttemptDashNow(out string reason)
    {
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                this.ResetWithStatus("BMR hints unavailable");
                reason = this.status;
                return false;
            }

            if (this.HasImminentDashBlockingMode(hints))
            {
                reason = "BMR imminent movement lock";
                return false;
            }

            reason = "BMR dash timing allowed";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod dash timing.");
            this.ResetWithStatus("BMR reflection query failed");
            reason = this.status;
            return false;
        }
    }

    public bool TryGetSafeMovementIntent(Vector3 playerPosition, out Vector3 destination, out string reason)
    {
        destination = default;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        if (this.forcedMovementField == null)
        {
            reason = "BMR ForcedMovement field not found";
            return false;
        }

        try
        {
            var hints = this.hintsField!.GetValue(this.bossModPlugin);
            if (hints == null)
            {
                reason = "BMR hints unavailable";
                return false;
            }

            var rawValue = this.forcedMovementField.GetValue(hints);
            if (rawValue is not Vector3 movement)
            {
                reason = "BMR ForcedMovement not set";
                return false;
            }

            var xz = new Vector2(movement.X, movement.Z);
            if (xz.Length() < 0.5f)
            {
                reason = "BMR movement vector too small";
                return false;
            }

            var inferredDestination = playerPosition + new Vector3(xz.X, 0, xz.Y);
            if (!this.TryIsPositionSafe(inferredDestination, out var destSafe, out var destReason))
            {
                reason = destReason;
                return false;
            }

            if (!destSafe)
            {
                reason = "BMR inferred destination not safe";
                return false;
            }

            destination = inferredDestination;
            reason = "BMR movement intent confirmed safe";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose($"Could not query BMR movement intent: {ex.Message}");
            reason = "BMR movement intent query failed";
            return false;
        }
    }

    public bool TryCheckNavigationLine(Vector3 from, Vector3 to, out BossModNavigationLineCheck check)
    {
        check = BossModNavigationLineCheck.Unavailable(this.status);

        if (!this.EnsureResolved())
        {
            check = BossModNavigationLineCheck.Unavailable(this.status);
            return false;
        }

        try
        {
            var map = this.ResolveNormalMovementMap();
            if (map == null)
            {
                check = BossModNavigationLineCheck.Unavailable("BMR navigation map unavailable");
                return false;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var width = ReadInt(ReadField(map, "Width", Flags)).GetValueOrDefault();
            var height = ReadInt(ReadField(map, "Height", Flags)).GetValueOrDefault();
            var resolution = ReadFloat(ReadField(map, "Resolution", Flags)).GetValueOrDefault();
            var pixelMaxG = ReadField(map, "PixelMaxG", Flags) as float[];
            var pixelPriority = ReadField(map, "PixelPriority", Flags) as float[];
            var center = this.ReadWPos(ReadField(map, "Center", Flags));
            var rotation = ReadAngleRadians(ReadField(map, "Rotation", Flags)).GetValueOrDefault();
            if (width <= 0 || height <= 0 || resolution <= 0f || pixelMaxG == null || center == null)
            {
                check = BossModNavigationLineCheck.Unavailable("BMR navigation map incomplete");
                return false;
            }

            if (pixelMaxG.Length < width * height)
            {
                check = BossModNavigationLineCheck.Unavailable("BMR navigation map arrays incomplete");
                return false;
            }

            var center3 = new Vector3(center.Value.X, from.Y, center.Value.Y);
            var distance = Distance2D(from, to);
            var steps = Math.Clamp((int)MathF.Ceiling(distance / MathF.Max(0.25f, resolution * 0.5f)), 1, MaxLineCheckSamples);
            var fromGrid = WorldToGrid(from, center3, rotation, resolution, width, height);
            var usePriority = pixelPriority != null && pixelPriority.Length >= width * height;
            var startedInNoGo = false;
            if (IsGridInside(fromGrid, width, height))
            {
                var fromIndex = (fromGrid.Y * width) + fromGrid.X;
                startedInNoGo = IsNavigationCellNoGo(
                    pixelMaxG[fromIndex],
                    usePriority ? pixelPriority![fromIndex] : 0f,
                    out _);
            }

            var exitedStartingNoGo = !startedInNoGo;
            for (var i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                var point = Vector3.Lerp(from, to, t);
                var grid = WorldToGrid(point, center3, rotation, resolution, width, height);
                if (!IsGridInside(grid, width, height))
                {
                    check = BossModNavigationLineCheck.Blocked("outside BMR pathfinding map", point, distance * t);
                    return true;
                }

                var maxG = pixelMaxG[(grid.Y * width) + grid.X];
                var priority = usePriority ? pixelPriority![(grid.Y * width) + grid.X] : 0f;
                if (IsNavigationCellNoGo(maxG, priority, out var noGoReason))
                {
                    if (exitedStartingNoGo)
                    {
                        check = BossModNavigationLineCheck.Blocked(noGoReason.Replace("navigation cell", "path", StringComparison.Ordinal), point, distance * t);
                        return true;
                    }

                    continue;
                }

                exitedStartingNoGo = true;
            }

            check = BossModNavigationLineCheck.ClearResult("BMR path line clear");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod navigation line safety.");
            check = BossModNavigationLineCheck.Unavailable("BMR navigation line query failed");
            return false;
        }
    }

    private static bool IsGridInside((int X, int Y) grid, int width, int height)
    {
        return grid.X >= 0 && grid.X < width && grid.Y >= 0 && grid.Y < height;
    }

    private static bool IsActiveDanger(float pixelMaxG)
    {
        return float.IsFinite(pixelMaxG) && pixelMaxG >= 0f && pixelMaxG <= 1f;
    }

    private static bool IsFullySafeNavigationCell(float pixelMaxG, float pixelPriority)
    {
        return pixelMaxG >= float.MaxValue * 0.5f && pixelPriority >= 0f;
    }

    private static bool IsNavigationCellSafe(float pixelMaxG, float pixelPriority, out string reason)
    {
        if (float.IsNaN(pixelMaxG))
        {
            reason = "BMR navigation cell unknown";
            return false;
        }

        if (pixelMaxG < 0f)
        {
            reason = "BMR navigation cell blocked";
            return false;
        }

        if (IsActiveDanger(pixelMaxG))
        {
            reason = "BMR navigation cell active danger";
            return false;
        }

        if (pixelMaxG < float.MaxValue * 0.5f)
        {
            reason = "BMR navigation cell future danger";
            return false;
        }

        if (pixelPriority < 0f)
        {
            reason = "BMR navigation cell avoid buffer";
            return false;
        }

        reason = pixelPriority > 0f ? "BMR navigation cell goal" : "BMR navigation cell safe";
        return true;
    }

    private static bool IsNavigationCellNoGo(float pixelMaxG, float pixelPriority, out string reason)
    {
        return !IsNavigationCellSafe(pixelMaxG, pixelPriority, out reason);
    }

    public bool TryIsNavigationCellSafe(Vector3 position, out bool safe, out string reason)
    {
        safe = false;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var map = this.ResolveNormalMovementMap();
            if (map == null)
            {
                reason = "BMR navigation map unavailable";
                return false;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var width = ReadInt(ReadField(map, "Width", Flags)).GetValueOrDefault();
            var height = ReadInt(ReadField(map, "Height", Flags)).GetValueOrDefault();
            var resolution = ReadFloat(ReadField(map, "Resolution", Flags)).GetValueOrDefault();
            var pixelMaxG = ReadField(map, "PixelMaxG", Flags) as float[];
            var pixelPriority = ReadField(map, "PixelPriority", Flags) as float[];
            var center = this.ReadWPos(ReadField(map, "Center", Flags));
            var rotation = ReadAngleRadians(ReadField(map, "Rotation", Flags)).GetValueOrDefault();
            if (width <= 0 || height <= 0 || resolution <= 0f || pixelMaxG == null || pixelPriority == null || center == null)
            {
                reason = "BMR navigation map incomplete";
                return false;
            }

            if (pixelMaxG.Length < width * height || pixelPriority.Length < width * height)
            {
                reason = "BMR navigation map arrays incomplete";
                return false;
            }

            var center3 = new Vector3(center.Value.X, position.Y, center.Value.Y);
            var grid = WorldToGrid(position, center3, rotation, resolution, width, height);
            if (!IsGridInside(grid, width, height))
            {
                reason = "outside BMR pathfinding map";
                return true;
            }

            var index = (grid.Y * width) + grid.X;
            safe = IsNavigationCellSafe(pixelMaxG[index], pixelPriority[index], out reason);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod navigation cell safety.");
            reason = "BMR navigation cell query failed";
            return false;
        }
    }

    public bool TryGetNearestNavigationBlocker(Vector3 position, float maxDistance, bool includeAvoidBuffer, out BossModNavigationBlocker blocker)
    {
        blocker = BossModNavigationBlocker.Unavailable(this.status);

        if (!this.EnsureResolved())
        {
            blocker = BossModNavigationBlocker.Unavailable(this.status);
            return false;
        }

        try
        {
            var map = this.ResolveNormalMovementMap();
            if (map == null)
            {
                blocker = BossModNavigationBlocker.Unavailable("BMR navigation map unavailable");
                return false;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var width = ReadInt(ReadField(map, "Width", Flags)).GetValueOrDefault();
            var height = ReadInt(ReadField(map, "Height", Flags)).GetValueOrDefault();
            var resolution = ReadFloat(ReadField(map, "Resolution", Flags)).GetValueOrDefault();
            var pixelMaxG = ReadField(map, "PixelMaxG", Flags) as float[];
            var pixelPriority = ReadField(map, "PixelPriority", Flags) as float[];
            var center = this.ReadWPos(ReadField(map, "Center", Flags));
            var rotation = ReadAngleRadians(ReadField(map, "Rotation", Flags)).GetValueOrDefault();
            if (width <= 0 || height <= 0 || resolution <= 0f || pixelMaxG == null || center == null)
            {
                blocker = BossModNavigationBlocker.Unavailable("BMR navigation map incomplete");
                return false;
            }

            if (pixelMaxG.Length < width * height ||
                (includeAvoidBuffer && (pixelPriority == null || pixelPriority.Length < width * height)))
            {
                blocker = BossModNavigationBlocker.Unavailable("BMR navigation map arrays incomplete");
                return false;
            }

            var center3 = new Vector3(center.Value.X, position.Y, center.Value.Y);
            var grid = WorldToGrid(position, center3, rotation, resolution, width, height);
            var searchCells = Math.Max(1, (int)MathF.Ceiling(maxDistance / resolution));
            var bestDistance = float.MaxValue;
            Vector3? bestPoint = null;
            string bestReason = includeAvoidBuffer ? "BMR blocked/avoid-buffer boundary" : "BMR blocked boundary";

            for (var y = Math.Max(0, grid.Y - searchCells); y <= Math.Min(height - 1, grid.Y + searchCells); y++)
            {
                for (var x = Math.Max(0, grid.X - searchCells); x <= Math.Min(width - 1, grid.X + searchCells); x++)
                {
                    var index = (y * width) + x;
                    var maxG = pixelMaxG[index];
                    var priority = pixelPriority?[index] ?? 0f;
                    if (maxG >= 0f && (!includeAvoidBuffer || priority >= 0f))
                    {
                        continue;
                    }

                    var point = GridToWorld(x, y, center3, rotation, resolution, width, height);
                    var distance = Distance2D(position, point);
                    if (distance <= maxDistance && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestPoint = point;
                        bestReason = maxG < 0f
                            ? "BMR blocked boundary"
                            : "BMR avoid-buffer boundary";
                    }
                }
            }

            if (bestPoint.HasValue)
            {
                blocker = new(true, bestReason, bestPoint, bestDistance);
                return true;
            }

            blocker = BossModNavigationBlocker.Clear("no nearby BMR blocker");
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod navigation blocker.");
            blocker = BossModNavigationBlocker.Unavailable("BMR navigation blocker query failed");
            return false;
        }
    }

    public bool TryFindNearestSafeNavigationPoint(Vector3 position, float maxDistance, out BossModSafeNavigationPoint point)
    {
        point = BossModSafeNavigationPoint.Unavailable(this.status);

        if (!this.EnsureResolved())
        {
            point = BossModSafeNavigationPoint.Unavailable(this.status);
            return false;
        }

        try
        {
            var map = this.ResolveNormalMovementMap();
            if (map == null)
            {
                point = BossModSafeNavigationPoint.Unavailable("BMR navigation map unavailable");
                return false;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var width = ReadInt(ReadField(map, "Width", Flags)).GetValueOrDefault();
            var height = ReadInt(ReadField(map, "Height", Flags)).GetValueOrDefault();
            var resolution = ReadFloat(ReadField(map, "Resolution", Flags)).GetValueOrDefault();
            var pixelMaxG = ReadField(map, "PixelMaxG", Flags) as float[];
            var pixelPriority = ReadField(map, "PixelPriority", Flags) as float[];
            var center = this.ReadWPos(ReadField(map, "Center", Flags));
            var rotation = ReadAngleRadians(ReadField(map, "Rotation", Flags)).GetValueOrDefault();
            if (width <= 0 || height <= 0 || resolution <= 0f || pixelMaxG == null || pixelPriority == null || center == null)
            {
                point = BossModSafeNavigationPoint.Unavailable("BMR navigation map incomplete");
                return false;
            }

            if (pixelMaxG.Length < width * height || pixelPriority.Length < width * height)
            {
                point = BossModSafeNavigationPoint.Unavailable("BMR navigation map arrays incomplete");
                return false;
            }

            var center3 = new Vector3(center.Value.X, position.Y, center.Value.Y);
            var grid = WorldToGrid(position, center3, rotation, resolution, width, height);
            var searchCells = Math.Max(1, (int)MathF.Ceiling(maxDistance / resolution));
            var bestDistance = float.MaxValue;
            Vector3? bestPoint = null;
            var bestClearDistance = float.MaxValue;
            Vector3? bestClearPoint = null;

            for (var y = Math.Max(0, grid.Y - searchCells); y <= Math.Min(height - 1, grid.Y + searchCells); y++)
            {
                for (var x = Math.Max(0, grid.X - searchCells); x <= Math.Min(width - 1, grid.X + searchCells); x++)
                {
                    var index = (y * width) + x;
                    if (!IsFullySafeNavigationCell(pixelMaxG[index], pixelPriority[index]))
                    {
                        continue;
                    }

                    var candidate = GridToWorld(x, y, center3, rotation, resolution, width, height);
                    var distance = Distance2D(position, candidate);
                    if (distance < 0.75f || distance > maxDistance)
                    {
                        continue;
                    }

                    if (HasSafeNavigationClearance(x, y, width, height, pixelMaxG, pixelPriority) &&
                        distance < bestClearDistance)
                    {
                        bestClearDistance = distance;
                        bestClearPoint = candidate;
                    }

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestPoint = candidate;
                    }
                }
            }

            if (bestClearPoint.HasValue)
            {
                point = new(true, "nearest fully safe BMR navigation cell with clearance", bestClearPoint.Value, bestClearDistance);
                return true;
            }

            if (!bestPoint.HasValue)
            {
                point = BossModSafeNavigationPoint.Unavailable("no nearby fully safe BMR navigation cell");
                return false;
            }

            point = new(true, "nearest fully safe BMR navigation cell", bestPoint.Value, bestDistance);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not query reflected BossMod safe navigation point.");
            point = BossModSafeNavigationPoint.Unavailable("BMR safe navigation point query failed");
            return false;
        }
    }

    private static bool HasSafeNavigationClearance(int x, int y, int width, int height, float[] pixelMaxG, float[] pixelPriority)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                {
                    return false;
                }

                var index = (ny * width) + nx;
                if (!IsFullySafeNavigationCell(pixelMaxG[index], pixelPriority[index]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool EnsureResolved()
    {
        if (this.bossModPlugin != null &&
            this.hintsField != null &&
            this.wposXField != null &&
            this.wposZField != null &&
            this.wposConstructor != null &&
            this.isDashDangerousMethod != null)
        {
            return true;
        }

        if (DateTime.UtcNow < this.nextResolveAttempt)
        {
            return false;
        }

        this.nextResolveAttempt = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var plugin = this.FindBossModPlugin();
            if (plugin == null)
            {
                this.ResetWithStatus("BMR plugin instance not found");
                return false;
            }

            var assembly = plugin.GetType().Assembly;
            var hintsType = assembly.GetType(BossModHintsTypeName);
            var wposType = assembly.GetType(BossModWPosTypeName);
            var actionDefinitionsType = assembly.GetType(BossModActionDefinitionsTypeName);
            if (hintsType == null || wposType == null || actionDefinitionsType == null)
            {
                this.ResetWithStatus($"BMR safety types not found: {FormatMissing(
                    (hintsType == null, BossModHintsTypeName),
                    (wposType == null, BossModWPosTypeName),
                    (actionDefinitionsType == null, BossModActionDefinitionsTypeName))}");
                return false;
            }

            var hints = plugin.GetType().GetField("_hints", BindingFlags.Instance | BindingFlags.NonPublic);
            var wposX = wposType.GetField("X", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var wposZ = wposType.GetField("Z", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var wposCtor = wposType.GetConstructor([typeof(float), typeof(float)]);
            var isDashDangerous = actionDefinitionsType.GetMethod(
                "IsDashDangerous",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [wposType, wposType, hintsType],
                null);

            if (hints == null || wposX == null || wposZ == null || wposCtor == null || isDashDangerous == null)
            {
                this.ResetWithStatus($"BMR safety members not found: {FormatMissing(
                    (hints == null, "BossMod.Plugin._hints"),
                    (wposX == null, "BossMod.WPos.X"),
                    (wposZ == null, "BossMod.WPos.Z"),
                    (wposCtor == null, "BossMod.WPos(float, float)"),
                    (isDashDangerous == null, "BossMod.ActionDefinitions.IsDashDangerous(WPos, WPos, AIHints)"))}");
                return false;
            }

            this.bossModPlugin = plugin;
            this.hintsField = hints;
            this.imminentSpecialModeField = hintsType.GetField("ImminentSpecialMode", BindingFlags.Instance | BindingFlags.Public);
            this.forcedMovementField = hintsType.GetField("ForcedMovement", BindingFlags.Instance | BindingFlags.Public);
            this.wposXField = wposX;
            this.wposZField = wposZ;
            this.wposConstructor = wposCtor;
            this.isDashDangerousMethod = isDashDangerous;
            this.status = "available";
            return true;
        }
        catch (Exception ex)
        {
            this.log.Verbose(ex, "Could not resolve reflected BossMod safety integration.");
            this.ResetWithStatus("BMR reflection resolve failed");
            return false;
        }
    }

    private object? FindBossModPlugin()
    {
        foreach (var plugin in ReflectionObjectSearch.EnumerateLoadedPlugins(
                     this.pluginInterface,
                     "BossModReborn",
                     "BossMod Reborn",
                     "BossMod"))
        {
            var found = FindObject(plugin, BossModPluginTypeName, maxDepth: 8);
            if (found != null)
            {
                return found;
            }
        }

        return FindObject(this.pluginInterface, BossModPluginTypeName, maxDepth: 8);
    }

    private object CreateWPos(Vector3 position)
    {
        return this.wposConstructor!.Invoke([position.X, position.Z]);
    }

    private bool HasImminentDashBlockingMode(object hints)
    {
        if (this.imminentSpecialModeField == null)
        {
            return false;
        }

        var value = this.imminentSpecialModeField.GetValue(hints);
        if (value == null)
        {
            return false;
        }

        var type = value.GetType();
        var mode = GetTupleField(value, type, "mode", "Item1");
        var activation = GetTupleField(value, type, "activation", "Item2");
        if (mode == null || activation is not DateTime deadline)
        {
            return false;
        }

        var modeName = mode.ToString();
        return (string.Equals(modeName, "Pyretic", StringComparison.Ordinal) ||
                string.Equals(modeName, "NoMovement", StringComparison.Ordinal)) &&
               deadline <= DateTime.Now.AddSeconds(DashLockSeconds);
    }

    private void ResetWithStatus(string newStatus)
    {
        var oldStatus = this.status;
        this.bossModPlugin = null;
        this.hintsField = null;
        this.imminentSpecialModeField = null;
        this.forcedMovementField = null;
        this.wposXField = null;
        this.wposZField = null;
        this.wposConstructor = null;
        this.isDashDangerousMethod = null;
        this.status = newStatus;
        if (!string.Equals(oldStatus, newStatus, StringComparison.Ordinal))
        {
            this.log.Verbose($"BossMod reflected gap closer safety unavailable: {newStatus}");
        }
    }

    private static object? GetTupleField(object value, Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null)
            {
                return field.GetValue(value);
            }
        }

        return null;
    }

    private object? ResolveNormalMovementMap()
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var normalMovementType = this.bossModPlugin?.GetType().Assembly.GetType(BossModNormalMovementTypeName);
        var normalMovement = normalMovementType?.GetField("Instance", Flags)?.GetValue(null);
        var context = ReadField(normalMovement, "_navCtx", Flags);
        return ReadField(context, "Map", Flags);
    }

    private Vector2? ReadWPos(object? value)
    {
        if (value == null || this.wposXField == null || this.wposZField == null)
        {
            return null;
        }

        var x = ReadFloat(this.wposXField.GetValue(value));
        var z = ReadFloat(this.wposZField.GetValue(value));
        return x.HasValue && z.HasValue ? new Vector2(x.Value, z.Value) : null;
    }

    private static object? ReadField(object? instance, string name, BindingFlags flags)
    {
        return instance?.GetType().GetField(name, flags)?.GetValue(instance);
    }

    private static float? ReadFloat(object? value)
    {
        return value switch
        {
            float f when float.IsFinite(f) => f,
            double d when double.IsFinite(d) => (float)d,
            int i => i,
            uint u => u,
            _ => null
        };
    }

    private static int? ReadInt(object? value)
    {
        return value switch
        {
            int i => i,
            uint u when u <= int.MaxValue => (int)u,
            _ => null
        };
    }

    private static float? ReadAngleRadians(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return ReadFloat(ReadField(value, "Rad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    private static (int X, int Y) WorldToGrid(Vector3 position, Vector3 center, float rotation, float resolution, int width, int height)
    {
        var dx = position.X - center.X;
        var dz = position.Z - center.Z;
        var sin = MathF.Sin(rotation);
        var cos = MathF.Cos(rotation);
        var gx = (width >> 1) + ((dx * cos) - (dz * sin)) / resolution;
        var gy = (height >> 1) + ((dx * sin) + (dz * cos)) / resolution;
        return ((int)MathF.Floor(gx), (int)MathF.Floor(gy));
    }

    private static Vector3 GridToWorld(int x, int y, Vector3 center, float rotation, float resolution, int width, int height)
    {
        var localX = ((x + 0.5f) - (width >> 1)) * resolution;
        var localZ = ((y + 0.5f) - (height >> 1)) * resolution;
        var sin = MathF.Sin(rotation);
        var cos = MathF.Cos(rotation);
        return new(
            center.X + (localX * cos) + (localZ * sin),
            center.Y,
            center.Z + (-localX * sin) + (localZ * cos));
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static string FormatMissing(params (bool Missing, string Name)[] members)
    {
        var missing = new List<string>();
        foreach (var member in members)
        {
            if (member.Missing)
            {
                missing.Add(member.Name);
            }
        }

        return string.Join(", ", missing);
    }

    private static object? FindObject(object root, string typeFullName, int maxDepth)
    {
        var queue = new Queue<(object Value, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (!visited.Add(value))
            {
                continue;
            }

            var type = value.GetType();
            if (type.FullName == typeFullName)
            {
                return value;
            }

            if (depth >= maxDepth || ShouldSkip(type))
            {
                continue;
            }

            foreach (var child in EnumerateChildren(value, type))
            {
                if (child != null)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateChildren(object value, Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(Flags))
        {
            if (field.IsStatic || ShouldSkip(field.FieldType))
            {
                continue;
            }

            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        foreach (var property in type.GetProperties(Flags))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead || ShouldSkip(property.PropertyType))
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            yield return child;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var count = 0;
            foreach (var child in enumerable)
            {
                yield return child;
                if (++count >= 128)
                {
                    yield break;
                }
            }
        }
    }

    private static bool ShouldSkip(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(Type) ||
               typeof(Assembly).IsAssignableFrom(type) ||
               typeof(MemberInfo).IsAssignableFrom(type);
    }
}

internal sealed record BossModNavigationLineCheck(
    bool Clear,
    string Reason,
    Vector3? BlockedPoint,
    float? BlockedDistance)
{
    public static BossModNavigationLineCheck ClearResult(string reason) => new(true, reason, null, null);
    public static BossModNavigationLineCheck Blocked(string reason, Vector3 point, float distance) => new(false, reason, point, distance);
    public static BossModNavigationLineCheck Unavailable(string reason) => new(true, reason, null, null);
}

internal sealed record BossModNavigationBlocker(
    bool Found,
    string Reason,
    Vector3? Point,
    float? Distance)
{
    public static BossModNavigationBlocker Clear(string reason) => new(false, reason, null, null);
    public static BossModNavigationBlocker Unavailable(string reason) => new(false, reason, null, null);
}

internal sealed record BossModSafeNavigationPoint(
    bool Found,
    string Reason,
    Vector3 Point,
    float Distance)
{
    public static BossModSafeNavigationPoint Unavailable(string reason) => new(false, reason, default, 0f);
}
