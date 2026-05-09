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

    public int Version { get; set; } = 14;

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
    public bool UseEscapeGapCloser { get; set; } = false;
    public bool EscapeGapCloserMNK { get; set; } = true;
    public bool EscapeGapCloserNIN { get; set; } = true;
    public bool EscapeGapCloserRPR { get; set; } = true;
    public bool EscapeGapCloserVPR { get; set; } = true;
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
    public bool ManagePartyGravityPositioning { get; set; } = true;
    public bool ManageHealerAoePositioning { get; set; } = true;
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

    [JsonProperty("ManageSurvivabilityZonePositioning")]
    private bool ManageSurvivabilityZonePositioningCompatibility
    {
        set
        {
            this.ManageDefensiveGroundZonePositioning = value;
            this.ManagePassageOfArmsPositioning = value;
        }
    }

    [JsonProperty("ManageMultiTargetTargeting")]
    private bool ManageMultiTargetTargetingCompatibility
    {
        set => this.KeepTrashTargetSelected = value;
    }

    [JsonProperty("AoePackPositioningControlRsrTarget")]
    private bool AoePackPositioningControlRsrTargetCompatibility
    {
        set => this.PickBetterAoeTarget = value;
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
        set => this.ManageTrueNorth = value;
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

    internal void Migrate()
    {
        if (this.Version < 2)
        {
            this.Version = 2;
        }

        if (this.Version < 3)
        {
            this.Version = 3;
        }

        if (this.Version < 4)
        {
            this.ManageTrueNorth = false;
            this.Version = 4;
        }

        if (this.Version < 5)
        {
            this.GapCloserSAM = false;
            this.Version = 5;
        }

        if (this.Version < 6)
        {
            this.GapCloserPLD = true;
            this.GapCloserWAR = true;
            this.GapCloserDRK = true;
            this.GapCloserGNB = true;
            this.GapCloserSAM = true;
            this.GapCloserRPR = true;
            this.Version = 6;
        }

        if (this.Version < 7)
        {
            this.UseEscapeGapCloser = false;
            this.EscapeGapCloserMNK = true;
            this.EscapeGapCloserNIN = true;
            this.EscapeGapCloserRPR = true;
            this.EscapeGapCloserVPR = true;
            this.Version = 7;
        }

        if (this.Version < 8)
        {
            this.EscapeGapCloserBLM = true;
            this.EscapeGapCloserSGE = true;
            this.EscapeGapCloserPCT = true;
            this.Version = 8;
        }

        if (this.Version < 9)
        {
            this.GapCloserDNC = true;
            this.EscapeGapCloserDNC = true;
            this.Version = 9;
        }

        if (this.Version < 10)
        {
            this.Version = 10;
        }

        if (this.Version < 11)
        {
            this.MinimumReengageGapCloserDistance = DefaultMinimumReengageGapCloserDistance;
            this.MinimumEscapeGapCloserDistance = DefaultMinimumEscapeGapCloserDistance;
            this.Version = 11;
        }

        if (this.Version < 12)
        {
            this.RespectManualMovement = true;
            this.Version = 12;
        }

        if (this.Version < 13)
        {
            this.ManageAoePackPositioning = true;
            this.ManagePartyGravityPositioning = true;
            this.ManageHealerAoePositioning = true;
            this.ManageDefensiveGroundZonePositioning = true;
            this.ManagePassageOfArmsPositioning = true;
            this.PickBetterAoeTarget = false;
            this.KeepTrashTargetSelected = true;
            this.AvoidStandingInsideEnemies = true;
            this.AvoidArenaEdge = true;
            this.ShowDecisionOverlay = false;
            this.ShowDecisionOverlayHud = false;
            this.Version = 13;
        }

        if (this.Version < 14)
        {
            this.AvoidBossFrontalCone = true;
            this.Version = 14;
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
        this.UseEscapeGapCloser = false;
        this.EscapeGapCloserMNK = true;
        this.EscapeGapCloserNIN = true;
        this.EscapeGapCloserRPR = true;
        this.EscapeGapCloserVPR = true;
        this.EscapeGapCloserBLM = true;
        this.EscapeGapCloserSGE = true;
        this.EscapeGapCloserDNC = true;
        this.EscapeGapCloserPCT = true;
        this.EchoStatusToChat = true;
        this.CombatStyle = CombatStyle.Normal;
        this.ManageAoePackPositioning = true;
        this.ManagePartyGravityPositioning = true;
        this.ManageHealerAoePositioning = true;
        this.ManageDefensiveGroundZonePositioning = true;
        this.ManagePassageOfArmsPositioning = true;
        this.PickBetterAoeTarget = false;
        this.KeepTrashTargetSelected = true;
        this.ManageAggroSafetyMovement = true;
        this.AvoidStandingInsideEnemies = true;
        this.AvoidArenaEdge = true;
        this.ShowDecisionOverlay = false;
        this.ShowDecisionOverlayHud = false;
        this.ResetBehaviorSettings();
    }

    internal void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
