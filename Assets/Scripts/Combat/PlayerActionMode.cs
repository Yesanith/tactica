namespace Tactica.Combat
{
    // What a click on the board currently means. Exactly one applies at a time - this decides how
    // input is interpreted, not what the board looks like (that is TileHighlightState, which is
    // [Flags] because several can apply to one tile at once).
    public enum PlayerActionMode
    {
        // No action chosen. Clicking a move-range tile still moves - see PlayerActionController
        // for why this is treated as an implicit Moving.
        None,

        // Move was chosen. Clicking a move-range tile moves the active unit there.
        Moving,

        // An ability was chosen. Clicking an Attackable tile resolves it; clicking anything else
        // cancels.
        Targeting,
    }
}
