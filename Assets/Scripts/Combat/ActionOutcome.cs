using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // What one carried-out action did to one target, as reported by ActionResolver.OnActionResolved.
    //
    // "Effective" is the progression rule from GAME_DESIGN.md: JP and XP come only from actions
    // that changed something - a hit that landed, HP restored, a reboot, a hack that took, a status
    // applied. A miss, a failed hack or a heal on a full-HP ally is carried out (it costs the action
    // and MP) but earns nothing.
    public readonly struct ActionOutcome
    {
        public readonly GridUnit User;
        public readonly GridUnit Target;
        public readonly AbilityDefinition Ability;
        public readonly bool Effective;

        // The action took the target from standing to knocked out.
        public readonly bool KnockedOut;

        public ActionOutcome(GridUnit user, GridUnit target, AbilityDefinition ability, bool effective, bool knockedOut)
        {
            User = user;
            Target = target;
            Ability = ability;
            Effective = effective;
            KnockedOut = knockedOut;
        }
    }
}
