namespace Tactica.Stats
{
    // What an ability does when it lands. Read by ActionResolver.
    //
    // Values are PINNED, like Faction: Unity serialises the underlying int, so reordering would
    // silently change every authored ability. Add new members at the end with the next number.
    public enum AbilityEffectType
    {
        Damage = 0,
        Heal = 1,
        Buff = 2,
        Debuff = 3,
        StatusEffect = 4,

        // Stands a knocked-out ally back up before its flatline counter runs out, with Power HP.
        // The only effect that targets the knocked out - FFT's Raise / Phoenix Down.
        Revive = 5,

        // Hacks a unit onto the caster's side. Success chance is Power + the caster's Tech,
        // clamped - FFT's Invite, whose base rate is MA + 20. A converted unit fights for its new
        // side from its next turn, and if it ends the mission there it can join the crew.
        Override = 6,
    }
}
