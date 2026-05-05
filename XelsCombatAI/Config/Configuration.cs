using System;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace XelsCombatAI.Config;

public sealed class Configuration : IPluginConfiguration
{
    public const float BossModMinRange = 1.1f;
    public const float BossModMaxRange = 30f;
    public const float DefaultMeleeRange = 2.6f;
    public const float DefaultRangedRange = 15f;
    public const float DefaultPhysicalRangedRange = 15f;
    public const float DefaultHealerRange = 15f;
    public const float DefaultMagicRangedRange = 20f;
    public const float DefaultAoEMeleeRange = 2.6f;
    public const float DefaultAoERangedRange = 15f;
    public const float DefaultAoEPhysicalRangedRange = 15f;
    public const float DefaultAoEHealerRange = 15f;
    public const float DefaultAoEMagicRangedRange = 20f;
    public const int DefaultAoEEnemyThreshold = 2;
    public const float DefaultPreferredForbiddenZoneDistance = 1f;
    public const float EnemyCountRadius = 10f;

    public int Version { get; set; } = 10;

    public bool Enabled { get; set; } = false;
    public bool ManageMovement { get; set; } = true;
    public bool ManageRange { get; set; } = true;
    public bool ManageForbiddenZoneDistance { get; set; } = true;
    public bool ManagePartyRoleFollow { get; set; } = true;
    public bool ManagePositionals { get; set; } = true;
    public bool ManageTrueNorth { get; set; } = false;
    public bool ManageLeylines { get; set; } = true;
    public bool UseBetweenTheLines { get; set; } = true;
    public bool UseRetrace { get; set; } = true;
    public bool ReturnToLeylines { get; set; } = true;
    public bool RoleBasedRange { get; set; } = true;
    public bool AoERangeInMultiTarget { get; set; } = true;
    public bool AoEHealerMeleeRange { get; set; } = false;
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
    public bool EscapeGapCloserBLU { get; set; } = true;
    public bool EchoStatusToChat { get; set; } = true;
    public CombatStyle CombatStyle { get; set; } = CombatStyle.Normal;
    public float MeleeRange { get; set; } = DefaultMeleeRange;
    public float RangedRange { get; set; } = DefaultRangedRange;
    public float PhysicalRangedRange { get; set; } = DefaultPhysicalRangedRange;
    public float HealerRange { get; set; } = DefaultHealerRange;
    public float MagicRangedRange { get; set; } = DefaultMagicRangedRange;
    public float AoEMeleeRange { get; set; } = DefaultAoEMeleeRange;
    public float AoERangedRange { get; set; } = DefaultAoERangedRange;
    public float AoEPhysicalRangedRange { get; set; } = DefaultAoEPhysicalRangedRange;
    public float AoEHealerRange { get; set; } = DefaultAoEHealerRange;
    public float AoEMagicRangedRange { get; set; } = DefaultAoEMagicRangedRange;
    public int AoEEnemyThreshold { get; set; } = DefaultAoEEnemyThreshold;
    public float PreferredForbiddenZoneDistance { get; set; } = DefaultPreferredForbiddenZoneDistance;
    public bool HealerPartyCoverage { get; set; } = true;

    [JsonProperty("ManageTrueNorthInRsr")]
    private bool ManageTrueNorthInRsrCompatibility
    {
        set => this.ManageTrueNorth = value;
    }

    internal void Migrate()
    {
        if (this.Version < 2)
        {
            this.PhysicalRangedRange = this.RangedRange;
            this.HealerRange = this.RangedRange;
            this.MagicRangedRange = this.RangedRange;
            this.Version = 2;
        }

        if (this.Version < 3)
        {
            this.AoEPhysicalRangedRange = this.AoERangedRange;
            this.AoEHealerRange = this.AoERangedRange;
            this.AoEMagicRangedRange = this.AoERangedRange;
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
            this.EscapeGapCloserBLU = true;
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
            this.HealerPartyCoverage = true;
            this.Version = 10;
        }
    }

    internal void Clamp()
    {
        this.MeleeRange = Math.Clamp(this.MeleeRange, BossModMinRange, BossModMaxRange);
        this.RangedRange = Math.Clamp(this.RangedRange, BossModMinRange, BossModMaxRange);
        this.PhysicalRangedRange = Math.Clamp(this.PhysicalRangedRange, BossModMinRange, BossModMaxRange);
        this.HealerRange = Math.Clamp(this.HealerRange, BossModMinRange, BossModMaxRange);
        this.MagicRangedRange = Math.Clamp(this.MagicRangedRange, BossModMinRange, BossModMaxRange);
        this.AoEMeleeRange = Math.Clamp(this.AoEMeleeRange, BossModMinRange, BossModMaxRange);
        this.AoERangedRange = Math.Clamp(this.AoERangedRange, BossModMinRange, BossModMaxRange);
        this.AoEPhysicalRangedRange = Math.Clamp(this.AoEPhysicalRangedRange, BossModMinRange, BossModMaxRange);
        this.AoEHealerRange = Math.Clamp(this.AoEHealerRange, BossModMinRange, BossModMaxRange);
        this.AoEMagicRangedRange = Math.Clamp(this.AoEMagicRangedRange, BossModMinRange, BossModMaxRange);
        this.AoEEnemyThreshold = Math.Clamp(this.AoEEnemyThreshold, 1, 10);
        this.PreferredForbiddenZoneDistance = Math.Clamp(this.PreferredForbiddenZoneDistance, 0f, 3f);
        this.CombatStyle = Enum.IsDefined(this.CombatStyle) ? this.CombatStyle : CombatStyle.Normal;
    }

    internal void ResetRanges()
    {
        this.MeleeRange = DefaultMeleeRange;
        this.RangedRange = DefaultRangedRange;
        this.PhysicalRangedRange = DefaultPhysicalRangedRange;
        this.HealerRange = DefaultHealerRange;
        this.MagicRangedRange = DefaultMagicRangedRange;
        this.AoEMeleeRange = DefaultAoEMeleeRange;
        this.AoERangedRange = DefaultAoERangedRange;
        this.AoEPhysicalRangedRange = DefaultAoEPhysicalRangedRange;
        this.AoEHealerRange = DefaultAoEHealerRange;
        this.AoEMagicRangedRange = DefaultAoEMagicRangedRange;
        this.AoEEnemyThreshold = DefaultAoEEnemyThreshold;
        this.PreferredForbiddenZoneDistance = DefaultPreferredForbiddenZoneDistance;
    }

    internal void ResetAll()
    {
        this.Enabled = false;
        this.ManageMovement = true;
        this.ManageRange = true;
        this.ManageForbiddenZoneDistance = true;
        this.ManagePartyRoleFollow = true;
        this.ManagePositionals = true;
        this.ManageTrueNorth = false;
        this.ManageLeylines = true;
        this.UseBetweenTheLines = true;
        this.UseRetrace = true;
        this.ReturnToLeylines = true;
        this.RoleBasedRange = true;
        this.AoERangeInMultiTarget = true;
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
        this.EscapeGapCloserBLU = true;
        this.EchoStatusToChat = true;
        this.CombatStyle = CombatStyle.Normal;
        this.HealerPartyCoverage = true;
        this.ResetRanges();
    }

    internal void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }
}
