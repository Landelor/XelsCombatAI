namespace XelsCombatAI.Runtime;

internal static class StatusReporter
{
    public static string Build(RuntimeStatus status)
    {
        return $"Enabled={status.Enabled}, Dependencies={(status.DependencyWarning ?? "OK")}, TrueNorthManagement={(status.TrueNorthWarning ?? status.RsrTrueNorthDisabled?.ToString() ?? "NotManaged")}, Preset={BossModIpc.DefaultPresetName}, LastPositional={status.LastPositional}, TrueNorthCharges={status.TrueNorthCharges}, TrueNorthActive={status.TrueNorthActive}, Range={status.LastRange:0.0}, Movement={status.LastMovement}, MovementRange={status.LastMovementRangeStrategy}, Cushion={status.LastForbiddenZoneCushion}, Role={status.LastPartyRole}, LeylinesBTL={status.LastLeylinesBetweenTheLines}, LeylinesRetrace={status.LastLeylinesRetrace}, LeylinesGoal={status.LastLeylinesGoal}, BmrGapMNK={status.LastMonkThunderclap}, BmrGapDRG={status.LastDragoonWingedGlide}, BmrGapNIN={status.LastNinjaShukuchi}, BmrGapVPR={status.LastViperSlither}, GapPLD={status.GapCloserPLD}, GapWAR={status.GapCloserWAR}, GapDRK={status.GapCloserDRK}, GapGNB={status.GapCloserGNB}, GapSAM={status.GapCloserSAM}, GapRPR={status.GapCloserRPR}, EscapeGapMNK={status.EscapeGapCloserMNK}, EscapeGapNIN={status.EscapeGapCloserNIN}, EscapeGapRPR={status.EscapeGapCloserRPR}, EscapeGapVPR={status.EscapeGapCloserVPR}, EscapeGapBLM={status.EscapeGapCloserBLM}, EscapeGapSGE={status.EscapeGapCloserSGE}, EscapeGapPCT={status.EscapeGapCloserPCT}, EscapeGapBLU={status.EscapeGapCloserBLU}, ReflectedGapSafety={status.ReflectedGapSafety}, LastGapCloser={status.LastGapCloserSafety}, LastEscapeGapCloser={status.LastEscapeGapCloserSafety}, Initialized={status.InitializedPreset}";
    }
}
