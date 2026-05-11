using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XelsCombatAI.Integrations;

internal enum RsrAoeShape
{
    Circle = 2,
    Cone = 3,
    StraightLine = 4
}

internal sealed record RsrAoeActionSnapshot(
    uint ActionId,
    uint AdjustedActionId,
    string ActionName,
    RsrAoeShape Shape,
    float Range,
    float EffectRange,
    float HalfWidth,
    int AoeCount,
    ulong PrimaryTargetId,
    Vector3 PrimaryTargetPosition,
    float PrimaryTargetRadius,
    int AffectedTargetCount,
    bool IsFriendly,
    bool IsTargetArea);

internal sealed class RotationSolverActionReflection(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
    private const string ActionUpdaterTypeName = "RotationSolver.Updaters.ActionUpdater";
    private static readonly TimeSpan LoadedCheckInterval = TimeSpan.FromSeconds(5);

    private Type? actionUpdaterType;
    private PropertyInfo? nextGcdActionProperty;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private DateTime nextLoadedCheck = DateTime.MinValue;
    private string status = "unresolved";

    public string Status => this.status;
    public string Diagnostics => string.Join(
        "; ",
        $"Status={this.status}",
        $"ActionUpdaterType={this.actionUpdaterType != null}",
        $"NextGCDActionProperty={this.nextGcdActionProperty != null}",
        $"NextResolveUtc={this.nextResolveAttempt:O}");

    public void Reset()
    {
        this.actionUpdaterType = null;
        this.nextGcdActionProperty = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.nextLoadedCheck = DateTime.MinValue;
        this.status = "unresolved";
    }

    public bool TryGetUpcomingGcd(bool requirePreview, [NotNullWhen(true)] out RsrAoeActionSnapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            return false;
        }

        try
        {
            var action = this.nextGcdActionProperty!.GetValue(null);
            if (action == null)
            {
                reason = "RSR next GCD unavailable";
                return false;
            }

            var actionType = action.GetType();
            var targetResult = GetPropertyValue(action, actionType, requirePreview ? "PreviewTarget" : "Target");
            if (targetResult == null && requirePreview)
            {
                reason = "RSR preview target unavailable";
                return false;
            }

            targetResult ??= GetPropertyValue(action, actionType, "Target");
            if (targetResult == null)
            {
                reason = "RSR target unavailable";
                return false;
            }

            var actionRow = GetPropertyValue(action, actionType, "Action");
            if (actionRow == null)
            {
                reason = "RSR action row unavailable";
                return false;
            }

            var castType = Convert.ToInt32(GetPropertyValue(actionRow, actionRow.GetType(), "CastType") ?? 0);

            var targetResultType = targetResult.GetType();
            var primaryTarget = GetPropertyValue(targetResult, targetResultType, "Target");
            if (primaryTarget == null)
            {
                reason = "RSR primary target unavailable";
                return false;
            }

            var primaryTargetType = primaryTarget.GetType();
            var primaryPosition = ReadVector3(GetPropertyValue(primaryTarget, primaryTargetType, "Position"));
            var primaryRadius = ReadFloat(GetPropertyValue(primaryTarget, primaryTargetType, "HitboxRadius"), 0f);
            var primaryId = ReadUlong(GetPropertyValue(primaryTarget, primaryTargetType, "GameObjectId"));
            if (primaryId == 0)
            {
                primaryId = ReadUlong(GetPropertyValue(primaryTarget, primaryTargetType, "EntityId"));
            }

            var affectedTargets = GetPropertyValue(targetResult, targetResultType, "AffectedTargets") as IEnumerable;
            var affectedCount = CountEnumerable(affectedTargets);
            var effectRange = ReadFloat(GetPropertyValue(actionRow, actionRow.GetType(), "EffectRange"), 0f);
            var xAxisModifier = ReadFloat(GetPropertyValue(actionRow, actionRow.GetType(), "XAxisModifier"), 0f);
            var targetInfo = GetPropertyValue(action, actionType, "TargetInfo");
            var range = targetInfo != null ? ReadFloat(GetPropertyValue(targetInfo, targetInfo.GetType(), "Range"), 25f) : 25f;
            var isTargetArea = targetInfo != null && ReadBool(GetPropertyValue(targetInfo, targetInfo.GetType(), "IsTargetArea"));
            var config = GetPropertyValue(action, actionType, "Config");
            var aoeCount = config != null ? Math.Max(1, Convert.ToInt32(GetPropertyValue(config, config.GetType(), "AoeCount") ?? 3)) : 3;
            var setting = GetPropertyValue(action, actionType, "Setting");
            var isFriendly = setting != null && Convert.ToBoolean(GetPropertyValue(setting, setting.GetType(), "IsFriendly") ?? false);
            var actionId = Convert.ToUInt32(GetPropertyValue(action, actionType, "ID") ?? 0u);
            var adjustedId = Convert.ToUInt32(GetPropertyValue(action, actionType, "AdjustedID") ?? actionId);
            var name = GetPropertyValue(actionRow, actionRow.GetType(), "Name")?.ToString() ?? adjustedId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // CastType=1 (Targeted) in the Lumina sheet describes targeting UI, not AoE geometry.
            // Some actions (e.g. Chain Saw) have CastType=1 but fire a multi-hit line AoE.
            // RSR's GetCanTarget/GetAffectsTarget skips geometry for CastType=1 so affectedCount is
            // always 0 or 1 — we can't rely on it. Instead infer shape from the action sheet geometry:
            //   XAxisModifier > 0 && EffectRange > 0 -> StraightLine beam (beam half-width from XAxisModifier)
            //   XAxisModifier == 0 && EffectRange > 0 && Range >> EffectRange -> targeted Circle AoE
            // For inferred beams, override Range to 0 so the self-origin repositioning path is taken.
            // For targeted circles, preserve action range so the pack-positioning controller can skip
            // body repositioning and avoid tiny correction steps for jobs like AST.
            int resolvedCastType;
            float resolvedRange;
            if (castType is 2 or 3 or 4)
            {
                resolvedCastType = castType;
                resolvedRange = range;
            }
            else if (castType == 1 && effectRange > 0f && !isTargetArea && !isFriendly)
            {
                // XAxisModifier > 0 indicates a beam width — treat as StraightLine.
                // Otherwise it's a radial AoE around the target — treat as Circle.
                resolvedCastType = xAxisModifier > 0f ? 4 : 2;
                resolvedRange = xAxisModifier > 0f ? 0f : range;
            }
            else if (castType == 1 && effectRange > 0f && isFriendly)
            {
                // Friendly party AoE heals/shields (e.g. Physis II, Eukrasian Prognosis, Kerachole
                // on SGE; Medica II on WHM). Always radial — treat as Circle. Single-target
                // heals/shields have EffectRange=0 and are excluded by the effectRange > 0f guard.
                resolvedCastType = (int)RsrAoeShape.Circle;
                resolvedRange = 0f;
            }
            else
            {
                reason = $"RSR unsupported cast type {castType} for {name}";
                return false;
            }

            // HalfWidth semantics differ by shape:
            //   StraightLine: beam half-width in yalms (XAxisModifier / 2)
            //   Cone: half-angle in radians — RSR hardcodes π/3 (60°) regardless of XAxisModifier
            //   Circle: unused
            var halfWidth = (RsrAoeShape)resolvedCastType switch
            {
                RsrAoeShape.StraightLine => xAxisModifier > 0f ? xAxisModifier / 2f : 2f,
                RsrAoeShape.Cone => MathF.PI / 3f,
                _ => 0f,
            };

            snapshot = new(
                actionId,
                adjustedId,
                name,
                (RsrAoeShape)resolvedCastType,
                Math.Max(1f, resolvedRange),
                Math.Max(1f, effectRange),
                halfWidth,
                aoeCount,
                primaryId,
                primaryPosition,
                primaryRadius,
                affectedCount,
                isFriendly,
                isTargetArea);
            reason = "available";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not query reflected RSR next GCD action: {ex.Message}");
            this.ResetWithStatus("RSR action reflection query failed");
            reason = "RSR action reflection query failed";
            return false;
        }
    }

    private bool EnsureResolved()
    {
        var now = DateTime.UtcNow;
        if (this.nextGcdActionProperty != null)
        {
            if (now >= this.nextLoadedCheck)
            {
                this.nextLoadedCheck = now.Add(LoadedCheckInterval);
                if (!IsRotationSolverLoaded(pluginInterface))
                {
                    this.ResetWithStatus("RSR plugin not loaded");
                    return false;
                }
            }

            return true;
        }

        if (now < this.nextResolveAttempt)
        {
            return false;
        }

        this.nextResolveAttempt = now.AddSeconds(5);
        if (!IsRotationSolverLoaded(pluginInterface))
        {
            this.ResetWithStatus("RSR plugin not loaded");
            return false;
        }

        try
        {
            var type = FindActionUpdaterType();
            if (type == null)
            {
                this.ResetWithStatus("RSR ActionUpdater type not found");
                return false;
            }

            var property = type.GetProperty("NextGCDAction", BindingFlags.Static | BindingFlags.NonPublic);
            if (property == null)
            {
                this.ResetWithStatus("RSR NextGCDAction property not found");
                return false;
            }

            this.actionUpdaterType = type;
            this.nextGcdActionProperty = property;
            this.nextLoadedCheck = now.Add(LoadedCheckInterval);
            this.status = "available";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not resolve reflected RSR action integration: {ex.Message}");
            this.ResetWithStatus("RSR action reflection resolve failed");
            return false;
        }
    }

    private Type? FindActionUpdaterType()
    {
        var plugin = ReflectionObjectSearch.FindLoadedPlugin(
            pluginInterface,
            "RotationSolver.RotationSolverPlugin",
            maxDepth: 2,
            "RotationSolver",
            "Rotation Solver Reborn");
        if (plugin == null)
        {
            return null;
        }

        var assembly = plugin.GetType().Assembly;
        try
        {
            return assembly.GetType(ActionUpdaterTypeName, throwOnError: false);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Could not resolve reflected RSR action updater type.");
            return null;
        }
    }

    private static bool IsRotationSolverLoaded(IDalamudPluginInterface pluginInterface)
    {
        return ReflectionObjectSearch.HasLoadedPlugin(
            pluginInterface,
            "RotationSolver",
            "RotationSolverReborn",
            "Rotation Solver Reborn",
            "RotationSolver Reborn");
    }

    private void ResetWithStatus(string newStatus)
    {
        this.actionUpdaterType = null;
        this.nextGcdActionProperty = null;
        this.status = newStatus;
    }

    private static object? GetPropertyValue(object value, Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    }

    private static Vector3 ReadVector3(object? value)
    {
        return value is Vector3 vector ? vector : default;
    }

    private static float ReadFloat(object? value, float fallback)
    {
        return value == null ? fallback : Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ReadBool(object? value)
    {
        return value != null && Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ulong ReadUlong(object? value)
    {
        return value == null ? 0UL : Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountEnumerable(IEnumerable? values)
    {
        if (values == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var _ in values)
        {
            ++count;
        }

        return count;
    }
}
