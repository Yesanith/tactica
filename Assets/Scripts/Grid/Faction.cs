namespace Tactica.Grid
{
    // Which side a unit fights for. Replaces the isPlayerControlled bool, which could only ever
    // express two sides and had no way to represent a unit changing allegiance.
    //
    // Lives in Tactica.Grid because GridUnit owns it, and Combat and UI already depend on Grid -
    // so no new namespace edge appears.
    //
    // Values are PINNED. Unity serialises an enum as its underlying int, so reordering the members
    // silently reassigns every authored unit in every scene and prefab - a Player would quietly
    // become an Enemy with no diff to notice. Adding a new member is safe as long as it takes the
    // next unused number rather than being inserted.
    public enum Faction
    {
        Player = 0,
        Enemy = 1,

        // Not used by anything today, deliberately. Recruitable-but-not-yet-recruited villagers,
        // quest NPCs, wild creatures and summons all need it, and adding an enum member later is
        // free while migrating serialised data a second time is not.
        Neutral = 2,
    }
}
