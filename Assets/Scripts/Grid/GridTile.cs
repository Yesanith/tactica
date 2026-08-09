using UnityEngine;

namespace Tactica.Grid
{
    // Data for a single grid cell. Pure data - no rendering, no behaviour.
    //
    // Struct, so a Dictionary lookup returns a COPY. Unlike UE5's TMap::Find returning FGridTile*,
    // you cannot mutate a tile in place:
    //
    //     GridTile tile = GridTiles[coords];
    //     tile.Height = 3;              // modifies the copy only
    //     GridTiles[coords] = tile;     // required write-back
    //
    // GridManager's SetOccupant/SetHeight/SetWalkable wrap that pattern.
    [System.Serializable]
    public struct GridTile
    {
        // X maps to world X, Y maps to world Z.
        public Vector2Int Coordinates;

        // Integer elevation level; converted to world units by GridManager.
        public int Height;

        public bool IsWalkable;

        // The unit standing here, or null. Test with == null: Unity's fake-null means a destroyed
        // GameObject is not literally null.
        public GameObject Occupant;

        public GridTile(Vector2Int coordinates, int height, bool isWalkable, GameObject occupant = null)
        {
            Coordinates = coordinates;
            Height = height;
            IsWalkable = isWalkable;
            Occupant = occupant;
        }

        // INTENTIONAL API: unused today, the natural predicate for spawn placement and AI.
        public bool IsFree => IsWalkable && Occupant == null;

        public override string ToString()
        {
            return $"GridTile({Coordinates.x},{Coordinates.y}) H={Height} Walkable={IsWalkable} Occupant={(Occupant == null ? "none" : Occupant.name)}";
        }
    }
}
