using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Combat;

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
        [FormerlySerializedAs("HeightStep")]
        [SerializeField] private float heightStep = 1.0f;

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

        [Header("Movement")]
        // Placeholder for tuning: will likely become per-unit, with separate up/down limits.
        [Tooltip("Maximum absolute height difference a unit may traverse in one step.")]
        [FormerlySerializedAs("MaxStepHeight")]
        [SerializeField] private int maxStepHeight = 1;

        [Header("Combat")]
        [Tooltip("Source of the current turn owner. If unset, click-to-move falls back to " +
                 "testUnit and turn order is not enforced.")]
        [FormerlySerializedAs("CombatManagerRef")]
        [SerializeField] private CombatManager combatManagerRef;

        // TEMPORARY TEST WIRING - REMOVE once combat is always present in a scene.
        [Header("Debug (temporary)")]
        [Tooltip("TEMPORARY fallback for testing without a CombatManager. Its range is shown on " +
                 "Start, and it receives clicks when combatManagerRef is unset.")]
        [FormerlySerializedAs("TestUnit")]
        [SerializeField] private GridUnit testUnit;

        // Keeps the fallback warning to one message instead of one per click.
        private bool warnedAboutMissingCombatManager;

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

        // Awake -> data, Start -> visuals. Every Awake runs before any Start, so other scripts can
        // reshape the grid in their Awake and the visuals will reflect it.
        private void Awake()
        {
            GenerateGrid();
        }

        // OnEnable/OnDisable rather than Awake/OnDestroy: symmetric, so toggling this component off
        // and on re-subscribes exactly once, and OnDisable runs on the way to destruction anyway.
        // Subscribing in Awake and never unsubscribing would keep this object alive through the
        // event's delegate list after a scene change - the usual C# event leak in Unity.
        private void OnEnable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnActiveUnitChanged += HandleActiveUnitChanged;
            }
        }

        private void OnDisable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnActiveUnitChanged -= HandleActiveUnitChanged;
            }
        }

        private void Start()
        {
            RenderGrid();

            // Only when nothing else drives the display. With a CombatManager present,
            // OnActiveUnitChanged owns the highlight, and showing testUnit here would leave a
            // stale range on screen until the first turn change.
            //
            // Known limitation, confined to this branch: GridUnit claims its tile via SetOccupant
            // in its own Start, and two Starts have no defined order - so this range may be
            // computed before some units have marked their tiles occupied. Harmless with a single
            // unit (a unit's own tile is excluded from its range regardless); with several it can
            // briefly show a tile that is actually taken.
            //
            // The combat path does NOT have this problem: StartCombat is invoked manually, long
            // after every Start has run. So this dies entirely along with testUnit - there is
            // nothing to fix here, only something to delete.
            //
            // TEMPORARY - REMOVE with the testUnit field.
            if (combatManagerRef == null && testUnit != null)
            {
                ShowMoveRangeForUnit(testUnit);
            }
        }

        // Turn passed to someone else: retire the old range and draw the new one.
        // ShowMoveRangeForUnit already clears first, so the explicit clear is only needed for the
        // null case (combat failed to start, or ended).
        private void HandleActiveUnitChanged(GridUnit unit)
        {
            if (unit == null)
            {
                ClearMoveRangeHighlight();
                return;
            }

            ShowMoveRangeForUnit(unit);
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

                // Parenting via the Instantiate argument avoids a redundant transform recalculation.
                GameObject tileObject = Instantiate(tilePrefab, GridToWorldPosition(coords), Quaternion.identity, transform);

                tileObject.name = $"Tile_{coords.x}_{coords.y}";

                // Puts the tile on the layer GetTileUnderCursor filters for, so units standing on
                // it are transparent to hover detection.
                if (TileLayer >= 0)
                {
                    SetLayerRecursively(tileObject, TileLayer);
                }

                // Default cube is 1x1x1, so localScale is effectively size in metres. Y is squashed
                // flat; elevation already came through in GridToWorldPosition via heightStep.
                tileObject.transform.localScale = new Vector3(tileSize, tileVisualHeight, tileSize);

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
            else if ((state & TileHighlightState.InMoveRange) != 0 && rangeMaterial != null)
            {
                material = rangeMaterial;
            }

            if (material != null)
            {
                ApplyMaterial(tileObject, material);
            }
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

        // Chebyshev (chessboard) step distance: the number of 8-directional steps between two
        // coordinates, ignoring terrain entirely. A diagonal neighbour is distance 1, exactly like
        // an orthogonal one - max, not sum, is what makes that true.
        //
        // DELIBERATELY DIFFERENT FROM MOVEMENT. GetTilesInMoveRange allows only one diagonal step
        // per path, so walking to (2,2) costs 3 there while this reports distance 2. That is not
        // an inconsistency to reconcile - the two answer different questions:
        //
        //   - Movement is a path. A unit physically crosses every tile in between, so the route
        //     has to be legal and is charged for the ground it covers. The single-diagonal limit
        //     is what stops diagonal travel from being strictly better than orthogonal.
        //   - Targeting is proximity. An arrow does not walk. Nothing about the intervening tiles
        //     matters, so anything that made reach depend on a route would be wrong - see the
        //     occupied-tile trap noted below.
        //
        // The practical upshot for design: a unit can shoot a tile it cannot step onto this turn.
        // That is normal for the genre and worth keeping straight when tuning Range against Move.
        //
        // This is the right metric for ability range, and GetTilesInMoveRange is NOT - two
        // reasons, the second decisive:
        //   1. That method measures *path* distance, so a wall or a unit in the way inflates it.
        //      Reach should not care: you can shoot over a chasm you cannot walk across.
        //   2. It skips occupied tiles. A target is by definition standing on one, so a unit
        //      could never appear in its own targetable set. Reusing it would silently make
        //      every ability untargetable.
        //
        // Static because it is pure coordinate arithmetic with no grid state - callers do not
        // need a GridManager instance to ask.
        public static int GetGridDistance(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        // ---------------------------------------------------------------------
        // Movement range
        // ---------------------------------------------------------------------

        // All eight neighbours. Every step costs 1 whether it is orthogonal or diagonal, which is
        // what makes BFS below correct and what makes Chebyshev the matching distance metric.
        //
        // DELIBERATE SIMPLIFICATION: a diagonal step covers ~1.41 tiles of real ground, and games
        // that care about that charge for it - either a fractional cost, or the common integer
        // approximation of alternating 1 and 2 per diagonal. Both are rejected here on purpose.
        // Uniform cost keeps movement trivially predictable ("Move 4 means four steps in any
        // direction"), keeps ability range and movement range speaking the same language, and
        // keeps the search a plain BFS.
        //
        // The price, should it ever bite: a unit can cross a 4x4 square diagonally in 4 steps but
        // needs 4 to go straight across too, so diagonal travel is strictly better and optimal
        // play drifts toward moving on diagonals. If that becomes visible in playtesting, the fix
        // is weighted edges - which means swapping the Queue below for a priority queue, not
        // restructuring anything else.
        private static readonly Vector2Int[] NeighborDirections =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1),
            new Vector2Int(1, 1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1, -1),
        };

        // Flood-fills from startCoords and returns every tile reachable in at most moveRange steps,
        // excluding startCoords. Results come back ordered by increasing distance.
        //
        // MOVEMENT RULE: at most ONE diagonal step per path, as if a unit may cut a single corner
        // and must walk the rest. So the open-ground cost of a move is:
        //     dx + dy - 1   when both dx and dy are non-zero (the one diagonal saves a step)
        //     dx + dy       otherwise
        // A diagonal neighbour costs 1; one diagonal plus orthogonals costs 1 + the rest; a tile
        // two diagonals away costs the orthogonal price (3 for (2,2), not 2).
        //
        // This deliberately differs from GetGridDistance, which stays pure Chebyshev - see the
        // cross-reference there. Movement is a path and pays for the ground it covers; targeting
        // is proximity and does not.
        //
        // BFS is still correct because every step costs exactly 1. The search now runs over
        // (tile, diagonal-still-available) states rather than bare tiles, which keeps the cost
        // uniform - the diagonal limit is expressed as which edges exist, not as a price. Variable
        // cost is what would force Dijkstra; this is not that.
        //
        // maxStepHeightOverride replaces the grid-wide maxStepHeight for one query - pass a unit's
        // EffectiveStats.Jump to give it its own climbing ability.
        //
        // DELIBERATE HALF-STEP: the parameter exists but nothing passes it yet. Wiring Jump
        // through now would change every unit's reachable set at once with only one unit in the
        // scene to check it against, so a regression would look identical to correct behaviour.
        // Connect it when there is a second unit with a different Jump to compare.
        //
        // Nullable rather than "= maxStepHeight" because C# optional-parameter defaults must be
        // compile-time constants, and a serialised field is not one.
        public List<Vector2Int> GetTilesInMoveRange(Vector2Int startCoords, int moveRange, int? maxStepHeightOverride = null)
        {
            List<Vector2Int> reachable = new List<Vector2Int>();

            if (moveRange <= 0 || !GridTiles.ContainsKey(startCoords))
            {
                return reachable;
            }

            int stepLimit = maxStepHeightOverride ?? maxStepHeight;

            // Visited is now split by whether the path still has its diagonal in hand, because a
            // tile reached with the diagonal unspent is genuinely more useful than the same tile
            // reached with it spent - it can still cut a corner later. Collapsing both into one
            // set would let an early diagonal-spending path claim a tile and lock out a
            // same-length orthogonal path that arrives with the diagonal intact, wrongly shrinking
            // everything downstream.
            //
            // The two-set form encodes the dominance rule directly:
            //   - reaching a tile with the diagonal AVAILABLE dominates every state of that tile,
            //     so a later candidate of either kind is discarded;
            //   - reaching it with the diagonal SPENT only blocks later spent candidates; an
            //     available one is still worth exploring and gets its own entry.
            // BFS expands in nondecreasing distance, so anything already recorded arrived at a
            // distance no greater than the candidate's - which is what makes "already present"
            // sufficient to mean "dominated", with no distance comparison needed.
            //
            // As before, entries are added only on acceptance, never on rejection, so a tile
            // refused for height from one direction stays eligible from another.
            HashSet<Vector2Int> visitedWithDiagonalAvailable = new HashSet<Vector2Int> { startCoords };
            HashSet<Vector2Int> visitedWithDiagonalSpent = new HashSet<Vector2Int>();

            // Output de-duplication. A tile can legitimately be accepted twice (once per state);
            // it should still be listed once, at the first and therefore shortest arrival.
            HashSet<Vector2Int> listed = new HashSet<Vector2Int> { startCoords };

            Queue<(Vector2Int Coords, int Distance, bool UsedDiagonal)> frontier =
                new Queue<(Vector2Int, int, bool)>();

            frontier.Enqueue((startCoords, 0, false));

            while (frontier.Count > 0)
            {
                (Vector2Int currentCoords, int distance, bool usedDiagonal) = frontier.Dequeue();

                if (distance >= moveRange)
                {
                    continue;
                }

                GridTile currentTile = GridTiles[currentCoords];

                foreach (Vector2Int direction in NeighborDirections)
                {
                    bool isDiagonalStep = direction.x != 0 && direction.y != 0;

                    // The whole rule, in one line: a diagonal edge only exists for a path that has
                    // not spent one yet.
                    if (isDiagonalStep && usedDiagonal)
                    {
                        continue;
                    }

                    bool nextUsedDiagonal = usedDiagonal || isDiagonalStep;
                    Vector2Int neighbourCoords = currentCoords + direction;

                    if (visitedWithDiagonalAvailable.Contains(neighbourCoords))
                    {
                        continue;
                    }

                    if (nextUsedDiagonal && visitedWithDiagonalSpent.Contains(neighbourCoords))
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
                    if (Mathf.Abs(neighbourTile.Height - currentTile.Height) > stepLimit)
                    {
                        continue;
                    }

                    if (nextUsedDiagonal)
                    {
                        visitedWithDiagonalSpent.Add(neighbourCoords);
                    }
                    else
                    {
                        visitedWithDiagonalAvailable.Add(neighbourCoords);
                    }

                    if (listed.Add(neighbourCoords))
                    {
                        reachable.Add(neighbourCoords);
                    }

                    frontier.Enqueue((neighbourCoords, distance + 1, nextUsedDiagonal));
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

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.EffectiveStats.Move);

            foreach (Vector2Int coords in tilesInRange)
            {
                SetTileHighlight(coords, TileHighlightState.InMoveRange, true);
                moveRangeHighlights.Add(coords);
            }
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
        // Recomputes the range rather than trusting moveRangeHighlights: that list is a record of
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

            List<Vector2Int> tilesInRange = GetTilesInMoveRange(unit.GridCoords, unit.EffectiveStats.Move);

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

        // Moves whoever currently holds the turn onto targetCoords. Entry point for click input.
        //
        // Deliberately does not end the turn. Moving and ending a turn are separate player
        // actions - move then attack, or move then wait - so state stays WaitingForInput and the
        // player decides when the turn is over.
        public bool TryMoveActiveUnitTo(Vector2Int targetCoords)
        {
            if (!TryGetActiveUnit(out GridUnit unit))
            {
                return false;
            }

            // Gate on turn state, but only when there is a combat to ask. Anything other than
            // WaitingForInput means an action is already playing out or the encounter is over,
            // and a click landing then would queue a second action mid-resolution.
            if (combatManagerRef != null && combatManagerRef.CurrentState != CombatState.WaitingForInput)
            {
                Debug.Log(
                    $"{nameof(GridManager)}: move rejected - combat state is " +
                    $"{combatManagerRef.CurrentState}, expected {CombatState.WaitingForInput}.",
                    this);

                return false;
            }

            if (!TryMoveUnitToTile(unit, targetCoords))
            {
                return false;
            }

            // Redraw from the new position. NOTE: nothing tracks "already moved this turn" yet,
            // so leaving the range up means a unit can keep moving until the turn is ended. That
            // rule belongs with action economy, not here.
            ShowMoveRangeForUnit(unit);
            return true;
        }

        // The unit clicks should act on: the turn owner when combat is running, testUnit when it
        // is not. Returns false when neither is available.
        private bool TryGetActiveUnit(out GridUnit unit)
        {
            if (combatManagerRef != null)
            {
                unit = combatManagerRef.GetActiveUnit();

                if (unit == null)
                {
                    Debug.Log(
                        $"{nameof(GridManager)}: move rejected - combat has no active unit. " +
                        $"Has {nameof(CombatManager.StartCombat)} been called?",
                        this);

                    return false;
                }

                return true;
            }

            // FALLBACK - remove alongside testUnit. Keeps click-to-move testable in scenes that
            // have no CombatManager yet, at the cost of enforcing no turn order at all.
            if (!warnedAboutMissingCombatManager)
            {
                warnedAboutMissingCombatManager = true;

                Debug.LogWarning(
                    $"{nameof(GridManager)}: no {nameof(combatManagerRef)} assigned, falling back to " +
                    $"{nameof(testUnit)}. Turn order and combat state are not enforced.",
                    this);
            }

            unit = testUnit;
            return unit != null;
        }

        // ---------------------------------------------------------------------
        // Convenience accessors. These hide the struct-copy write-back described in GridTile.
        // ---------------------------------------------------------------------

        // INTENTIONAL API: unused today, for validating coordinates from UI and map editing.
        public bool HasTile(Vector2Int coords) => GridTiles.ContainsKey(coords);

        // INTENTIONAL API: unused today, the read path for ability targeting and AI queries.
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
