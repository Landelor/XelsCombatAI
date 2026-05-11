using System.Collections.Generic;

namespace XelsCombatAI.Combat;

internal interface IMovementCandidateSource
{
    void AddMovementCandidates(MovementPlannerContext context, ICollection<MovementCandidate> candidates);
}
