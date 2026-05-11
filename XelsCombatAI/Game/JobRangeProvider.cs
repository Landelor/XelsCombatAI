using System;
using XelsCombatAI.Services;

namespace XelsCombatAI.Game;

internal sealed class JobRangeProvider(DalamudServices services) : IDisposable
{
    public float EngagementRange { get; private set; } = Configuration.InternalMeleeUptimeRange;
    public float PackAoeRange { get; private set; } = Configuration.InternalMeleeUptimeRange;

    private uint lastClassJobId;

    public void Initialize() => services.ClientState.TerritoryChanged += this.OnTerritoryChanged;

    public void Dispose() => services.ClientState.TerritoryChanged -= this.OnTerritoryChanged;

    public void Tick()
    {
        var classJobId = services.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        if (classJobId == 0 || classJobId == this.lastClassJobId)
            return;
        this.lastClassJobId = classJobId;
        this.Recompute(classJobId);
    }

    private void Recompute(uint classJobId)
    {
        (this.EngagementRange, this.PackAoeRange) = classJobId switch
        {
            // Tanks
            1 or 19 or 3 or 21 or 32 or 37 => (2.6f, 2.6f),
            // Melee
            2 or 20 or 4 or 22 or 29 or 30 or 34 or 39 or 41 => (2.6f, 2.6f),
            // Physical Ranged
            5 or 23 => (25f, 8f),   // ARC / BRD — Rain of Death
            31 => (25f, 12f),  // MCH — Auto Crossbow / Scattergun
            38 => (25f, 5f),   // DNC — Windmill / Bladeshower
            // Healers
            6 or 24 => (25f, 8f),   // CNJ / WHM — Holy
            28 => (25f, 5f),   // SCH — Art of War
            33 => (25f, 25f),   // AST — Gravity
            40 => (25f, 5f),   // SGE — Dyskrasia
            // Magic Ranged — AoE at 25y except RDM
            7 or 25 => (25f, 25f),  // THM / BLM
            26 or 27 => (25f, 25f), // ACN / SMN
            35 => (25f, 8f),   // RDM — Veraero II / Impact
            42 => (25f, 25f),  // PCT
            // Default: melee
            _ => (2.6f, 2.6f),
        };
    }

    private void OnTerritoryChanged(uint _)
    {
        this.lastClassJobId = 0;
        this.Tick();
    }
}
