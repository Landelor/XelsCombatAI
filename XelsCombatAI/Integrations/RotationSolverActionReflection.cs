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
    bool IsTargetArea,
    bool IsTargetCenteredCircle,
    float GcdRemaining = -1f,
    float GcdElapsed = -1f,
    float GcdTotal = -1f);

internal enum RsrRedMageMeleeTrack
{
    None,
    SingleTarget,
    AoE
}

internal sealed record RsrRedMageMeleeIntent(
    string RotationName,
    RsrRedMageMeleeTrack Track,
    uint ActionId,
    string ActionName,
    int AffectedTargets,
    string Reason,
    bool SuppressLocalFallback);

internal sealed class RotationSolverActionReflection(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
    private const string ActionUpdaterTypeName = "RotationSolver.Updaters.ActionUpdater";
    private const string DataCenterTypeName = "RotationSolver.Basic.DataCenter";
    private const string RebornRedMageRotationName = "RotationSolver.RebornRotations.Magical.RDM_Reborn";
    private const string BeirutaRedMageRotationName = "RotationSolver.ExtraRotations.Magical.BeirutaRDM";
    private static readonly TimeSpan LoadedCheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ContractProbeInterval = TimeSpan.FromSeconds(5);

    private Type? actionUpdaterType;
    private Type? dataCenterType;
    private PropertyInfo? nextGcdActionProperty;
    private PropertyInfo? currentRotationProperty;
    private PropertyInfo? defaultGcdRemainProperty;
    private PropertyInfo? defaultGcdElapsedProperty;
    private PropertyInfo? defaultGcdTotalProperty;
    private DateTime nextResolveAttempt = DateTime.MinValue;
    private DateTime nextLoadedCheck = DateTime.MinValue;
    private DateTime nextRedMageContractProbe = DateTime.MinValue;
    private string status = "unresolved";
    private string redMageMeleeDiagnostics = "not checked";
    private string lastRedMageMeleeQuery = "not checked";

    public string Status => this.status;
    public string Diagnostics => string.Join(
        "; ",
        $"Status={this.status}",
        $"ActionUpdaterType={this.actionUpdaterType != null}",
        $"NextGCDActionProperty={this.nextGcdActionProperty != null}",
        $"DataCenterType={this.dataCenterType != null}",
        $"CurrentRotationProperty={this.currentRotationProperty != null}",
        $"DefaultGCDRemainProperty={this.defaultGcdRemainProperty != null}",
        $"DefaultGCDElapsedProperty={this.defaultGcdElapsedProperty != null}",
        $"DefaultGCDTotalProperty={this.defaultGcdTotalProperty != null}",
        $"NextResolveUtc={this.nextResolveAttempt:O}");
    public string RedMageMeleeDiagnostics => this.GetRedMageMeleeDiagnostics();

    public void Reset()
    {
        this.actionUpdaterType = null;
        this.dataCenterType = null;
        this.nextGcdActionProperty = null;
        this.currentRotationProperty = null;
        this.defaultGcdRemainProperty = null;
        this.defaultGcdElapsedProperty = null;
        this.defaultGcdTotalProperty = null;
        this.nextResolveAttempt = DateTime.MinValue;
        this.nextLoadedCheck = DateTime.MinValue;
        this.nextRedMageContractProbe = DateTime.MinValue;
        this.status = "unresolved";
        this.redMageMeleeDiagnostics = "not checked";
        this.lastRedMageMeleeQuery = "not checked";
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
            var gcdRemaining = ReadStaticFloat(this.defaultGcdRemainProperty, -1f);
            var gcdElapsed = ReadStaticFloat(this.defaultGcdElapsedProperty, -1f);
            var gcdTotal = ReadStaticFloat(this.defaultGcdTotalProperty, -1f);

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
            var isTargetCenteredCircle = false;
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
                isTargetCenteredCircle = xAxisModifier <= 0f;
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
                isTargetArea,
                isTargetCenteredCircle,
                gcdRemaining,
                gcdElapsed,
                gcdTotal);
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

    public bool TryGetRedMageMeleeIntent([NotNullWhen(true)] out RsrRedMageMeleeIntent? intent, out string reason)
    {
        intent = null;
        reason = string.Empty;

        if (!this.EnsureResolved())
        {
            reason = this.status;
            this.lastRedMageMeleeQuery = $"unavailable: {reason}";
            return false;
        }

        try
        {
            var rotation = this.currentRotationProperty!.GetValue(null);
            if (rotation == null)
            {
                reason = "RSR current rotation unavailable";
                this.lastRedMageMeleeQuery = reason;
                return false;
            }

            var rotationType = rotation.GetType();
            var rotationName = rotationType.FullName ?? rotationType.Name;
            intent = rotationName switch
            {
                BeirutaRedMageRotationName => this.BuildBeirutaRedMageIntent(rotation, rotationType),
                RebornRedMageRotationName => this.BuildRebornRedMageIntent(rotation, rotationType),
                _ => null
            };

            if (intent == null)
            {
                reason = $"RSR current rotation is {rotationName}";
                this.lastRedMageMeleeQuery = reason;
                return false;
            }

            reason = intent.Reason;
            this.lastRedMageMeleeQuery = $"{intent.RotationName}/{intent.Track}: {intent.Reason}";
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose($"Could not query reflected RSR RDM melee intent: {ex.Message}");
            reason = "RSR RDM melee reflection query failed";
            this.lastRedMageMeleeQuery = $"BROKEN: {reason}: {ex.Message}";
            return false;
        }
    }

    private string GetRedMageMeleeDiagnostics()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextRedMageContractProbe)
        {
            return this.redMageMeleeDiagnostics;
        }

        this.nextRedMageContractProbe = now.Add(ContractProbeInterval);
        if (!this.EnsureResolved())
        {
            this.redMageMeleeDiagnostics = $"Status=unavailable; Reason={this.status}; LastQuery={this.lastRedMageMeleeQuery}";
            return this.redMageMeleeDiagnostics;
        }

        try
        {
            var currentRotation = this.currentRotationProperty!.GetValue(null);
            var currentRotationName = currentRotation?.GetType().FullName ?? "<none>";
            var reborn = BuildRedMageContractStatus(
                RebornRedMageRotationName,
                "Reborn",
                [
                    "ManaStacks",
                    "HasManafication",
                    "CanInstantCast",
                    "CanVerEither",
                    "IsInMeleeCombo",
                    "Pooling",
                    "EnoughManaComboPooling",
                    "EnoughManaComboNoPooling"
                ],
                []);
            var beiruta = BuildRedMageContractStatus(
                BeirutaRedMageRotationName,
                "BeirutaRDM",
                [
                    "ManaStacks",
                    "HasManafication",
                    "_activeMeleeTrack",
                    "HasSwift",
                    "HasDualcast",
                    "IsBurst",
                    "IsOpen",
                    "NearPoolingCap",
                    "Pooling",
                    "NearManaCap",
                    "HasEmbolden",
                    "CanMagickedSwordplay",
                    "EnoughManaComboPooling",
                    "EnoughManaComboNoPooling",
                    "HoldMeleeComboIfOutOfRange",
                    "EnchantedMoulinetPvE"
                ],
                [
                    ("IsLastAoEComboStep", 0),
                    ("IsAnyMeleeComboInProgress", 0),
                    ("InFinisherChain", 0),
                    ("EmboldenRem", 0),
                    ("ShouldGateMeleeStarterAndManafication", 1),
                    ("DesiredMeleeTrackForCurrentState", 0),
                    ("GetTargetAoeCount", 1)
                ]);

            this.redMageMeleeDiagnostics = $"Status={this.status}; CurrentRotation={currentRotationName}; {reborn}; {beiruta}; LastQuery={this.lastRedMageMeleeQuery}";
            return this.redMageMeleeDiagnostics;
        }
        catch (Exception ex)
        {
            this.redMageMeleeDiagnostics = $"BROKEN: RSR RDM contract probe failed: {ex.Message}; LastQuery={this.lastRedMageMeleeQuery}";
            return this.redMageMeleeDiagnostics;
        }
    }

    private RsrRedMageMeleeIntent BuildRebornRedMageIntent(object rotation, Type rotationType)
    {
        var rotationName = FriendlyRotationName(rotationType);
        var manaStacks = ReadByte(GetMemberValue(rotation, rotationType, "ManaStacks"));
        var hasManafication = ReadBool(GetMemberValue(rotation, rotationType, "HasManafication"));

        if (manaStacks >= 3)
        {
            return NoRedMageIntent(rotationName, "RSR Reborn: finisher chain pending");
        }

        if (ReadBool(GetMemberValue(rotation, rotationType, "CanInstantCast")) &&
            !ReadBool(GetMemberValue(rotation, rotationType, "CanVerEither")))
        {
            return NoRedMageIntent(rotationName, "RSR Reborn: instant spell before melee");
        }

        if (ReadBool(GetMemberValue(rotation, rotationType, "IsInMeleeCombo")))
        {
            return CreateRedMageMeleeIntent(
                rotationName,
                RsrRedMageMeleeTrack.SingleTarget,
                manaStacks,
                hasManafication,
                0,
                "RSR Reborn: continuing melee combo");
        }

        var pooling = ReadBool(GetMemberValue(rotation, rotationType, "Pooling"));
        var enoughMana = pooling
            ? ReadBool(GetMemberValue(rotation, rotationType, "EnoughManaComboPooling"))
            : ReadBool(GetMemberValue(rotation, rotationType, "EnoughManaComboNoPooling"));
        if (!enoughMana)
        {
            return NoRedMageIntent(rotationName, "RSR Reborn: melee starter gated by mana");
        }

        return CreateRedMageMeleeIntent(
            rotationName,
            RsrRedMageMeleeTrack.SingleTarget,
            manaStacks,
            hasManafication,
            0,
            "RSR Reborn: melee starter available when in range");
    }

    private RsrRedMageMeleeIntent BuildBeirutaRedMageIntent(object rotation, Type rotationType)
    {
        var rotationName = FriendlyRotationName(rotationType);
        var manaStacks = ReadByte(GetMemberValue(rotation, rotationType, "ManaStacks"));
        var hasManafication = ReadBool(GetMemberValue(rotation, rotationType, "HasManafication"));
        var activeTrack = ReadRedMageMeleeTrack(GetMemberValue(rotation, rotationType, "_activeMeleeTrack"));

        if (activeTrack != RsrRedMageMeleeTrack.None)
        {
            return CreateRedMageMeleeIntent(
                rotationName,
                activeTrack,
                manaStacks,
                hasManafication,
                ReadBeirutaAffectedTargets(rotation, rotationType, activeTrack),
                "RSR BeirutaRDM: active melee combo");
        }

        if (InvokeBool(rotation, rotationType, "IsLastAoEComboStep") || InvokeBool(rotation, rotationType, "IsAnyMeleeComboInProgress") && manaStacks is 1 or 2)
        {
            var inferredTrack = InvokeBool(rotation, rotationType, "IsLastAoEComboStep")
                ? RsrRedMageMeleeTrack.AoE
                : RsrRedMageMeleeTrack.SingleTarget;
            return CreateRedMageMeleeIntent(
                rotationName,
                inferredTrack,
                manaStacks,
                hasManafication,
                ReadBeirutaAffectedTargets(rotation, rotationType, inferredTrack),
                "RSR BeirutaRDM: continuing melee combo");
        }

        if (manaStacks >= 3 || InvokeBool(rotation, rotationType, "InFinisherChain"))
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: finisher chain pending");
        }

        if (ReadBool(GetMemberValue(rotation, rotationType, "HasSwift")) ||
            ReadBool(GetMemberValue(rotation, rotationType, "HasDualcast")))
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: instant spell before melee starter");
        }

        if (!ReadBool(GetMemberValue(rotation, rotationType, "IsBurst")))
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: waiting for burst");
        }

        if (ReadBool(GetMemberValue(rotation, rotationType, "IsOpen")))
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: opener window");
        }

        var emboldenRemaining = InvokeFloat(rotation, rotationType, "EmboldenRem", -1f);
        var gateMelee = InvokeBool(rotation, rotationType, "ShouldGateMeleeStarterAndManafication", emboldenRemaining);
        var nearPoolingCap = ReadBool(GetMemberValue(rotation, rotationType, "NearPoolingCap"));
        if (gateMelee && !nearPoolingCap)
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: pooling before melee starter");
        }

        var pooling = ReadBool(GetMemberValue(rotation, rotationType, "Pooling"));
        var nearManaCap = ReadBool(GetMemberValue(rotation, rotationType, "NearManaCap"));
        var hasEmbolden = ReadBool(GetMemberValue(rotation, rotationType, "HasEmbolden"));
        var canMagickedSwordplay = ReadBool(GetMemberValue(rotation, rotationType, "CanMagickedSwordplay"));
        var enoughPooling = ReadBool(GetMemberValue(rotation, rotationType, "EnoughManaComboPooling"));
        var enoughNoPooling = ReadBool(GetMemberValue(rotation, rotationType, "EnoughManaComboNoPooling"));
        var enoughToStart = pooling
            ? enoughPooling || enoughNoPooling
            : enoughNoPooling || nearManaCap || enoughPooling;
        var burstStartOk = pooling
            ? nearPoolingCap || hasManafication || canMagickedSwordplay || hasEmbolden && canMagickedSwordplay || !gateMelee
            : nearManaCap || hasManafication || canMagickedSwordplay || hasEmbolden && canMagickedSwordplay;

        if (!enoughToStart)
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: melee starter gated by mana");
        }

        if (!burstStartOk)
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: burst starter gate");
        }

        if (!ReadBool(GetMemberValue(rotation, rotationType, "HoldMeleeComboIfOutOfRange")))
        {
            return NoRedMageIntent(rotationName, "RSR BeirutaRDM: out-of-range hold disabled");
        }

        var desiredTrack = ReadRedMageMeleeTrack(InvokeValue(rotation, rotationType, "DesiredMeleeTrackForCurrentState"));
        if (desiredTrack == RsrRedMageMeleeTrack.None)
        {
            desiredTrack = RsrRedMageMeleeTrack.SingleTarget;
        }

        return CreateRedMageMeleeIntent(
            rotationName,
            desiredTrack,
            manaStacks,
            hasManafication,
            ReadBeirutaAffectedTargets(rotation, rotationType, desiredTrack),
            "RSR BeirutaRDM: held melee starter wants range");
    }

    private bool EnsureResolved()
    {
        var now = DateTime.UtcNow;
        if (this.nextGcdActionProperty != null && this.currentRotationProperty != null)
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

            var dataCenter = FindDataCenterType(type.Assembly);
            if (dataCenter == null)
            {
                this.ResetWithStatus("RSR DataCenter type not found");
                return false;
            }

            var currentRotation = dataCenter.GetProperty("CurrentRotation", BindingFlags.Static | BindingFlags.Public);
            if (currentRotation == null)
            {
                this.ResetWithStatus("RSR CurrentRotation property not found");
                return false;
            }

            this.actionUpdaterType = type;
            this.dataCenterType = dataCenter;
            this.nextGcdActionProperty = property;
            this.currentRotationProperty = currentRotation;
            this.defaultGcdRemainProperty = dataCenter.GetProperty("DefaultGCDRemain", BindingFlags.Static | BindingFlags.Public);
            this.defaultGcdElapsedProperty = dataCenter.GetProperty("DefaultGCDElapsed", BindingFlags.Static | BindingFlags.Public);
            this.defaultGcdTotalProperty = dataCenter.GetProperty("DefaultGCDTotal", BindingFlags.Static | BindingFlags.Public);
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

    private static Type? FindDataCenterType(Assembly pluginAssembly)
    {
        var type = pluginAssembly.GetType(DataCenterTypeName, throwOnError: false);
        if (type != null)
        {
            return type;
        }

        foreach (var refName in pluginAssembly.GetReferencedAssemblies())
        {
            if (!string.Equals(refName.Name, "RotationSolver.Basic", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedName = loaded.GetName();
                if (loadedName.Name == refName.Name && loadedName.Version == refName.Version)
                {
                    return loaded.GetType(DataCenterTypeName, throwOnError: false);
                }
            }
        }

        return FindLoadedType(DataCenterTypeName);
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
        this.dataCenterType = null;
        this.nextGcdActionProperty = null;
        this.currentRotationProperty = null;
        this.defaultGcdRemainProperty = null;
        this.defaultGcdElapsedProperty = null;
        this.defaultGcdTotalProperty = null;
        this.status = newStatus;
        this.nextRedMageContractProbe = DateTime.MinValue;
    }

    private static float ReadStaticFloat(PropertyInfo? property, float fallback)
    {
        if (property == null)
        {
            return fallback;
        }

        try
        {
            return ReadFloat(property.GetValue(null), fallback);
        }
        catch
        {
            return fallback;
        }
    }

    private static object? GetPropertyValue(object value, Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    }

    private static object? GetMemberValue(object? instance, Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetMethod != null)
            {
                return property.GetValue(property.GetMethod.IsStatic ? null : instance);
            }

            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(field.IsStatic ? null : instance);
            }
        }

        return null;
    }

    private static MethodInfo? GetMethod(Type type, string name, int parameterCount)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == name && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
        }

        return null;
    }

    private static string BuildRedMageContractStatus(string typeName, string label, IReadOnlyList<string> members, IReadOnlyList<(string Name, int ParameterCount)> methods)
    {
        var type = FindLoadedType(typeName);
        if (type == null)
        {
            return $"{label}=BROKEN missing type {typeName}";
        }

        var missing = new List<string>();
        foreach (var member in members)
        {
            if (!HasFieldOrProperty(type, member))
            {
                missing.Add(member);
            }
        }

        foreach (var method in methods)
        {
            if (GetMethod(type, method.Name, method.ParameterCount) == null)
            {
                missing.Add($"{method.Name}/{method.ParameterCount}");
            }
        }

        return missing.Count == 0
            ? $"{label}=OK"
            : $"{label}=BROKEN missing {string.Join(", ", missing)}";
    }

    private static bool HasFieldOrProperty(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetProperty(name, Flags) != null || current.GetField(name, Flags) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static object? InvokeValue(object instance, Type type, string name, params object[] args)
    {
        var method = GetMethod(type, name, args.Length);
        return method?.Invoke(method.IsStatic ? null : instance, args);
    }

    private static bool InvokeBool(object instance, Type type, string name, params object[] args)
    {
        var value = InvokeValue(instance, type, name, args);
        return value != null && ReadBool(value);
    }

    private static float InvokeFloat(object instance, Type type, string name, float fallback, params object[] args)
    {
        var value = InvokeValue(instance, type, name, args);
        return value == null ? fallback : ReadFloat(value, fallback);
    }

    private static string FriendlyRotationName(Type rotationType)
    {
        return rotationType.FullName switch
        {
            RebornRedMageRotationName => "Reborn",
            BeirutaRedMageRotationName => "BeirutaRDM",
            _ => rotationType.Name
        };
    }

    private static RsrRedMageMeleeIntent NoRedMageIntent(string rotationName, string reason)
    {
        return new(rotationName, RsrRedMageMeleeTrack.None, 0, "<none>", 0, reason, true);
    }

    private static RsrRedMageMeleeIntent CreateRedMageMeleeIntent(
        string rotationName,
        RsrRedMageMeleeTrack track,
        byte manaStacks,
        bool hasManafication,
        int affectedTargets,
        string reason)
    {
        var actionId = ResolveRedMageMeleeActionId(track, manaStacks, hasManafication);
        return new(
            rotationName,
            track,
            actionId,
            GetRedMageMeleeActionName(actionId),
            affectedTargets,
            reason,
            true);
    }

    private static RsrRedMageMeleeTrack ReadRedMageMeleeTrack(object? value)
    {
        return value?.ToString() switch
        {
            "SingleTarget" => RsrRedMageMeleeTrack.SingleTarget,
            "AoE" => RsrRedMageMeleeTrack.AoE,
            _ => RsrRedMageMeleeTrack.None
        };
    }

    private static int ReadBeirutaAffectedTargets(object rotation, Type rotationType, RsrRedMageMeleeTrack track)
    {
        if (track != RsrRedMageMeleeTrack.AoE)
        {
            return 1;
        }

        var moulinet = GetMemberValue(rotation, rotationType, "EnchantedMoulinetPvE");
        if (moulinet == null)
        {
            return 0;
        }

        var value = InvokeValue(rotation, rotationType, "GetTargetAoeCount", moulinet);
        return value == null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static uint ResolveRedMageMeleeActionId(RsrRedMageMeleeTrack track, byte manaStacks, bool hasManafication)
    {
        if (track == RsrRedMageMeleeTrack.AoE)
        {
            return manaStacks switch
            {
                1 => ActionUse.RedMageEnchantedMoulinetDeuxActionId,
                2 => ActionUse.RedMageEnchantedMoulinetTroisActionId,
                _ => ActionUse.RedMageEnchantedMoulinetActionId
            };
        }

        return manaStacks switch
        {
            1 => hasManafication ? ActionUse.RedMageManaficationZwerchhauActionId : ActionUse.RedMageEnchantedZwerchhauActionId,
            2 => hasManafication ? ActionUse.RedMageManaficationRedoublementActionId : ActionUse.RedMageEnchantedRedoublementActionId,
            _ => hasManafication ? ActionUse.RedMageManaficationRiposteActionId : ActionUse.RedMageEnchantedRiposteActionId
        };
    }

    private static string GetRedMageMeleeActionName(uint actionId)
    {
        return actionId switch
        {
            ActionUse.RedMageEnchantedRiposteActionId or ActionUse.RedMageManaficationRiposteActionId => "Enchanted Riposte",
            ActionUse.RedMageEnchantedZwerchhauActionId or ActionUse.RedMageManaficationZwerchhauActionId => "Enchanted Zwerchhau",
            ActionUse.RedMageEnchantedRedoublementActionId or ActionUse.RedMageManaficationRedoublementActionId => "Enchanted Redoublement",
            ActionUse.RedMageEnchantedMoulinetActionId => "Enchanted Moulinet",
            ActionUse.RedMageEnchantedMoulinetDeuxActionId => "Enchanted Moulinet Deux",
            ActionUse.RedMageEnchantedMoulinetTroisActionId => "Enchanted Moulinet Trois",
            _ => actionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
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

    private static byte ReadByte(object? value)
    {
        return value == null ? (byte)0 : Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture);
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
