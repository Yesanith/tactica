using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Tactica.Grid
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup - at file scope it would be checked too late.
    using Camera = UnityEngine.Camera;

    // Grid data layer: tile storage, generation, coordinate conversion, and the visuals derived
    // from them.
    public class GridManager : MonoBehaviour
    {
        // Dictionary rather than GridTile[,] (UE5: TMap<FIntPoint, FGridTile>). Costs a hash per
        // lookup and loses cache locality, but supports sparse/irregular maps, negative
        // coordinates, and runtime add/remove. At 100-10,000 tiles that trade is free. If
        // pathfinding ever profiles hot, flatten to an array for the search rather than replacing
        // this store.
        public Dictionary<Vector2Int, GridTile> GridTiles { get; private set; } = new Dictionary<Vector2Int, GridTile>();

        // Lowercase backing fields so the public properties below can take the PascalCase names.
        // FormerlySerializedAs keeps Inspector values across the rename.
        [Header("Grid Dimensions")]
        [FormerlySerializedAs("GridWidth")]
        [SerializeField] private int gridWidth = 10;

        [FormerlySerializedAs("GridDepth")]
        [SerializeField] private int gridDepth = 10;

        // Unity is metres: 1 unit = 1m. (UE5 defaults to 1uu = 1cm, so don't carry 100 over.)
        [FormerlySerializedAs("TileSize")]
        [SerializeField] private float tileSize = 1.0f;

        // Read-only: these define the grid's shape, and changing them at runtime without
        // regenerating would desync GridTiles from the dimensions.
        public int GridWidth => gridWidth;
        public int GridDepth => gridDepth;
        public float TileSize => tileSize;

        [Tooltip("World units added per Height level. Separate from TileSize so elevation steps " +
                 "can be shallower or steeper than a tile is wide.")]
        [SerializeField] private float HeightStep = 1.0f;

        [Header("Rendering")]
        [Tooltip("Cube prefab instantiated once per tile. Leave empty to keep the grid data-only.")]
        [SerializeField] private GameObject TilePrefab;

        // Lowercase so the public property below can take the PascalCase name. FormerlySerializedAs
        // keeps the Inspector value across the rename (Unity serialises by field name).
        [Tooltip("Y scale of each spawned tile. Small values read as a floor slab, not a cube.")]
        [FormerlySerializedAs("TileVisualHeight")]
        [SerializeField] private float tileVisualHeight = 0.1f;

        public float TileVisualHeight => tileVisualHeight;

        // Tiles are centre-pivoted cubes placed on the point GridToWorldPosition returns, so the
        // walkable top face is half a thickness above it.
        public float TileSurfaceOffset => tileVisualHeight * 0.5f;

        [Header("Highlighting")]
        [Tooltip("Default material applied to a tile with no highlight layers set.")]
        [SerializeField] private Material BaseMaterial;

        [Tooltip("Hover colour. Takes priority over RangeMaterial when both apply.")]
        [SerializeField] private Material HighlightMaterial;

        [Tooltip("Move-range colour.")]
        [SerializeField] private Material RangeMaterial;

        [Header("Movement")]
        // Placeholder for tuning: will likely become per-unit, with separate up/down limits.
        [Tooltip("Maximum absolute height difference a unit may traverse in one step.")]
        [SerializeField] private int MaxStepHeight = 1;

        // TEMPORARY TEST WIRING - REMOVE once unit-selection input calls ShowMoveRangeForUnit.
        [Header("Debug (temporary)")]
        [Tooltip("TEMPORARY: if assigned, this unit's move range is highlighted on Start.")]
        [SerializeField] private GridUnit TestUnit;

        // Must exist in Project Settings > Tags and Layers. Layers cannot be created from a
        // script, only read by name - so this resolves to -1 if someone deletes it.
        private const string TileLayerName = "Tiles";

        // Cached: NameToLayer is a string lookup, and this keeps the missing-layer warning to one
        // message instead of one per tile.
        private int? CachedTileLayer;

        // Spawned visuals, keyed like GridTiles. Kept parallel to the data rather than as a field
        // on GridTile, so the data layer holds no scene references.
        private readonly Dictionary<Vector2Int, GameObject> SpawnedTiles = new Dictionary<Vector2Int, GameObject>();

        // Reverse lookup for raycast hits, which hand back a GameObject. Dictionary has no
        // value->key search, and scanning SpawnedTiles every frame would be O(n).
        // Written and cleared together with SpawnedTiles.
        private readonly Dictionary<GameObject, Vector2Int> ObjectToCoords = new Dictionary<GameObject, Vector2Int>();

        // Which highlight layers each tile currently carries. Absent key means None.
        private readonly Dictionary<Vector2Int, TileHighlightState> HighlightStates = new Dictionary<Vector2Int, TileHighlightState>();

        // Nullable because Vector2Int is a struct with no spare sentinel value - (0,0) and (-1,-1)
        // are both valid coordinates.
        private Vector2Int? HighlightedCoords;

        // Exactly the tiles lit by the last ShowMoveRangeForUnit call. Recorded rather than
        // recomputed at clear time, since the grid may have changed in between.
        private readonly List<Vector2Int> MoveRangeHighlights = new List<Vector2Int>();

        // Awake -> data, Start -> visuals. Every Awake runs before any Start, so other scripts can
        // reshape the grid in their Awake and the visuals will reflect it.
        private void Awake()
        {
            GenerateGrid();
        }

        private void Start()
        {
            RenderGrid();

            // TEMPORARY TEST WIRING - REMOVE with the TestUnit field.
            if (TestUnit != null)
            {
                ShowMoveRangeForUnit(TestUnit);
            }
        }

        // Builds a flat, fully walkable grid from (0,0) to (GridWidth-1, GridDepth-1).
        // Safe to call again at runtime.
        public void GenerateGrid()
        {
            GridTiles.Clear();
            GridTiles.EnsureCapacity(gridWidth * gridDepth);

            for (int x = 0; x < gridWidth; x++)
            {
                for (int z = 0; z < gridDepth; z++)
                {
                    Vector2Int coords = new Vector2Int(x, z);
                    GridTiles[coords] = new GridTile(coords, height: 0, isWalkable: true);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Rendering
        // ---------------------------------------------------------------------

        // Spawns one TilePrefab per tile, clearing any previous set first.
        // One GameObject per tile is fine at this scale and gives raycast picking for free.
        // Graphics.RenderMeshInstanced would be the move at ~10,000 tiles; the swap is local here.
        [ContextMenu("Render Grid")]
        public void RenderGrid()
        {
            if (TilePrefab == null)
            {
                Debug.LogWarning($"{nameof(GridManager)}: no {nameof(TilePrefab)} assigned, nothing to render.", this);
                return;
            }

            ClearRenderedTiles();

            // Load-bearing for hover detection: Physics.Raycast only sees colliders. Unity's Cube
            // primitive has a BoxCollider already; a prefab without one renders fine and is
            // silently un-hoverable.
            if (TilePrefab.GetComponentInChildren<Collider>() == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridManager)}: {nameof(TilePrefab)} '{TilePrefab.name}' has no Collider. " +
                    "Tiles will render but cursor hover detection will not work. Add a BoxCollider to the prefab.",
                    this);
            }

            foreach (KeyValuePair<Vector2Int, GridTile> entry in GridTiles)
            {
                Vector2Int coords = entry.Key;

                // Parenting via the Instantiate argument avoids a redundant transform recalculation.
                GameObject tileObject = Instantiate(TilePrefab, GridToWorldPosition(coords), Quaternion.identity, transform);

                tileObject.name = $"Tile_{coords.x}_{coords.y}";

                // Puts the tile on the layer GetTileUnderCursor filters for, so units standing on
                // it are transparent to hover detection.
                if (TileLayer >= 0)
                {
                    SetLayerRecursively(tileObject, TileLayer);
                }

                // Default cube is 1x1x1, so localScale is effectively size in metres. Y is squashed
                // flat; elevation already came through in GridToWorldPosition via HeightStep.
                tileObject.transform.localScale = new Vector3(tileSize, tileVisualHeight, tileSize);

                SpawnedTiles[coords] = tileObject;
                ObjectToCoords[tileObject] = coords;

                // Registered first so the visual resolver can find the object. State is None here
                // (ClearRenderedTiles wiped it), so this applies BaseMaterial.
                ApplyHighlightVisual(coords);
            }
        }

        [ContextMenu("Clear Rendered Tiles")]
        public void ClearRenderedTiles()
        {
            foreach (GameObject tileObject in SpawnedTiles.Values)
            {
                // Unity fake-null: an externally destroyed object leaves a non-null C# reference
                // that only == null catches.
                if (tileObject == null)
                {
                    continue;
                }

                // Destroy is deferred and does nothing in edit mode, which would leak a duplicate
                // set of cubes every time RenderGrid runs from the context menu.
                if (Application.isPlaying)
                {
                    Destroy(tileObject);
                }
                else
                {
                    DestroyImmediate(tileObject);
                }
            }

            SpawnedTiles.Clear();
            ObjectToCoords.Clear();

            // All highlight bookkeeping names objects that no longer exist.
            HighlightStates.Clear();
            HighlightedCoords = null;
            MoveRangeHighlights.Clear();
        }

        // Index of the "Tiles" layer, or -1 if it does not exist in the project.
        private int TileLayer
        {
            get
            {
                if (!CachedTileLayer.HasValue)
                {
                    CachedTileLayer = LayerMask.NameToLayer(TileLayerName);

                    if (CachedTileLayer.Value < 0)
                    {
                        Debug.LogWarning(
                            $"{nameof(GridManager)}: layer '{TileLayerName}' does not exist. Add it in " +
                            "Project Settings > Tags and Layers. Hover detection will fall back to all " +
                            "layers, so unit colliders will block tile hover.",
                            this);
                    }
                }

                return CachedTileLayer.Value;
            }
        }

        // Children too: a prefab whose collider sits on a child would otherwise stay off the mask
        // and be invisible to the hover raycast.
        private static void SetLayerRecursively(GameObject target, int layer)
        {
            target.layer = layer;

            foreach (Transform child in target.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        // The spawned cube for a coordinate, or null if it has no visual.
        public GameObject GetTileObject(Vector2Int coords)
        {
            return SpawnedTiles.TryGetValue(coords, out GameObject tileObject) ? tileObject : null;
        }

        // ---------------------------------------------------------------------
        // Cursor hover and highlighting
        // ---------------------------------------------------------------------

        // True if the cursor is over a spawned tile, with coords set to it.
        public bool GetTileUnderCursor(Camera cam, out Vector2Int coords)
        {
            coords = default;

            if (cam == null || !GridInput.TryGetMouseScreenPosition(out Vector3 screenPosition))
            {
                return false;
            }

            Ray ray = cam.ScreenPointToRay(screenPosition);

            // Restricting to the Tiles layer makes unit colliders (and everything else) invisible
            // to this ray, so hovering a tile still works when a unit is standing on it.
            // Guard the shift: 1 << -1 would wrongly resolve to layer 31, since C# masks shift
            // counts to 5 bits. Missing layer falls back to hitting everything, which is the old
            // behaviour rather than hovering nothing at all.
            int layerMask = TileLayer >= 0 ? 1 << TileLayer : Physics.DefaultRaycastLayers;

            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, layerMask))
            {
                return false;
            }

            GameObject hitObject = hit.collider.gameObject;

            if (ObjectToCoords.TryGetValue(hitObject, out coords))
            {
                return true;
            }

            // Walk up for prefabs whose collider sits on a child of the instantiated root.
            Transform root = hitObject.transform.parent;
            while (root != null)
            {
                if (ObjectToCoords.TryGetValue(root.gameObject, out coords))
                {
                    return true;
                }

                root = root.parent;
            }

            return false;
        }

        public TileHighlightState GetTileHighlightState(Vector2Int coords)
        {
            return HighlightStates.TryGetValue(coords, out TileHighlightState state) ? state : TileHighlightState.None;
        }

        // Sets or clears one layer, leaving the others untouched. This is what stops hover and
        // move-range from overwriting each other.
        public void SetTileHighlight(Vector2Int coords, TileHighlightState layer, bool enabled)
        {
            TileHighlightState state = GetTileHighlightState(coords);
            TileHighlightState updated = enabled ? state | layer : state & ~layer;

            if (updated == state)
            {
                return;
            }

            if (updated == TileHighlightState.None)
            {
                HighlightStates.Remove(coords);
            }
            else
            {
                HighlightStates[coords] = updated;
            }

            ApplyHighlightVisual(coords);
        }

        // The single place that decides what a tile looks like. Precedence lives here rather than
        // in whoever wrote last, so the order the two systems run in stops mattering.
        private void ApplyHighlightVisual(Vector2Int coords)
        {
            if (!SpawnedTiles.TryGetValue(coords, out GameObject tileObject) || tileObject == null)
            {
                return;
            }

            TileHighlightState state = GetTileHighlightState(coords);
            Material material = BaseMaterial;

            // Bitwise rather than HasFlag - clearer intent for flags, and no boxing.
            if ((state & TileHighlightState.Hovered) != 0 && HighlightMaterial != null)
            {
                material = HighlightMaterial;
            }
            else if ((state & TileHighlightState.InMoveRange) != 0 && RangeMaterial != null)
            {
                material = RangeMaterial;
            }

            if (material != null)
            {
                ApplyMaterial(tileObject, material);
            }
        }

        // Call once per frame. Moves the hover highlight to the tile under the cursor.
        public void UpdateCursorHighlight(Camera cam)
        {
            Vector2Int? currentCoords = GetTileUnderCursor(cam, out Vector2Int hitCoords)
                ? hitCoords
                : (Vector2Int?)null;

            // Nullable == covers moved-between / moved-on / moved-off / didn't-move in one test,
            // and stops us reassigning materials every frame the cursor sits still.
            if (currentCoords == HighlightedCoords)
            {
                return;
            }

            // Only the Hovered layer is touched; a tile's InMoveRange bit survives untouched.
            if (HighlightedCoords.HasValue)
            {
                SetTileHighlight(HighlightedCoords.Value, TileHighlightState.Hovered, false);
            }

            if (currentCoords.HasValue)
            {
                SetTileHighlight(currentCoords.Value, TileHighlightState.Hovered, true);
            }

            HighlightedCoords = currentCoords;
        }

        public void ClearCursorHighlight()
        {
            if (HighlightedCoords.HasValue)
            {
                SetTileHighlight(HighlightedCoords.Value, TileHighlightState.Hovered, false);
                HighlightedCoords = null;
            }
        }

        // sharedMaterial, not material: reading Renderer.material clones per object and leaks one
        // material per tile. Assigning sharedMaterial is safe; mutating one edits the asset itself.
        private static void ApplyMaterial(GameObject tileObject, Material material)
        {
            if (tileObject.TryGetComponent(out Renderer tileRenderer))
            {
                tileRenderer.sharedMaterial = material;
            }
        }

        // ---------------------------------------------------------------------
        // Coordinate conversion
        // ---------------------------------------------------------------------

        // Grid X -> world X, grid Y -> world Z. Unity is Y-up (UE5 is Z-up).
        // Unknown coordinates are treated as Height 0 rather than throwing.
        public Vector3 GridToWorldPosition(Vector2Int gridCoords)
        {
            int height = GridTiles.TryGetValue(gridCoords, out GridTile tile) ? tile.Height : 0;

            return new Vector3(
                gridCoords.x * tileSize,
                height * HeightStep,
                gridCoords.y * tileSize) + transform.position;
        }

        // Y is ignored; elevation is tile data, not something recovered from a world position.
        // The result is not guaranteed to exist - check HasTile.
        public Vector2Int WorldToGridPosition(Vector3 worldPos)
        {
            Vector3 local = worldPos - transform.position;

            // RoundToInt, not FloorToInt: GridToWorldPosition places the tile *centre* at
            // coord * TileSize, and the two must agree or they stop being inverses.
            return new Vector2Int(
                Mathf.RoundToInt(local.x / tileSize),
                Mathf.RoundToInt(local.z / tileSize));
        }

        // ---------------------------------------------------------------------
        // Movement range
        // ---------------------------------------------------------------------

        // Orthogonal only - standard tactics movement, and it keeps every edge cost at 1.
        private static readonly Vector2Int[] OrthogonalDirections =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1),
        };

        // Flood-fills from startCoords and returns every tile reachable in at most moveRange steps,
        // excluding startCoords. Results come back ordered by increasing distance.
        //
        // BFS is correct here only because every step costs exactly 1, so first arrival at a tile
        // is guaranteed to be the shortest path. Add variable terrain cost or diagonals and this
        // needs to become Dijkstra (priority queue, revisit on a cheaper route).
        public List<Vector2Int> GetTilesInMoveRange(Vector2Int startCoords, int moveRange)
        {
            List<Vector2Int> reachable = new List<Vector2Int>();

            if (moveRange <= 0 || !GridTiles.ContainsKey(startCoords))
            {
                return reachable;
            }

            // Seeding visited with the start tile also implements "occupied tiles block, unless
            // it's the start" - the moving unit occupies the start, which is never re-tested.
            HashSet<Vector2Int> visited = new HashSet<Vector2Int> { startCoords };

            Queue<(Vector2Int Coords, int Distance)> frontier = new Queue<(Vector2Int, int)>();
            frontier.Enqueue((startCoords, 0));

            while (frontier.Count > 0)
            {
                (Vector2Int currentCoords, int distance) = frontier.Dequeue();

                if (distance >= moveRange)
                {
                    continue;
                }

                GridTile currentTile = GridTiles[currentCoords];

                foreach (Vector2Int direction in OrthogonalDirections)
                {
                    Vector2Int neighbourCoords = currentCoords + direction;

                    if (visited.Contains(neighbourCoords))
                    {
                        continue;
                    }

                    // Missing key = off the map or a hole in it; same case with a Dictionary.
                    if (!GridTiles.TryGetValue(neighbourCoords, out GridTile neighbourTile))
                    {
                        continue;
                    }

                    if (!neighbourTile.IsWalkable)
                    {
                        continue;
                    }

                    // Blocks allies too - a design decision, not a technical one.
                    if (neighbourTile.Occupant != null)
                    {
                        continue;
                    }

                    // Depends on where we step *from*, unlike the checks above. This is why
                    // rejected tiles must NOT be marked visited: a tile behind a cliff on this
                    // side may still be reachable via a gentler slope elsewhere.
                    if (Mathf.Abs(neighbourTile.Height - currentTile.Height) > MaxStepHeight)
                    {
                        continue;
                    }

                    visited.Add(neighbourCoords);
                    reachable.Add(neighbourCoords);
                    frontier.Enqueue((neighbourCoords, distance + 1));
                }
            }

            return reachable;
        }

        // Highlights the unit's move range, replacing any previously shown range.
        // Touches only the InMoveRange layer, so an active hover highlight is preserved.
        public void ShowMoveRangeForUnit(GridUnit unit)
        {
            if (unit == null)
            {
                Debug.LogWarning($"{nameof(GridManager)}: {nameof(ShowMoveRangeForUnit)} called with a null unit.", this);
                return;
            }

            ClearMoveRangeHighlight();

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.MoveRange);

            foreach (Vector2Int coords in tilesInRange)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, true);
                MoveRangeHighlights.Add(coords);
            }
        }

        public void ClearMoveRangeHighlight()
        {
            foreach (Vector2Int coords in MoveRangeHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, false);
            }

            MoveRangeHighlights.Clear();
        }

        // Moves the unit if targetCoords is within its current move range. No side effects if not.
        //
        // Recomputes the range rather than trusting MoveRangeHighlights: that list is a record of
        // what is *displayed*, and the grid may have changed since it was drawn (another unit
        // moved, a tile became unwalkable), which would let an illegal move through. Recomputing
        // is O(tiles in range) per click - free at this scale. Cache it only if a profiler says so,
        // and then invalidate it on any grid mutation.
        public bool TryMoveUnitToTile(GridUnit unit, Vector2Int targetCoords)
        {
            if (unit == null)
            {
                return false;
            }

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.MoveRange);

            if (!tilesInRange.Contains(targetCoords))
            {
                return false;
            }

            SetOccupant(unit.GridCoords, null);
            SetOccupant(targetCoords, unit.gameObject);

            // Sets the coordinate and snaps the transform in one step.
            unit.SetGridCoords(targetCoords);

            ClearMoveRangeHighlight();
            return true;
        }

        // TEMPORARY TEST WIRING - REMOVE with the TestUnit field.
        // Stands in for a real "select unit -> see range -> click to move" flow. Re-shows the
        // range from the new position so the display stays live between clicks.
        public bool TryMoveTestUnitTo(Vector2Int targetCoords)
        {
            if (TestUnit == null || !TryMoveUnitToTile(TestUnit, targetCoords))
            {
                return false;
            }

            ShowMoveRangeForUnit(TestUnit);
            return true;
        }

        // ---------------------------------------------------------------------
        // Convenience accessors. These hide the struct-copy write-back described in GridTile.
        // ---------------------------------------------------------------------

        public bool HasTile(Vector2Int coords) => GridTiles.ContainsKey(coords);

        public bool TryGetTile(Vector2Int coords, out GridTile tile) => GridTiles.TryGetValue(coords, out tile);

        // These setters return false if no tile exists at the given coordinates.

        public bool SetOccupant(Vector2Int coords, GameObject occupant)
        {
            if (!GridTiles.TryGetValue(coords, out GridTile tile))
            {
                return false;
            }

            tile.Occupant = occupant;   // mutates the local copy...
            GridTiles[coords] = tile;   // ...so it must be written back
            return true;
        }

        public bool SetHeight(Vector2Int coords, int height)
        {
            if (!GridTiles.TryGetValue(coords, out GridTile tile))
            {
                return false;
            }

            tile.Height = height;
            GridTiles[coords] = tile;
            return true;
        }

        public bool SetWalkable(Vector2Int coords, bool isWalkable)
        {
            if (!GridTiles.TryGetValue(coords, out GridTile tile))
            {
                return false;
            }

            tile.IsWalkable = isWalkable;
            GridTiles[coords] = tile;
            return true;
        }
    }
}
