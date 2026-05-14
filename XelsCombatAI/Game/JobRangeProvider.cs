using System;
using XelsCombatAI.Services;

namespace XelsCombatAI.Game;

internal sealed class JobRangeProvider(DalamudServices services) : IDisposable
{
    public float EngagementRange { get; private set; } = Configuration.InternalMeleeUptimeRange;
    public float PackAoeRange { get; private set; } = Configuration.InternalMeleeUptimeRange;

    private uint lastClassJobId;
    private byte lastLevel;

    public void Initialize() => services.ClientState.TerritoryChanged += this.OnTerritoryChanged;

    public void Dispose() => services.ClientState.TerritoryChanged -= this.OnTerritoryChanged;

    public void Tick()
    {
        var player = services.ObjectTable.LocalPlayer;
        var classJobId = player?.ClassJob.RowId ?? 0;
        var level = player?.Level ?? 0;
        if (classJobId == 0 || level == 0 ||
            classJobId == this.lastClassJobId && level == this.lastLevel)
        {
            return;
        }

        this.lastClassJobId = classJobId;
        this.lastLevel = level;
        this.Recompute(classJobId, level);
    }

    private void Recompute(uint classJobId, byte level)
    {
        var engagementRange = ResolveEngagementRange(classJobId);
        var packAoeRange = HasPackAoeAtLevel(classJobId, level)
            ? ResolvePackAoeRange(classJobId)
            : engagementRange;
        (this.EngagementRange, this.PackAoeRange) = (engagementRange, packAoeRange);
    }

    private static float ResolveEngagementRange(uint classJobId)
    {
        return classJobId switch
        {
            // Physical ranged
            5 or 23 or 31 or 38 => 25f,
            // Healers
            6 or 24 or 28 or 33 or 40 => 25f,
            // Magic ranged
            7 or 25 or 26 or 27 or 35 or 42 => 25f,
            _ => Configuration.InternalMeleeUptimeRange,
        };
    }

    private static float ResolvePackAoeRange(uint classJobId)
    {
        return classJobId switch
        {
            // Tanks
            1 or 19 or 3 or 21 or 32 or 37 => 2.6f,
            // Melee
            2 or 20 or 4 or 22 or 29 or 30 or 34 or 39 or 41 => 2.6f,
            // Physical Ranged
            5 or 23 => 8f,   // ARC / BRD — Rain of Death
            31 => 12f,  // MCH — Auto Crossbow / Scattergun
            38 => 5f,   // DNC — Windmill / Bladeshower
            // Healers
            6 or 24 => 8f,   // CNJ / WHM — Holy
            28 => 5f,   // SCH — Art of War
            33 => 25f,   // AST — Gravity
            40 => 5f,   // SGE — Dyskrasia
            // Magic Ranged — AoE at 25y except RDM
            7 or 25 => 25f,  // THM / BLM
            26 or 27 => 25f, // ACN / SMN
            35 => 8f,   // RDM — Veraero II / Impact
            42 => 25f,  // PCT
            // Default: melee
            _ => Configuration.InternalMeleeUptimeRange,
        };
    }

    private static bool HasPackAoeAtLevel(uint classJobId, byte level)
        => level >= MinimumPackAoeLevel(classJobId);

    private static int MinimumPackAoeLevel(uint classJobId)
    {
        return classJobId switch
        {
            // Tanks
            1 or 19 => 6,      // GLA / PLD — Total Eclipse
            3 or 21 => 10,     // MRD / WAR — Overpower
            32 => 6,           // DRK — Unleash
            37 => 10,          // GNB — Demon Slice
            // Melee
            2 or 20 => 26,     // PGL / MNK — Arm of the Destroyer
            4 or 22 => 40,     // LNC / DRG — Doom Spike
            29 or 30 => 38,    // ROG / NIN — Death Blossom
            34 => 26,          // SAM — Fuga
            39 => 25,          // RPR — Spinning Scythe
            41 => 10,          // VPR — Steel Maw
            // Physical ranged
            5 or 23 => 18,     // ARC / BRD — Quick Nock
            31 => 18,          // MCH — Spread Shot
            38 => 15,          // DNC — Windmill
            // Healers
            6 or 24 => 45,     // CNJ / WHM — Holy
            28 => 46,          // SCH — Art of War
            33 => 45,          // AST — Gravity
            40 => 46,          // SGE — Dyskrasia
            // Magic ranged
            7 or 25 => 12,     // THM / BLM — Blizzard II
            26 or 27 => 26,    // ACN / SMN — Outburst
            35 => 18,          // RDM — Veraero II / Verthunder II
            42 => 25,          // PCT — Fire II in Red
            _ => 1,
        };
    }

    private void OnTerritoryChanged(uint _)
    {
        this.lastClassJobId = 0;
        this.lastLevel = 0;
        this.Tick();
    }
}
