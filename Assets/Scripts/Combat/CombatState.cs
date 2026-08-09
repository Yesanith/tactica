namespace Tactica.Combat
{
    // Phases of a combat encounter. Exactly one is active at a time - this is a state machine,
    // not a set of flags.
    public enum CombatState
    {
        // Active unit's turn, waiting on the player (or AI) to choose an action.
        WaitingForInput,

        // An action has been chosen and is playing out. Input should be ignored here, otherwise
        // a second command can land mid-animation.
        ResolvingAction,

        // Between turns: hand-off to the next unit in initiative order.
        TurnTransition,

        // Encounter is over. Terminal - nothing advances out of this.
        CombatEnd,
    }
}
