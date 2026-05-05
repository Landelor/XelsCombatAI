namespace XelsCombatAI.Game;

internal static class CombatConstants
{
    public const float MeleeActionRange = 3f;
    public const float HealerCoverageAttackRange = 25f;
    public const float GapCloserMaxRange = 20f;
    public const float GapCloserMinimumDistance = 4f;
    public const float GapCloserDestinationMeleeRange = 2.6f;
    public const float EscapeGapCloserMinimumSafetyDistance = 4f;
    public const double EscapeGapCloserDangerWindowMilliseconds = 750d;
    public const float FixedForwardGapCloserRange = 15f;
    public const float PositionalDotThreshold = 0.7071068f;
    public const string AvaricePositionalStatusKey = "Avarice.PositionalStatus";
    public static readonly float[] EscapeLocationRadii = [8f, 12f, 16f, 20f];
}
