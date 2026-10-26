namespace Tactica.Stats
{
    // Which stat an ability adds to its Power. Strength for blades and fists, Tech for hacks,
    // medicine and anything a Netrunner or Medic scales with.
    //
    // Values PINNED.
    public enum StatScaling
    {
        Strength = 0,
        Tech = 1,
    }

    // What a status does besides its stat modifier, ticked at the start of the affected unit's own
    // turns. Durations count those turns, not clock ticks - "until its third turn" is something a
    // player can read off the turn queue.
    //
    // Values PINNED.
    public enum StatusKind
    {
        // Only the stat modifier (Buff / Debuff abilities).
        None = 0,

        // The unit loses its turns while it lasts (FFT's Stop / Don't Act, collapsed into one).
        Stun = 1,

        // Power damage at the start of each of its turns - bleed, burning, a virus.
        DamageOverTime = 2,

        // Power healing at the start of each of its turns - Regen, a stim.
        HealOverTime = 3,

        // AI units must target the source if they can reach it (Phase 13 makes the AI obey).
        Taunt = 4,

        // On the user itself: until its next turn, the first enemy that ends a move inside the
        // ability's reach (weapon range) draws a free shot. Fires once, then drops.
        Overwatch = 5,
    }

    // What a Reaction-slot ability does when its owner is hit. Each rolls its own TriggerChance -
    // GAME_DESIGN.md's fixed per-reaction rate, in place of FFT's Brave.
    //
    // Values PINNED.
    public enum ReactionKind
    {
        None = 0,

        // Strike back at the attacker with the reaction's Power, if the attacker is in its range.
        // A counter never triggers another reaction, so two counter-fighters cannot loop.
        Counter = 1,

        // Gain the reaction's stat modifier for its Duration - armour hardening after a hit.
        Brace = 2,
    }
}
