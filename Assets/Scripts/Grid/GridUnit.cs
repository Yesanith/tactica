using UnityEngine;
using UnityEngine.Serialization;

namespace Tactica.Grid
{
    // A placeholder unit occupying a single tile. Owns its coordinate and keeps its transform
    // parked on that tile's surface. No movement, no stats, no turn logic.
    // Assigns no mesh or material - build the visual (e.g. a Capsule) in the Inspector.
    public class GridUnit : MonoBehaviour
    {
        // Lowercase so the public property below can take the PascalCase name. FormerlySerializedAs
        // keeps the Inspector value across the rename (Unity serialises by field name).
        [Tooltip("Tile this unit currently stands on.")]
        [FormerlySerializedAs("GridCoords")]
        [SerializeField] private Vector2Int gridCoords;

        [Tooltip("How many tiles this unit can move per turn.")]
        [SerializeField] private int moveRange = 3;

        [Tooltip("Grid this unit belongs to. Defaults to the first GridManager in the scene.")]
        [SerializeField] private GridManager GridManagerRef;

        [Tooltip("Distance from this GameObject's pivot down to its feet. Unity's Capsule is 2 " +
                 "units tall with a centred pivot, so 1.0 fits an unscaled capsule. Use 0 for a " +
                 "model whose pivot already sits at its feet.")]
        [SerializeField] private float PivotHeight = 1.0f;

        public Vector2Int GridCoords => gridCoords;

        // Read-only: spending movement is turn-system behaviour that doesn't exist yet.
        public int MoveRange => moveRange;

        private void Awake()
        {
            if (GridManagerRef == null)
            {
                // Full scene scan - fine once at startup, never per frame.
                GridManagerRef = FindAnyObjectByType<GridManager>();
            }
        }

        // Start, not Awake: GridManager builds GridTiles in its Awake, and every Awake runs before
        // any Start. Position is computed from grid data only, never from a spawned tile's
        // transform, so it does not matter whether GridManager.Start has run yet.
        private void Start()
        {
            SnapToGridPosition();

            // Claim the starting tile, otherwise nothing marks it occupied until the first move
            // and other units could path onto it. Start, not Awake: GridTiles is built in
            // GridManager.Awake and the order of two Awakes is not guaranteed.
            if (GridManagerRef != null)
            {
                GridManagerRef.SetOccupant(gridCoords, gameObject);
            }
        }

        // Places this unit on top of the tile at GridCoords.
        [ContextMenu("Snap To Grid Position")]
        public void SnapToGridPosition()
        {
            if (GridManagerRef == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridUnit)} '{name}': no {nameof(GridManagerRef)} assigned and none found in the scene. " +
                    "Cannot snap to grid.",
                    this);
                return;
            }

            // pivot = tile centre + half the tile slab + pivot-to-feet.
            // TileSurfaceOffset comes from GridManager rather than a constant, so changing tile
            // thickness lifts units to match instead of half-burying them.
            Vector3 tileCentre = GridManagerRef.GridToWorldPosition(GridCoords);
            float verticalOffset = GridManagerRef.TileSurfaceOffset + PivotHeight;

            transform.position = tileCentre + new Vector3(0f, verticalOffset, 0f);
        }

        // Pure placement: does not check walkability or update tile occupancy.
        public void SetGridCoords(Vector2Int coords)
        {
            gridCoords = coords;
            SnapToGridPosition();
        }
    }
}
