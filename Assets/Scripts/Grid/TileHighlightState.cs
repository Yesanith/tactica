using System;

namespace Tactica.Grid
{
    // Highlight layers a tile can carry. [Flags] because hover and move-range overlap: a tile can
    // be both at once, and each system must be able to set/clear its own bit without destroying
    // the other's. Visual priority (Hovered over InMoveRange) is resolved when the material is
    // picked, not by which system wrote last - see GridManager.ApplyHighlightVisual.
    [Flags]
    public enum TileHighlightState
    {
        None = 0,
        InMoveRange = 1 << 0,
        Hovered = 1 << 1,

        // Tiles holding a unit the current ability may legally hit. Independent of InMoveRange:
        // a tile can be both reachable and attackable, and each system sets only its own bit.
        Attackable = 1 << 2,

        // The pre-fight deployment zone: where a crew member may be placed. Its own layer and
        // colour, so "you may start here" never reads as a move range.
        Deployable = 1 << 3,

        // Where a charged ability will go off. Stays lit until it fires, so everyone can see the
        // danger coming and step out of it.
        Charging = 1 << 4,
    }
}
