using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Stats;

namespace Tactica.Grid
{
    // A unit occupying a single tile. Owns its coordinate, its job/level, and its live HP/MP,
    // and keeps its transform parked on that tile's surface. No turn logic yet.
    // Assigns no mesh or material - build the visual (e.g. a Capsule) in the Inspector.
    public class GridUnit : MonoBehaviour
    {
        // Lowercase so the public property below can take the PascalCase name. FormerlySerializedAs
        // keeps the Inspector value across the rename (Unity serialises by field name).
        [Tooltip("Tile this unit currently stands on.")]
        [FormerlySerializedAs("GridCoords")]
        [SerializeField] private Vector2Int gridCoords;

        [Tooltip("Job asset defining this unit's stats and growth. Required - without it the " +
                 "unit has zero Move and cannot act.")]
        [FormerlySerializedAs("CurrentJob")]
        [SerializeField] private JobDefinition currentJob;

        [Tooltip("Job level. Level 1 gives the job's BaseStats with no growth applied.")]
        [Min(1)]
        [FormerlySerializedAs("Level")]
        [SerializeField] private int level = 1;

        [Tooltip("Grid this unit belongs to. Defaults to the first GridManager in the scene.")]
        [FormerlySerializedAs("GridManagerRef")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Distance from this GameObject's pivot down to its feet. Unity's Capsule is 2 " +
                 "units tall with a centred pivot, so 1.0 fits an unscaled capsule. Use 0 for a " +
                 "model whose pivot already sits at its feet.")]
        [FormerlySerializedAs("PivotHeight")]
        [SerializeField] private float pivotHeight = 1.0f;

        public Vector2Int GridCoords => gridCoords;

        // Live pools. A public field rather than a property on purpose: CurrentStats is a struct,
        // and C# forbids writing through a property of struct type (unit.CurrentStats.HP -= 5 is
        // CS1612, "cannot modify the return value"). As a field that assignment just works, which
        // is what combat will want. Serialised so the values are visible in the Inspector while
        // playing; whatever is authored in edit mode is overwritten by the first RecalculateStats.
        public CurrentStats CurrentStats;

        // Derived, never stored as truth: recomputed from job and level by RecalculateStats.
        // Cached rather than computed per access so there is one place that null-checks the job,
        // and so "recalculate" is an explicit event rather than something that silently happens
        // at every read. Change level at runtime and this is stale until RecalculateStats runs.
        public CharacterStats EffectiveStats { get; private set; }

        // Distinguishes "spawning, fill the pools" from "job or level changed, keep the damage".
        private bool statsInitialised;

        private void Awake()
        {
            if (gridManagerRef == null)
            {
                // Full scene scan - fine once at startup, never per frame.
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            // Awake, not Start. Unity guarantees every Awake runs before any Start, so this is
            // the only phase where a value can be published for other scripts to read in their
            // Start - and GridManager.Start reads EffectiveStats.Move via ShowMoveRangeForUnit.
            // Two Starts have no defined order between them, so computing this in Start would be
            // a coin flip that fails silently: Move would read 0 and the range would just not
            // appear.
            //
            // Safe to run this early because stats depend only on serialised data (currentJob,
            // level). Anything needing GridTiles must wait for Start - see below.
            RecalculateStats();
        }

        private void Start()
        {
            // Both of these need GridTiles, which GridManager builds in its own Awake. Awake-to-
            // Awake order is undefined, so they cannot move up alongside RecalculateStats.
            SnapToGridPosition();

            // Claim the starting tile, otherwise nothing marks it occupied until the first move
            // and other units could path onto it.
            if (gridManagerRef != null)
            {
                gridManagerRef.SetOccupant(gridCoords, gameObject);
            }
        }

        // Rebuilds EffectiveStats from job and level. Call after changing either.
        //
        // First run fills the pools; every later run only clamps them. That asymmetry is the whole
        // point of splitting CharacterStats from CurrentStats - levelling up must not be a free
        // full heal, but unequipping +HP gear must not leave HP stranded above the new maximum.
        [ContextMenu("Recalculate Stats")]
        public void RecalculateStats()
        {
            if (currentJob == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridUnit)} '{name}': no {nameof(currentJob)} assigned. Stats are all zero, " +
                    "so this unit has Move 0 and will show no movement range. Create a job via " +
                    "Create > Tactica > Job Definition and assign it.",
                    this);

                EffectiveStats = default;
            }
            else
            {
                EffectiveStats = currentJob.GetStatsAtLevel(level);
            }

            if (!statsInitialised)
            {
                CurrentStats = CurrentStats.FullFrom(EffectiveStats);
                statsInitialised = true;
                return;
            }

            CurrentStats = CurrentStats.ClampedTo(EffectiveStats);
        }

        // Places this unit on top of the tile at GridCoords.
        [ContextMenu("Snap To Grid Position")]
        public void SnapToGridPosition()
        {
            if (gridManagerRef == null)
            {
                Debug.LogWarning(
                    $"{nameof(GridUnit)} '{name}': no {nameof(gridManagerRef)} assigned and none found in the scene. " +
                    "Cannot snap to grid.",
                    this);
                return;
            }

            // pivot = tile centre + half the tile slab + pivot-to-feet.
            // TileSurfaceOffset comes from GridManager rather than a constant, so changing tile
            // thickness lifts units to match instead of half-burying them.
            Vector3 tileCentre = gridManagerRef.GridToWorldPosition(GridCoords);
            float verticalOffset = gridManagerRef.TileSurfaceOffset + pivotHeight;

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
