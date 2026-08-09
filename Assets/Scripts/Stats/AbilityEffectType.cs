namespace Tactica.Stats
{
    // What an ability does. Power is interpreted against this - damage dealt, HP restored, and so
    // on - so the pair only makes sense read together.
    //
    // Not [Flags]: an ability is one kind of thing. A skill that damages *and* debuffs is better
    // modelled as a list of effects than as a combined enum value, and that list is the shape this
    // will likely grow into.
    public enum AbilityEffectType
    {
        Damage,
        Heal,
        Buff,
        Debuff,
        StatusEffect,
    }
}
