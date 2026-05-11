using System;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace XelsCombatAI.Config;

public sealed class Configuration : IPluginConfiguration
{
    public const float InternalMeleeUptimeRange = 2.6f;
    public const float InternalRangedUptimeRange = 25f;
    public const float InternalDisabledUptimeRange = 30f;
    public const float DefaultPreferredForbiddenZoneDistance = 1f;
    public const float DefaultMinimumGapCloserDistance = 8f;
    public const float MinimumGapCloserDistanceMin = 0f;
    public const float MinimumGapCloserDistanceMax = 20f;

    public int Version { get; set; } = 17;

    public bool Enabled { get; set; } = false;
    public bool ManageMovement { get; set; } = true;
    public bool RespectManualMovement { get; set; } = true;
    public bool ManageSocialTurning { get; set; } = true;
    public bool ManageTargetUptime { get; set; } = true;
    public bool ManageForbiddenZoneDistance { get; set; } = true;
    public bool ManagePositionals { get; set; } = true;
    public bool ManageTrueNorth { get; set; } = false;
    public bool ManageLeylines { get; set; } = true;
    public bool UseBetweenTheLines { get; set; } = true;
    public bool UseRetrace { get; set; } = true;
    public bool ReturnToLeylines { get; set; } = true;
    public bool UseRedMageMeleeComboMovement { get; set; } = false;
    public bool UseGapCloser { get; set; } = false;
    public bool GapCloserPLD { get; set; } = true;
    public bool GapCloserWAR { get; set; } = true;
    public bool GapCloserDRK { get; set; } = true;
    public bool GapCloserGNB { get; set; } = true;
    public bool GapCloserMNK { get; set; } = true;
    public bool GapCloserDRG { get; set; } = true;
    public bool GapCloserNIN { get; set; } = true;
    public bool GapCloserSAM { get; set; } = true;
    public bool GapCloserBRD { get; set; } = true;
    public bool GapCloserDNC { get; set; } = true;
    public bool GapCloserRPR { get; set; } = true;
    public bool GapCloserVPR { get; set; } = true;
    public bool GapCloserWHM { get; set; } = true;
    public bool GapCloserBLM { get; set; } = true;
    public bool GapCloserRDM { get; set; } = true;
    public bool GapCloserSGE { get; set; } = true;
    public bool GapCloserPCT { get; set; } = true;
    public bool EchoStatusToChat { get; set; } = true;
    public CombatStyle CombatStyle { get; set; } = CombatStyle.Normal;
    public float PreferredForbiddenZoneDistance { get; set; } = DefaultPreferredForbiddenZoneDistance;
    public float MinimumGapCloserDistance { get; set; } = DefaultMinimumGapCloserDistance;
    public bool ManageAoePackPositioning { get; set; } = true;
    public bool LeadTrashPullsWithTank { get; set; } = true;
    public bool ManageHealerCoverageZone { get; set; } = true;
    public bool ManageDefensiveGroundZonePositioning { get; set; } = true;
    public bool ManagePassageOfArmsPositioning { get; set; } = true;
    public bool PickBetterAoeTarget { get; set; } = false;
    public bool KeepTrashTargetSelected { get; set; } = true;
    public bool ManageAggroSafetyMovement { get; set; } = true;
    public bool GuardUnknownBossNavigationWithVnavmesh { get; set; } = true;
    public bool AvoidStandingInsideEnemies { get; set; } = true;
    public bool AvoidArenaEdge { get; set; } = true;
    public bool ShowDecisionOverlay { get; set; } = false;
    public bool ShowDecisionOverlayHud { get; set; } = false;
    public bool FightReviewLoggingEnabled { get; set; } = false;

