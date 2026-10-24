using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Tactica.Grid
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup - at file scope it would be checked too late.
    using Camera = UnityEngine.Camera;

    // Grid data layer: tile storage, generation, coordinate conversion, pathfinding, and the
    // visuals derived from them.
    //
    // Knows nothing about combat. Turn order, the active unit and the combat state gate all live
    // in PlayerActionController, which drives this rather than being consulted by it - that is what
    // keeps Tactica.Grid free of a Tactica.Combat reference and breaks half the old namespace cycle.
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
        [FormerlySerializedAs("HeightStep")]
        [SerializeField] private float heightStep = 1.0f;

        public float HeightStep => heightStep;

        [Header("Rendering")]
        [Tooltip("Cube prefab instantiated once per tile. Leave empty to keep the grid data-only.")]
        [FormerlySerializedAs("TilePrefab")]
        [SerializeField] private GameObject tilePrefab;

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
        [FormerlySerializedAs("BaseMaterial")]
        [SerializeField] private Material baseMaterial;

        [Tooltip("Hover colour. Takes priority over rangeMaterial when both apply.")]
        [FormerlySerializedAs("HighlightMaterial")]
        [SerializeField] private Material highlightMaterial;

        [Tooltip("Move-range colour.")]
        [FormerlySerializedAs("RangeMaterial")]
        [SerializeField] private Material rangeMaterial;

        [Tooltip("Valid ability target colour. Should read clearly against rangeMaterial - a tile " +
                 "can be both reachable and attackable, and this wins.")]
        [SerializeField] private Material attackableMaterial;

        [Header("Movement")]
        // Placeholder for tuning: will likely become per-unit, with separate up/down limits.
        [Tooltip("Maximum absolute height difference a unit may traverse in one step.")]
        [FormerlySerializedAs("MaxStepHeight")]
        [SerializeField] private int maxStepHeight = 1;

        // Must exist in Project Settings > Tags and Layers. Layers cannot be created from a
        // script, only read by name - so this resolves to -1 if someone deletes it.
        private const string TileLayerName = "Tiles";

        // Cached: NameToLayer is a string lookup, and this keeps the missing-layer warning to one
        // message instead of one per tile.
        private int? cachedTileLayer;

        // Spawned visuals, keyed like GridTiles. Kept parallel to the data rather than as a field
        // on GridTile, so the data layer holds no scene references.
        private readonly Dictionary<Vector2Int, GameObject> spawnedTiles = new Dictionary<Vector2Int, GameObject>();

        // Reverse lookup for raycast hits, which hand back a GameObject. Dictionary has no
        // value->key search, and scanning spawnedTiles every frame would be O(n).
        // Written and cleared together with spawnedTiles.
        private readonly Dictionary<GameObject, Vector2Int> objectToCoords = new Dictionary<GameObject, Vector2Int>();

        // Which highlight layers each tile currently carries. Absent key means None.
        private readonly Dictionary<Vector2Int, TileHighlightState> highlightStates = new Dictionary<Vector2Int, TileHighlightState>();

        // Nullable because Vector2Int is a struct with no spare sentinel value - (0,0) and (-1,-1)
        // are both valid coordinates.
        private Vector2Int? highlightedCoords;

        // Exactly the tiles lit by the last ShowMoveRangeForUnit call. Recorded rather than
        // recomputed at clear time, since the grid may have changed in between.
        private readonly List<Vector2Int> moveRangeHighlights = new List<Vector2Int>();

        // Same record-what-was-lit approach as moveRangeHighlights, for the same reason: the grid
        // may change between showing and clearing, so recomputing would not match what is on screen.
        private readonly List<Vector2Int> attackableHighlights = new List<Vector2Int>();

        // Raised after BuildMap or ResetToDefaultMap replaces the board, once the new tiles are
        // drawn. The camera reframes on it; anything caching tile data should drop it here.
        public event System.Action GridRebuilt;

        // Highest tile Height on the board. Lets the camera lift its focus for hilly maps.
        public int MaxTileHeight { get; private set; }

        // Gap between neighbouring tiles as a fraction of tileSize, so each tile reads as its own
        // square - especially on terraces, where columns would otherwise merge into one slope.
        private const float TileGapFraction = 0.04f;

        // The scene's authored dimensions, kept so a generated map can be undone.
        private int defaultWidth;
        private int defaultDepth;
        private float defaultHeightStep;

        // Awake -> data, Start -> visuals. Every Awake runs before any Start, so other scripts can
        // reshape the grid in their Awake and the visuals will reflect it.
        private void Awake()
        {
            defaultWidth = gridWidth;
            defaultDepth = gridDepth;
            defaultHeightStep = heightStep;

            GenerateGrid();
        }

        // Replaces the board with a generated map and redraws it. The caller must have cleared
        // units off the old board first - occupancy does not carry over, because the tiles it
        // pointed at no longer exist.
        public void BuildMap(MapSettings settings)
        {
            if (!settings.IsDefined)
            {
                ResetToDefaultMap();
                return;
            }

            gridWidth = settings.width;
            gridDepth = settings.depth;
            heightStep = settings.heightStep > 0f ? settings.heightStep : defaultHeightStep;

            GridTiles = MapGenerator.Generate(settings);
            FinishRebuild();
        }

        // Back to the scene's flat rectangle.
        public void ResetToDefaultMap()
        {
            gridWidth = defaultWidth;
            gridDepth = defaultDepth;
            heightStep = defaultHeightStep;

            GenerateGrid();
            FinishRebuild();
        }

        private void FinishRebuild()
        {
            MaxTileHeight = 0;

            foreach (GridTile tile in GridTiles.Values)
            {
                MaxTileHeight = Mathf.Max(MaxTileHeight, tile.Height);
            }

            RenderGrid();
            GridRebuilt?.Invoke();
        }

        private void Start()
        {
            RenderGrid();
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

        // Spawns one tilePrefab per tile, clearing any previous set first.
        // One GameObject per tile is fine at this scale and gives raycast picking for free.
        // Graphics.RenderMeshInstanced would be the move at ~10,000 tiles; the swap is local here.
        [ContextMenu("Render Grid")]
        public void RenderGrid()
        {
            if (tilePrefab == null)
            {
                Debug.LogWarning($"{nameof(GridManager)}: no {nameof(tilePrefab)} assigned, nothing to render.", this);
                return;
            }

            ClearRenderedTiles();

            // Load-bearing for hover detection: Physics.Raycast only sees colliders. Unity's Cube
            // primitive has a BoxCollider already; a prefab without one renders fine and is
            // silently un-hoverable.
            if (tilePrefab.GetComponentInChildren<Collider>() == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridManager)}: {nameof(tilePrefab)} '{tilePrefab.name}' has no Collider. " +
                    "Tiles will render but cursor hover detection will not work. Add a BoxCollider to the prefab.",
                    this);
            }

            foreach (KeyValuePair<Vector2Int, GridTile> entry in GridTiles)
            {
                Vector2Int coords = entry.Key;

                // Each tile is a COLUMN from the ground slab up to its surface, so raised terrain
                // reads as solid ground rather than floating slabs. The surface stays exactly where
                // it always was (GridToWorldPosition + TileSurfaceOffset), which is what units snap
                // to - only the bottom of the column moves.
                float rise = entry.Value.Height * heightStep;
                Vector3 position = GridToWorldPosition(coords) - new Vector3(0f, rise * 0.5f, 0f);

                // Parenting via the Instantiate argument avoids a redundant transform recalculation.
                GameObject tileObject = Instantiate(tilePrefab, position, Quaternion.identity, transform);

                tileObject.name = $"Tile_{coords.x}_{coords.y}";

                // Puts the tile on the layer GetTileUnderCursor filters for, so units standing on
                // it are transparent to hover detection.
                if (TileLayer >= 0)
                {
                    SetLayerRecursively(tileObject, TileLayer);
                }

                // Default cube is 1x1x1, so localScale is effectively size in metres: a thin slab
                // plus the column's rise, slightly narrower than tileSize to leave a visible seam.
                float footprint = tileSize * (1f - TileGapFraction);
                tileObject.transform.localScale = new Vector3(footprint, tileVisualHeight + rise, footprint);

                spawnedTiles[coords] = tileObject;
                objectToCoords[tileObject] = coords;

                // Registered first so the visual resolver can find the object. State is None here
                // (ClearRenderedTiles wiped it), so this applies baseMaterial.
                ApplyHighlightVisual(coords);
            }
        }

        [ContextMenu("Clear Rendered Tiles")]
        public void ClearRenderedTiles()
        {
            foreach (GameObject tileObject in spawnedTiles.Values)
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

            spawnedTiles.Clear();
            objectToCoords.Clear();

            // All highlight bookkeeping names objects that no longer exist.
            highlightStates.Clear();
            highlightedCoords = null;
            moveRangeHighlights.Clear();
            attackableHighlights.Clear();
        }

        // Index of the "Tiles" layer, or -1 if it does not exist in the project.
        private int TileLayer
        {
            get
            {
                if (!cachedTileLayer.HasValue)
                {
                    cachedTileLayer = LayerMask.NameToLayer(TileLayerName);

                    if (cachedTileLayer.Value < 0)
                    {
                        Debug.LogWarning(
                            $"{nameof(GridManager)}: layer '{TileLayerName}' does not exist. Add it in " +
                            "Project Settings > Tags and Layers. Hover detection will fall back to all " +
                            "layers, so unit colliders will block tile hover.",
                            this);
                    }
                }

                return cachedTileLayer.Value;
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
        // INTENTIONAL API: unused today, for selection outlines and per-tile VFX attachment.
        public GameObject GetTileObject(Vector2Int coords)
        {
            return spawnedTiles.TryGetValue(coords, out GameObject tileObject) ? tileObject : null;
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

            if (objectToCoords.TryGetValue(hitObject, out coords))
            {
                return true;
            }

            // Walk up for prefabs whose collider sits on a child of the instantiated root.
            Transform root = hitObject.transform.parent;
            while (root != null)
            {
                if (objectToCoords.TryGetValue(root.gameObject, out coords))
                {
                    return true;
                }

                root = root.parent;
            }

            return false;
        }

        public TileHighlightState GetTileHighlightState(Vector2Int coords)
        {
            return highlightStates.TryGetValue(coords, out TileHighlightState state) ? state : TileHighlightState.None;
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
                highlightStates.Remove(coords);
            }
            else
            {
                highlightStates[coords] = updated;
            }

            ApplyHighlightVisual(coords);
        }

        // The single place that decides what a tile looks like. Precedence lives here rather than
        // in whoever wrote last, so the order the two systems run in stops mattering.
        private void ApplyHighlightVisual(Vector2Int coords)
        {
            if (!spawnedTiles.TryGetValue(coords, out GameObject tileObject) || tileObject == null)
            {
                return;
            }

            TileHighlightState state = GetTileHighlightState(coords);
            Material material = baseMaterial;

            // Bitwise rather than HasFlag - clearer intent for flags, and no boxing.
            if ((state & TileHighlightState.Hovered) != 0 && highlightMaterial != null)
            {
                material = highlightMaterial;
            }
            // Attackable outranks InMoveRange: when a tile is both, "I can hit this" is the more
            // urgent fact, and the move range is already legible from the tiles around it.
            else if ((state & TileHighlightState.Attackable) != 0 && attackableMaterial != null)
            {
                material = attackableMaterial;
            }
            else if ((state & TileHighlightState.InMoveRange) != 0 && rangeMaterial != null)
            {
                material = rangeMaterial;
            }

            if (material != null)
            {
                ApplyMaterial(tileObject, material);
            }

            ApplyHeightTint(tileObject, coords, material == baseMaterial);
        }

        // Low ground dark slate, high ground a pale neon blue. Without it every column renders the
        // same near-black and terraces, cliffs and cover are unreadable from the tactics camera.
        private static readonly Color LowGroundColor = new Color(0.08f, 0.09f, 0.13f);
        private static readonly Color HighGroundColor = new Color(0.28f, 0.42f, 0.58f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private MaterialPropertyBlock tintBlock;

        // A property block rather than a material per height: it tints without cloning materials,
        // and is removed while a highlight shows so the highlight colour reads unchanged.
        private void ApplyHeightTint(GameObject tileObject, Vector2Int coords, bool tint)
        {
            if (!tileObject.TryGetComponent(out Renderer tileRenderer))
            {
                return;
            }

            if (!tint)
            {
                tileRenderer.SetPropertyBlock(null);
                return;
            }

            int height = GridTiles.TryGetValue(coords, out GridTile tile) ? tile.Height : 0;
            float t = MaxTileHeight > 0 ? (float)height / MaxTileHeight : 0f;
            Color color = Color.Lerp(LowGroundColor, HighGroundColor, t);

            tintBlock ??= new MaterialPropertyBlock();
            tintBlock.SetColor(BaseColorId, color);
            tintBlock.SetColor(ColorId, color);
            tileRenderer.SetPropertyBlock(tintBlock);
        }

        // Moves the hover highlight to the given tile, or clears it when null.
        //
        // Takes an already-resolved coordinate rather than a Camera so the caller can raycast once
        // per frame and feed the result to every consumer. Previously this raycast internally,
        // which meant hover, click handling and hover logging each fired their own.
        public void SetHoveredTile(Vector2Int? currentCoords)
        {
            // Nullable == covers moved-between / moved-on / moved-off / didn't-move in one test,
            // and stops us reassigning materials every frame the cursor sits still.
            if (currentCoords == highlightedCoords)
            {
                return;
            }

            // Only the Hovered layer is touched; a tile's InMoveRange bit survives untouched.
            if (highlightedCoords.HasValue)
            {
                SetTileHighlight(highlightedCoords.Value, TileHighlightState.Hovered, false);
            }

            if (currentCoords.HasValue)
            {
                SetTileHighlight(currentCoords.Value, TileHighlightState.Hovered, true);
            }

            highlightedCoords = currentCoords;
        }

        public void ClearCursorHighlight()
        {
            if (highlightedCoords.HasValue)
            {
                SetTileHighlight(highlightedCoords.Value, TileHighlightState.Hovered, false);
                highlightedCoords = null;
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
                height * heightStep,
                gridCoords.y * tileSize) + transform.position;
        }

        // Y is ignored; elevation is tile data, not something recovered from a world position.
        // The result is not guaranteed to exist - check HasTile.
        // INTENTIONAL API: unused today. The picking path if tiles ever move to
        // Graphics.RenderMeshInstanced and lose their colliders - raycast a plane, convert here.
        public Vector2Int WorldToGridPosition(Vector3 worldPos)
        {
            Vector3 local = worldPos - transform.position;

            // RoundToInt, not FloorToInt: GridToWorldPosition places the tile *centre* at
            // coord * TileSize, and the two must agree or they stop being inverses.
            return new Vector2Int(
                Mathf.RoundToInt(local.x / tileSize),
                Mathf.RoundToInt(local.z / tileSize));
        }

        // Manhattan step distance: orthogonal steps between two coordinates, ignoring terrain.
        // FFT's metric for both movement and range - areas are diamonds, not squares.
        //
        // This replaced Chebyshev distance plus a "one diagonal per path" movement rule. That pair
        // spoke two languages (a unit could reach (2,2) for range 2 but walk there for 3), and
        // diagonal travel stayed strictly better than orthogonal, so optimal play drifted onto
        // diagonals. With 4-directional steps, movement and range are measured the same way and
        // no direction is cheaper than another.
        //
        // Still DIFFERENT FROM MOVEMENT in one respect: this ignores walls, height and occupancy.
        // Movement is a path and pays for the ground it covers; targeting is proximity - an arrow
        // does not walk, and a target always stands on an occupied tile, which the flood fill would
        // exclude. So a unit can still hit a tile it cannot step onto this turn.
        //
        // Static because it is pure coordinate arithmetic with no grid state.
        public static int GetGridDistance(Vector2Int a, Vector2Int b)
        {
            return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);
        }

        // ---------------------------------------------------------------------
        // Movement range
        // ---------------------------------------------------------------------

        // The four orthogonal neighbours. Every step costs 1, which is what keeps the search a
        // plain BFS. Weighted terrain (mud, stairs) would swap the Queue for a priority queue
        // without restructuring anything else.
        private static readonly Vector2Int[] NeighbourDirections =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1),
        };

        // Flood-fills from startCoords and returns every tile reachable in at most moveRange
        // orthogonal steps, excluding startCoords. Results come back ordered by increasing
        // distance - EnemyAIController relies on that to pick the cheapest tile first.
        //
        // maxStepHeightOverride replaces the grid-wide maxStepHeight for one query; callers pass
        // the unit's EffectiveStats.Jump, so climbing is a per-job ability as in FFT. Nullable
        // because C# optional-parameter defaults must be compile-time constants.
        public List<Vector2Int> GetTilesInMoveRange(Vector2Int startCoords, int moveRange, int? maxStepHeightOverride = null)
        {
            List<Vector2Int> reachable = new List<Vector2Int>();

            if (moveRange <= 0 || !GridTiles.ContainsKey(startCoords))
            {
                return reachable;
            }

            int stepLimit = maxStepHeightOverride ?? maxStepHeight;

            // Marked only on acceptance, never on rejection: a tile refused for height from one
            // side may still be reachable via a gentler slope elsewhere.
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

                foreach (Vector2Int direction in NeighbourDirections)
                {
                    Vector2Int neighbourCoords = currentCoords + direction;

                    if (visited.Contains(neighbourCoords))
                    {
                        continue;
                    }

                    // Missing key = off the map or a hole in it.
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

                    if (Mathf.Abs(neighbourTile.Height - currentTile.Height) > stepLimit)
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

        // Walking distance from `origin` to every tile reachable from it, ignoring occupants.
        //
        // For the AI's approach: "how many steps would it take to get there" around holes, cover
        // and cliffs - which straight-line distance cannot see. Occupants are ignored because units
        // move between turns; treating them as walls would make a crowd look like a dead end.
        // Height still applies, with the same symmetric step rule as movement.
        public Dictionary<Vector2Int, int> GetWalkDistances(Vector2Int origin, int stepLimit)
        {
            Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();

            if (!GridTiles.ContainsKey(origin))
            {
                return distances;
            }

            distances[origin] = 0;
            Queue<Vector2Int> frontier = new Queue<Vector2Int>();
            frontier.Enqueue(origin);

            while (frontier.Count > 0)
            {
                Vector2Int current = frontier.Dequeue();
                int height = GridTiles[current].Height;

                foreach (Vector2Int direction in NeighbourDirections)
                {
                    Vector2Int next = current + direction;

                    if (distances.ContainsKey(next) || !GridTiles.TryGetValue(next, out GridTile tile))
                    {
                        continue;
                    }

                    if (!tile.IsWalkable || Mathf.Abs(tile.Height - height) > stepLimit)
                    {
                        continue;
                    }

                    distances[next] = distances[current] + 1;
                    frontier.Enqueue(next);
                }
            }

            return distances;
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

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.EffectiveStats.Move, unit.EffectiveStats.Jump);

            foreach (Vector2Int coords in tilesInRange)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, true);
                moveRangeHighlights.Add(coords);
            }
        }

        // Lights the given tiles as ability targets, replacing any previous set.
        // Touches only the Attackable layer, so hover and move range survive underneath.
        public void ShowAttackableTiles(IEnumerable<Vector2Int> coords)
        {
            ClearAttackableHighlights();

            if (coords == null)
            {
                return;
            }

            foreach (Vector2Int tile in coords)
            {
                SetTileHighlight(tile, TileHighlightState.Attackable, true);
                attackableHighlights.Add(tile);
            }
        }

        public void ClearAttackableHighlights()
        {
            foreach (Vector2Int coords in attackableHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.Attackable, false);
            }

            attackableHighlights.Clear();
        }

        public void ClearMoveRangeHighlight()
        {
            foreach (Vector2Int coords in moveRangeHighlights)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, false);
            }

            moveRangeHighlights.Clear();
        }

        // Moves the unit if targetCoords is within its current move range. No side effects if not.
        //
        // The caller supplies the unit rather than this resolving the turn owner itself. There used
        // to be a TryMoveActiveUnitTo wrapper that took a bare coordinate and re-derived the unit
        // and the combat-state gate - both of which PlayerActionController had already done before
        // calling it. Resolving twice meant the two could in principle disagree, and the unit that
        // got MarkMoved() was not provably the one that moved.
        //
        // Recomputes the range rather than trusting moveRangeHighlights: that list is a record of
        // what is *displayed*, and the grid may have changed since it was drawn (another unit
        // moved, a tile became unwalkable), which would let an illegal move through. Recomputing
        // is O(tiles in range) per click - free at this scale. Cache it only if a profiler says so,
        // and then invalidate it on any grid mutation.
        //
        // Deliberately does not end the turn. Moving and ending a turn are separate player actions
        // - move then attack, or move then wait - so the player decides when the turn is over.
        public bool TryMoveUnitToTile(GridUnit unit, Vector2Int targetCoords)
        {
            if (unit == null)
            {
                return false;
            }

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.EffectiveStats.Move, unit.EffectiveStats.Jump);

            if (!tilesInRange.Contains(targetCoords))
            {
                return false;
            }

            Vector2Int from = unit.GridCoords;

            SetOccupant(from, null);
            SetOccupant(targetCoords, unit.gameObject);

            // Sets the coordinate and snaps the transform in one step.
            unit.SetGridCoords(targetCoords);

            // Ends the move looking the way it travelled, as a walking unit would. Acting turns it
            // again toward its target (ActionResolver), so a unit that moves past an enemy and
            // strikes back faces the right way.
            unit.SetFacing(FacingUtility.Towards(from, targetCoords, unit.CurrentFacing));

            // Clear rather than redraw from the new position. Movement is once per turn
            // (GridUnit.HasMovedThisTurn), so leaving a range lit would advertise a move the unit
            // can no longer make.
            ClearMoveRangeHighlight();
            return true;
        }

        // ---------------------------------------------------------------------
        // Convenience accessors. These hide the struct-copy write-back described in GridTile.
        // ---------------------------------------------------------------------

        // INTENTIONAL API: unused today, for validating coordinates from UI and map editing.
        public bool HasTile(Vector2Int coords) => GridTiles.ContainsKey(coords);

        // The read path for ability targeting - PlayerActionController.GetUnitAt resolves occupants
        // through this.
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

        // INTENTIONAL API: unused today, for terrain authoring and destructible/raising tiles.
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

        // INTENTIONAL API: unused today, for collapsing bridges and map-editing tools.
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
