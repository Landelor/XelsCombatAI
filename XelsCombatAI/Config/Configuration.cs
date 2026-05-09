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
    public const float DefaultMinimumReengageGapCloserDistance = 8f;
    public const float DefaultMinimumEscapeGapCloserDistance = 8f;
    public const float MinimumGapCloserDistanceMin = 0f;
    public const float MinimumGapCloserDistanceMax = 20f;

    public int Version { get; set; } = 13;

    public bool Enabled { get; set; } = false;
    public bool ManageMovement { get; set; } = true;
    public bool RespectManualMovement { get; set; } = true;
    public bool ManageTargetUptime { get; set; } = true;
    public bool ManageForbiddenZoneDistance { get; set; } = true;
    public bool ManagePositionals { get; set; } = true;
    public bool ManageTrueNorth { get; set; } = false;
    public bool ManageLeylines { get; set; } = true;
    public bool UseBetweenTheLines { get; set; } = true;
    public bool UseRetrace { get; set; } = true;
    public bool ReturnToLeylines { get; set; } = true;
    public bool UseGapCloser { get; set; } = false;
    public bool GapCloserPLD { get; set; } = true;
    public bool GapCloserWAR { get; set; } = true;
    public bool GapCloserDRK { get; set; } = true;
    public bool GapCloserGNB { get; set; } = true;
    public bool GapCloserMNK { get; set; } = true;
    public bool GapCloserDRG { get; set; } = true;
    public bool GapCloserNIN { get; set; } = true;
    public bool GapCloserSAM { get; set; } = true;
    public bool GapCloserDNC { get; set; } = true;
    public bool GapCloserRPR { get; set; } = true;
    public bool GapCloserVPR { get; set; } = true;
    public bool GapCloserWHM { get; set; } = true;
    public bool UseEscapeGapCloser { get; set; } = false;
    public bool EscapeGapCloserMNK { get; set; } = true;
    public bool EscapeGapCloserNIN { get; set; } = true;
    public bool EscapeGapCloserRPR { get; set; } = true;
    public bool EscapeGapCloserVPR { get; set; } = true;
    public bool EscapeGapCloserWHM { get; set; } = true;
    public bool EscapeGapCloserBLM { get; set; } = true;
    public bool EscapeGapCloserSGE { get; set; } = true;
    public bool EscapeGapCloserDNC { get; set; } = true;
    public bool EscapeGapCloserPCT { get; set; } = true;
    public bool EchoStatusToChat { get; set; } = true;
    public CombatStyle CombatStyle { get; set; } = CombatStyle.Normal;
    public float PreferredForbiddenZoneDistance { get; set; } = DefaultPreferredForbiddenZoneDistance;
    public float MinimumReengageGapCloserDistance { get; set; } = DefaultMinimumReengageGapCloserDistance;
    public float MinimumEscapeGapCloserDistance { get; set; } = DefaultMinimumEscapeGapCloserDistance;
    public bool ManageAoePackPositioning { get; set; } = true;
    public bool ManageHealerCoverageZone { get; set; } = true;
    public bool ManageDefensiveGroundZonePositioning { get; set; } = true;
    public bool ManagePassageOfArmsPositioning { get; set; } = true;
    public bool PickBetterAoeTarget { get; set; } = false;
    public bool KeepTrashTargetSelected { get; set; } = true;
    public bool ManageAggroSafetyMovement { get; set; } = true;
    public bool AvoidStandingInsideEnemies { get; set; } = true;
    public bool AvoidArenaEdge { get; set; } = true;
    public bool AvoidBossFrontalCone { get; set; } = true;
    public bool ShowDecisionOverlay { get; set; } = false;
    public bool ShowDecisionOverlayHud { get; set; } = false;

    private bool? manageSurvivabilityZonePositioningCompatibility;
    private bool? manageMultiTargetTargetingCompatibility;
    private bool? aoePackPositioningControlRsrTargetCompatibility;
    private bool? manageTrueNorthInRsrCompatibility;
    private bool? manageHealerAoePositioningCompatibility;

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
            this.UseEscapeGapCloser = false;
            this.EscapeGapCloserMNK = true;
            this.EscapeGapCloserNIN = true;
            this.EscapeGapCloserRPR = true;
            this.EscapeGapCloserVPR = true;
            this.EscapeGapCloserWHM = true;
            this.EscapeGapCloserBLM = true;
            this.EscapeGapCloserSGE = true;
            this.EscapeGapCloserPCT = true;
            this.EscapeGapCloserDNC = true;
            this.MinimumReengageGapCloserDistance = DefaultMinimumReengageGapCloserDistance;
            this.MinimumEscapeGapCloserDistance = DefaultMinimumEscapeGapCloserDistance;
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
            this.AvoidBossFrontalCone = true;
            this.ShowDecisionOverlay = false;
            this.ShowDecisionOverlayHud = false;
            this.ApplyCompatibilityValues();
            this.Version = 13;
        }
    }

    internal void Clamp()
    {
        this.PreferredForbiddenZoneDistance = Math.Clamp(this.PreferredForbiddenZoneDistance, 0f, 3f);
        this.MinimumReengageGapCloserDistance = MathF.Round(Math.Clamp(this.MinimumReengageGapCloserDistance, MinimumGapCloserDistanceMin, MinimumGapCloserDistanceMax));
        this.MinimumEscapeGapCloserDistance = MathF.Round(Math.Clamp(this.MinimumEscapeGapCloserDistance, MinimumGapCloserDistanceMin, MinimumGapCloserDistanceMax));
        this.CombatStyle = Enum.IsDefined(this.CombatStyle) ? this.CombatStyle : CombatStyle.Normal;
    }

    internal void ResetBehaviorSettings()
    {
        this.CombatStyle = CombatStyle.Normal;
        this.ManageTargetUptime = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManageAggroSafetyMovement = true;
        this.PreferredForbiddenZoneDistance = DefaultPreferredForbiddenZoneDistance;
        this.MinimumReengageGapCloserDistance = DefaultMinimumReengageGapCloserDistance;
        this.MinimumEscapeGapCloserDistance = DefaultMinimumEscapeGapCloserDistance;
    }

    internal void ResetAll()
    {
        this.Enabled = false;
        this.ManageMovement = true;
        this.RespectManualMovement = true;
        this.ManageTargetUptime = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManagePositionals = true;
        this.ManageTrueNorth = false;
        this.ManageLeylines = true;
        this.UseBetweenTheLines = true;
        this.UseRetrace = true;
        this.ReturnToLeylines = true;
        this.UseGapCloser = false;
        this.GapCloserPLD = true;
        this.GapCloserWAR = true;
        this.GapCloserDRK = true;
        this.GapCloserGNB = true;
        this.GapCloserMNK = true;
        this.GapCloserDRG = true;
        this.GapCloserNIN = true;
        this.GapCloserSAM = true;
        this.GapCloserDNC = true;
        this.GapCloserRPR = true;
        this.GapCloserVPR = true;
        this.GapCloserWHM = true;
        this.UseEscapeGapCloser = false;
        this.EscapeGapCloserMNK = true;
        this.EscapeGapCloserNIN = true;
        this.EscapeGapCloserRPR = true;
        this.EscapeGapCloserVPR = true;
        this.EscapeGapCloserWHM = true;
        this.EscapeGapCloserBLM = true;
        this.EscapeGapCloserSGE = true;
        this.EscapeGapCloserDNC = true;
        this.EscapeGapCloserPCT = true;
        this.EchoStatusToChat = true;
        this.CombatStyle = CombatStyle.Normal;
        this.ManageAoePackPositioning = true;
        this.ManageHealerCoverageZone = true;
        this.ManageDefensiveGroundZonePositioning = true;
        this.ManagePassageOfArmsPositioning = true;
        this.PickBetterAoeTarget = false;
        this.KeepTrashTargetSelected = true;
        this.ManageAggroSafetyMovement = true;
        this.AvoidStandingInsideEnemies = true;
        this.AvoidArenaEdge = true;
        this.AvoidBossFrontalCone = true;
        this.ShowDecisionOverlay = false;
        this.ShowDecisionOverlayHud = false;
        this.ResetBehaviorSettings();
    }

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