    private bool? manageSurvivabilityZonePositioningCompatibility;
    private bool? manageMultiTargetTargetingCompatibility;
    private bool? aoePackPositioningControlRsrTargetCompatibility;
    private bool? manageTrueNorthInRsrCompatibility;
    private bool? manageHealerAoePositioningCompatibility;
    private bool? useEscapeGapCloserCompatibility;
    private bool? escapeGapCloserMNKCompatibility;
    private bool? escapeGapCloserDRGCompatibility;
    private bool? escapeGapCloserNINCompatibility;
    private bool? escapeGapCloserSAMCompatibility;
    private bool? escapeGapCloserBRDCompatibility;
    private bool? escapeGapCloserDNCCompatibility;
    private bool? escapeGapCloserRPRCompatibility;
    private bool? escapeGapCloserVPRCompatibility;
    private bool? escapeGapCloserWHMCompatibility;
    private bool? escapeGapCloserBLMCompatibility;
    private bool? escapeGapCloserRDMCompatibility;
    private bool? escapeGapCloserSGECompatibility;
    private bool? escapeGapCloserPCTCompatibility;
    private float? minimumReengageGapCloserDistanceCompatibility;
    private float? minimumEscapeGapCloserDistanceCompatibility;

    [JsonProperty("ManageSurvivabilityZonePositioning")]
    private bool ManageSurvivabilityZonePositioningCompatibility
    {
        set
        {
            this.manageSurvivabilityZonePositioningCompatibility = value;
            this.ManageDefensiveGroundZonePositioning = value;
            this.ManagePassageOfArmsPositioning = value;
        }
    }

    [JsonProperty("ManageMultiTargetTargeting")]
    private bool ManageMultiTargetTargetingCompatibility
    {
        set
        {
            this.manageMultiTargetTargetingCompatibility = value;
            this.KeepTrashTargetSelected = value;
        }
    }

    [JsonProperty("AoePackPositioningControlRsrTarget")]
    private bool AoePackPositioningControlRsrTargetCompatibility
    {
        set
        {
            this.aoePackPositioningControlRsrTargetCompatibility = value;
            this.PickBetterAoeTarget = value;
        }
    }

    [JsonProperty("MoveCloserToTrashPacks")]
    private bool MoveCloserToTrashPacksCompatibility
    {
        set => this.ManageTargetUptime |= value;
    }

    [JsonProperty("AoePackPositioningAoeCombatControl")]
    private bool AoePackPositioningAoeCombatControlCompatibility
    {
        set
        {
            if (!value)
                return;

            this.PickBetterAoeTarget = true;
            this.KeepTrashTargetSelected = true;
            this.ManageTargetUptime = true;
        }
    }

    [JsonProperty("ManageTrueNorthInRsr")]
    private bool ManageTrueNorthInRsrCompatibility
    {
        set
        {
            this.manageTrueNorthInRsrCompatibility = value;
            this.ManageTrueNorth = value;
        }
    }

    [JsonProperty("ManageRange")]
    private bool ManageRangeCompatibility
    {
        set => this.ManageTargetUptime = value;
    }

    [JsonProperty("HealerPartyCoverage")]
    private bool HealerPartyCoverageCompatibility
    {
        set { }
    }

    [JsonProperty("ManageHealerAoePositioning")]
    private bool ManageHealerAoePositioningCompatibility
    {
        set
        {
            this.manageHealerAoePositioningCompatibility = value;
            this.ManageHealerCoverageZone = value;
        }
    }

