namespace Tactica.Stats
{
    // One entry in a job's learn list: "at this job level, this ability becomes available".
    //
    // Ability is a direct asset reference, not a name string. Renaming an ability now updates
    // every job that teaches it, a typo is impossible, and the Project window's "Find References"
    // actually works - none of which was true of the string placeholder this replaces.
    [System.Serializable]
    public struct AbilityUnlock
    {
        public int RequiredLevel;
        public AbilityDefinition Ability;

        public AbilityUnlock(int requiredLevel, AbilityDefinition ability)
        {
            RequiredLevel = requiredLevel;
            Ability = ability;
        }

        // An entry added in the Inspector but left unassigned. Worth testing before use rather
        // than handing a null down to whatever consumes the learn list.
        // == null, not ?., because Unity's fake-null needs the overloaded operator.
        public bool IsAssigned => Ability != null;

        public override string ToString()
        {
            return Ability == null
                ? $"Lv{RequiredLevel}: <unassigned>"
                : $"Lv{RequiredLevel}: {Ability.AbilityName}";
        }
    }
}
