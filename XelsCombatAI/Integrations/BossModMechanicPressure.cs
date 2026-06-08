using System;
using System.Globalization;

namespace XelsCombatAI.Integrations;

internal enum BossModMechanicPressureKind
{
    None,
    Knockback,
    Tankbuster,
    Raidwide,
    Damage,
    Downtime,
    Vulnerable
}

internal sealed record BossModMechanicPressure(
    float BMRRaidwideIn,
    float BMRTankbusterIn,
    float BMRKnockbackIn,
    float BMRDamageIn,
    float BMRDowntimeIn,
    float BMRDowntimeEndIn,
    float BMRVulnerableIn,
    float BMRVulnerableEndIn,
    DateTime KnockbackRecoveryUntilUtc)
{
    public const float RaidwidePressureSeconds = 3f;
    public const float DamagePressureSeconds = 3f;
    public const float TankbusterPressureSeconds = 4f;
    public const float TankbusterHardPressureSeconds = 1.5f;
    public const float KnockbackReserveSeconds = 5f;
    public const float KnockbackHardPressureSeconds = 1.5f;
    public const float DowntimePressureSeconds = 3f;
    public const float VulnerablePressureSeconds = 3f;

    public static BossModMechanicPressure None { get; } = new(
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        float.MaxValue,
        DateTime.MinValue);

    public bool RaidwideSoon => IsSoon(this.BMRRaidwideIn, RaidwidePressureSeconds);
    public bool TankbusterSoon => IsSoon(this.BMRTankbusterIn, TankbusterPressureSeconds);
    public bool KnockbackSoon => IsSoon(this.BMRKnockbackIn, KnockbackReserveSeconds);
    public bool DamageSoon => IsSoon(this.BMRDamageIn, DamagePressureSeconds);
    public bool DowntimeSoon => IsSoon(this.BMRDowntimeIn, DowntimePressureSeconds);
    public bool VulnerableSoon => IsSoon(this.BMRVulnerableIn, VulnerablePressureSeconds);
    public bool HardTankbusterSoon => IsSoon(this.BMRTankbusterIn, TankbusterHardPressureSeconds);
    public bool HardKnockbackSoon => IsSoon(this.BMRKnockbackIn, KnockbackHardPressureSeconds);
    public bool KnockbackRecoveryActive => DateTime.UtcNow <= this.KnockbackRecoveryUntilUtc;
    public bool RaidwideOrDamageSoon => this.RaidwideSoon || this.DamageSoon;
    public bool BadForOptionalMovement => this.KnockbackSoon || this.RaidwideOrDamageSoon || this.DowntimeSoon;
    public bool BadForGreedyDash => this.KnockbackSoon || this.RaidwideOrDamageSoon || this.DowntimeSoon;
    public bool TankStabilityPressure => this.TankbusterSoon;

    public BossModMechanicPressureKind PrimaryPressure
    {
        get
        {
            if (this.KnockbackSoon)
            {
                return BossModMechanicPressureKind.Knockback;
            }

            if (this.TankbusterSoon)
            {
                return BossModMechanicPressureKind.Tankbuster;
            }

            if (this.RaidwideSoon)
            {
                return BossModMechanicPressureKind.Raidwide;
            }

            if (this.DamageSoon)
            {
                return BossModMechanicPressureKind.Damage;
            }

            if (this.DowntimeSoon)
            {
                return BossModMechanicPressureKind.Downtime;
            }

            return this.VulnerableSoon
                ? BossModMechanicPressureKind.Vulnerable
                : BossModMechanicPressureKind.None;
        }
    }

    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"{this.PrimaryPressure}: raidwide={FormatTimer(this.BMRRaidwideIn)}, tankbuster={FormatTimer(this.BMRTankbusterIn)}, knockback={FormatTimer(this.BMRKnockbackIn)}, damage={FormatTimer(this.BMRDamageIn)}, downtime={FormatTimer(this.BMRDowntimeIn)}, vulnerable={FormatTimer(this.BMRVulnerableIn)}, kbRecovery={this.KnockbackRecoveryActive}");

    public BossModMechanicPressure WithKnockbackRecoveryUntil(DateTime recoveryUntilUtc)
    {
        return this with { KnockbackRecoveryUntilUtc = recoveryUntilUtc };
    }

    public string FormatOptionalMovementHoldReason()
    {
        return this.PrimaryPressure switch
        {
            BossModMechanicPressureKind.Knockback => string.Create(CultureInfo.InvariantCulture, $"held: knockback in {this.BMRKnockbackIn:0.0}s"),
            BossModMechanicPressureKind.Raidwide => string.Create(CultureInfo.InvariantCulture, $"held: raidwide in {this.BMRRaidwideIn:0.0}s"),
            BossModMechanicPressureKind.Damage => string.Create(CultureInfo.InvariantCulture, $"held: damage in {this.BMRDamageIn:0.0}s"),
            BossModMechanicPressureKind.Downtime => string.Create(CultureInfo.InvariantCulture, $"held: downtime in {this.BMRDowntimeIn:0.0}s"),
            BossModMechanicPressureKind.Tankbuster => string.Create(CultureInfo.InvariantCulture, $"held: tankbuster in {this.BMRTankbusterIn:0.0}s"),
            BossModMechanicPressureKind.Vulnerable => string.Create(CultureInfo.InvariantCulture, $"held: vulnerability window in {this.BMRVulnerableIn:0.0}s"),
            _ => "no mechanic pressure"
        };
    }

    private static bool IsSoon(float value, float threshold)
    {
        return float.IsFinite(value) && value > 0f && value <= threshold;
    }

    private static string FormatTimer(float value)
    {
        return float.IsFinite(value) && value < float.MaxValue / 2f
            ? value.ToString("0.0", CultureInfo.InvariantCulture)
            : "none";
    }
}

internal sealed class BossModMechanicPressureMonitor
{
    private static readonly TimeSpan KnockbackRecoveryWindow = TimeSpan.FromSeconds(2);

    private bool sawKnockbackPressure;
    private DateTime knockbackRecoveryUntilUtc = DateTime.MinValue;

    public BossModMechanicPressure Current { get; private set; } = BossModMechanicPressure.None;

    public void Update(BossModIpc bossMod)
    {
        var now = DateTime.UtcNow;
        var pressure = bossMod.GetMechanicPressure();
        if (pressure.KnockbackSoon)
        {
            this.sawKnockbackPressure = true;
        }
        else if (this.sawKnockbackPressure)
        {
            this.knockbackRecoveryUntilUtc = now.Add(KnockbackRecoveryWindow);
            this.sawKnockbackPressure = false;
        }

        if (now > this.knockbackRecoveryUntilUtc && !pressure.KnockbackSoon)
        {
            this.knockbackRecoveryUntilUtc = DateTime.MinValue;
        }

        this.Current = pressure.WithKnockbackRecoveryUntil(this.knockbackRecoveryUntilUtc);
    }

    public void Reset()
    {
        this.sawKnockbackPressure = false;
        this.knockbackRecoveryUntilUtc = DateTime.MinValue;
        this.Current = BossModMechanicPressure.None;
    }
}