    [JsonProperty("UseEscapeGapCloser")]
    private bool UseEscapeGapCloserCompatibility
    {
        set => this.useEscapeGapCloserCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserMNK")]
    private bool EscapeGapCloserMNKCompatibility
    {
        set => this.escapeGapCloserMNKCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserDRG")]
    private bool EscapeGapCloserDRGCompatibility
    {
        set => this.escapeGapCloserDRGCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserNIN")]
    private bool EscapeGapCloserNINCompatibility
    {
        set => this.escapeGapCloserNINCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserSAM")]
    private bool EscapeGapCloserSAMCompatibility
    {
        set => this.escapeGapCloserSAMCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserBRD")]
    private bool EscapeGapCloserBRDCompatibility
    {
        set => this.escapeGapCloserBRDCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserDNC")]
    private bool EscapeGapCloserDNCCompatibility
    {
        set => this.escapeGapCloserDNCCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserRPR")]
    private bool EscapeGapCloserRPRCompatibility
    {
        set => this.escapeGapCloserRPRCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserVPR")]
    private bool EscapeGapCloserVPRCompatibility
    {
        set => this.escapeGapCloserVPRCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserWHM")]
    private bool EscapeGapCloserWHMCompatibility
    {
        set => this.escapeGapCloserWHMCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserBLM")]
    private bool EscapeGapCloserBLMCompatibility
    {
        set => this.escapeGapCloserBLMCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserRDM")]
    private bool EscapeGapCloserRDMCompatibility
    {
        set => this.escapeGapCloserRDMCompatibility = value;
    }

    [JsonProperty("EscapeGapCloserSGE")]
    private bool EscapeGapCloserSGECompatibility
    {
        set => this.escapeGapCloserSGECompatibility = value;
    }

    [JsonProperty("EscapeGapCloserPCT")]
    private bool EscapeGapCloserPCTCompatibility
    {
        set => this.escapeGapCloserPCTCompatibility = value;
    }

    [JsonProperty("MinimumReengageGapCloserDistance")]
    private float MinimumReengageGapCloserDistanceCompatibility
    {
        set => this.minimumReengageGapCloserDistanceCompatibility = value;
    }

    [JsonProperty("MinimumEscapeGapCloserDistance")]
    private float MinimumEscapeGapCloserDistanceCompatibility
    {
        set => this.minimumEscapeGapCloserDistanceCompatibility = value;
    }

    internal void Migrate()
    {
        if (this.Version < 13)
        {
            this.ManageTrueNorth = false;
            this.GapCloserSAM = true;
            this.GapCloserPLD = true;
            this.GapCloserWAR = true;
            this.GapCloserDRK = true;
            this.GapCloserGNB = true;
            this.GapCloserRPR = true;
            this.GapCloserDNC = true;
            this.GapCloserWHM = true;
            this.MinimumGapCloserDistance = DefaultMinimumGapCloserDistance;
            this.RespectManualMovement = true;
            this.ManageAoePackPositioning = true;
            this.ManageHealerCoverageZone = true;
            this.ManageDefensiveGroundZonePositioning = true;
            this.ManagePassageOfArmsPositioning = true;
            this.PickBetterAoeTarget = false;
            this.KeepTrashTargetSelected = true;
            this.ManageAggroSafetyMovement = true;
            this.AvoidStandingInsideEnemies = true;
            this.AvoidArenaEdge = true;
            this.ShowDecisionOverlay = false;
            this.ShowDecisionOverlayHud = false;
            this.ApplyCompatibilityValues();
            this.Version = 13;
        }

        if (this.Version < 14)
        {
            this.GuardUnknownBossNavigationWithVnavmesh = true;
            this.Version = 14;
        }

        if (this.Version < 15)
        {
            this.Version = 15;
        }

        if (this.Version < 16)
        {
            this.Version = 16;
        }

        if (this.Version < 17)
        {
            this.FightReviewLoggingEnabled = false;
            this.LeadTrashPullsWithTank = true;
            this.ManageSocialTurning = true;
            this.UseRedMageMeleeComboMovement = false;
            this.MigrateUnifiedGapCloserSettings();
            this.Version = 17;
        }
    }

    internal void Clamp()
    {
        this.PreferredForbiddenZoneDistance = Math.Clamp(this.PreferredForbiddenZoneDistance, 0f, 3f);
        this.MinimumGapCloserDistance = MathF.Round(Math.Clamp(this.MinimumGapCloserDistance, MinimumGapCloserDistanceMin, MinimumGapCloserDistanceMax));
        this.CombatStyle = Enum.IsDefined(this.CombatStyle) ? this.CombatStyle : CombatStyle.Normal;
    }

    internal void ResetBehaviorSettings()
    {
        this.CombatStyle = CombatStyle.Normal;
        this.ManageSocialTurning = true;
        this.ManageTargetUptime = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManageAggroSafetyMovement = true;
        this.LeadTrashPullsWithTank = true;
        this.GuardUnknownBossNavigationWithVnavmesh = true;
        this.PreferredForbiddenZoneDistance = DefaultPreferredForbiddenZoneDistance;
        this.MinimumGapCloserDistance = DefaultMinimumGapCloserDistance;
        this.UseRedMageMeleeComboMovement = false;
    }

    internal void ResetAll()
    {
        this.Enabled = false;
        this.ManageMovement = true;
        this.RespectManualMovement = true;
        this.ManageSocialTurning = true;
        this.ManageTargetUptime = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManagePositionals = true;
        this.ManageTrueNorth = false;
        this.ManageLeylines = true;
        this.UseBetweenTheLines = true;
        this.UseRetrace = true;
        this.ReturnToLeylines = true;
        this.UseRedMageMeleeComboMovement = false;
        this.UseGapCloser = false;
        this.GapCloserPLD = true;
        this.GapCloserWAR = true;
        this.GapCloserDRK = true;
        this.GapCloserGNB = true;
        this.GapCloserMNK = true;
        this.GapCloserDRG = true;
        this.GapCloserNIN = true;
        this.GapCloserSAM = true;
        this.GapCloserBRD = true;
        this.GapCloserDNC = true;
        this.GapCloserRPR = true;
        this.GapCloserVPR = true;
        this.GapCloserWHM = true;
        this.GapCloserBLM = true;
        this.GapCloserRDM = true;
        this.GapCloserSGE = true;
        this.GapCloserPCT = true;
        this.EchoStatusToChat = true;
        this.CombatStyle = CombatStyle.Normal;
        this.ManageAoePackPositioning = true;
        this.LeadTrashPullsWithTank = true;
        this.ManageHealerCoverageZone = true;
        this.ManageDefensiveGroundZonePositioning = true;
        this.ManagePassageOfArmsPositioning = true;
        this.PickBetterAoeTarget = false;
        this.KeepTrashTargetSelected = true;
        this.ManageAggroSafetyMovement = true;
        this.GuardUnknownBossNavigationWithVnavmesh = true;
        this.AvoidStandingInsideEnemies = true;
        this.AvoidArenaEdge = true;
        this.ShowDecisionOverlay = false;
        this.ShowDecisionOverlayHud = false;
        this.FightReviewLoggingEnabled = false;
        this.ResetBehaviorSettings();
    }

    private void MigrateUnifiedGapCloserSettings()
    {
        var oldReengageEnabled = this.UseGapCloser;
        var oldEscapeEnabled = this.useEscapeGapCloserCompatibility ?? false;

        if (oldEscapeEnabled && !oldReengageEnabled)
        {
            this.GapCloserPLD = false;
            this.GapCloserWAR = false;
            this.GapCloserDRK = false;
            this.GapCloserGNB = false;
            this.ApplyEscapeGapCloserJobs(replace: true);
        }
        else if (oldEscapeEnabled)
        {
            this.ApplyEscapeGapCloserJobs(replace: false);
        }
        else if (oldReengageEnabled)
        {
            this.GapCloserBRD = false;
            this.GapCloserBLM = false;
            this.GapCloserRDM = false;
            this.GapCloserSGE = false;
            this.GapCloserPCT = false;
        }

        this.MigrateUnifiedGapCloserDistance(oldReengageEnabled, oldEscapeEnabled);
        this.UseGapCloser = oldReengageEnabled || oldEscapeEnabled;
    }

    private void MigrateUnifiedGapCloserDistance(bool oldReengageEnabled, bool oldEscapeEnabled)
    {
        var oldReengageDistance = this.minimumReengageGapCloserDistanceCompatibility ?? this.MinimumGapCloserDistance;
        var oldEscapeDistance = this.minimumEscapeGapCloserDistanceCompatibility ?? this.MinimumGapCloserDistance;
        this.MinimumGapCloserDistance = (oldReengageEnabled, oldEscapeEnabled) switch
        {
            (true, false) => oldReengageDistance,
            (false, true) => oldEscapeDistance,
            (true, true) => MathF.Max(oldReengageDistance, oldEscapeDistance),
            _ => MathF.Max(oldReengageDistance, oldEscapeDistance)
        };
    }

    private void ApplyEscapeGapCloserJobs(bool replace)
    {
        if (replace)
        {
            this.GapCloserMNK = LegacyEscapeGapCloserEnabled(this.escapeGapCloserMNKCompatibility);
            this.GapCloserDRG = LegacyEscapeGapCloserEnabled(this.escapeGapCloserDRGCompatibility);
            this.GapCloserNIN = LegacyEscapeGapCloserEnabled(this.escapeGapCloserNINCompatibility);
            this.GapCloserSAM = LegacyEscapeGapCloserEnabled(this.escapeGapCloserSAMCompatibility);
            this.GapCloserBRD = LegacyEscapeGapCloserEnabled(this.escapeGapCloserBRDCompatibility);
            this.GapCloserDNC = LegacyEscapeGapCloserEnabled(this.escapeGapCloserDNCCompatibility);
            this.GapCloserRPR = LegacyEscapeGapCloserEnabled(this.escapeGapCloserRPRCompatibility);
            this.GapCloserVPR = LegacyEscapeGapCloserEnabled(this.escapeGapCloserVPRCompatibility);
            this.GapCloserWHM = LegacyEscapeGapCloserEnabled(this.escapeGapCloserWHMCompatibility);
            this.GapCloserBLM = LegacyEscapeGapCloserEnabled(this.escapeGapCloserBLMCompatibility);
            this.GapCloserRDM = LegacyEscapeGapCloserEnabled(this.escapeGapCloserRDMCompatibility);
            this.GapCloserSGE = LegacyEscapeGapCloserEnabled(this.escapeGapCloserSGECompatibility);
            this.GapCloserPCT = LegacyEscapeGapCloserEnabled(this.escapeGapCloserPCTCompatibility);
            return;
        }

        this.GapCloserMNK |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserMNKCompatibility);
        this.GapCloserDRG |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserDRGCompatibility);
        this.GapCloserNIN |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserNINCompatibility);
        this.GapCloserSAM |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserSAMCompatibility);
        this.GapCloserBRD |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserBRDCompatibility);
        this.GapCloserDNC |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserDNCCompatibility);
        this.GapCloserRPR |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserRPRCompatibility);
        this.GapCloserVPR |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserVPRCompatibility);
        this.GapCloserWHM |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserWHMCompatibility);
        this.GapCloserBLM |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserBLMCompatibility);
        this.GapCloserRDM |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserRDMCompatibility);
        this.GapCloserSGE |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserSGECompatibility);
        this.GapCloserPCT |= LegacyEscapeGapCloserEnabled(this.escapeGapCloserPCTCompatibility);
    }

    private static bool LegacyEscapeGapCloserEnabled(bool? value) => value ?? true;

    private void ApplyCompatibilityValues()
    {
        if (this.manageSurvivabilityZonePositioningCompatibility.HasValue)
        {
            this.ManageDefensiveGroundZonePositioning = this.manageSurvivabilityZonePositioningCompatibility.Value;
            this.ManagePassageOfArmsPositioning = this.manageSurvivabilityZonePositioningCompatibility.Value;
        }

        if (this.manageMultiTargetTargetingCompatibility.HasValue)
        {
            this.KeepTrashTargetSelected = this.manageMultiTargetTargetingCompatibility.Value;
        }

        if (this.aoePackPositioningControlRsrTargetCompatibility.HasValue)
        {
            this.PickBetterAoeTarget = this.aoePackPositioningControlRsrTargetCompatibility.Value;
        }

        if (this.manageTrueNorthInRsrCompatibility.HasValue)
        {
            this.ManageTrueNorth = this.manageTrueNorthInRsrCompatibility.Value;
        }

        if (this.manageHealerAoePositioningCompatibility.HasValue)
        {
            this.ManageHealerCoverageZone = this.manageHealerAoePositioningCompatibility.Value;
        }
    }

    internal void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
