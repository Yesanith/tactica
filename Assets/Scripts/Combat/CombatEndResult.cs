namespace Tactica.Combat
{
    // Outcome of an encounter, as reported by CombatManager.CheckCombatEndCondition.
    public enum CombatEndResult
    {
        // Ongoing is deliberately first, so default(CombatEndResult) means "not over".
        // A zero value of PlayerVictory would make every uninitialised result an accidental win.
        Ongoing,

        PlayerVictory,
        PlayerDefeat,
    }
}
