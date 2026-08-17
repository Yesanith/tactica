using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Stats;
using Tactica.UI;

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

        // PLACEHOLDER, same category as ActionResolver's "any two different units are enemies":
        // a single bool is not a faction system. It cannot express neutrals, a third hostile army,
        // or charmed units switching sides mid-battle. It exists so CheckCombatEndCondition has
        // *something* to split the roster by. Replace both this and ActionResolver.IsValidTarget
        // with a real Faction type together - they are the same missing concept seen from two
        // angles, and fixing one without the other leaves the rules inconsistent.
        [Tooltip("PLACEHOLDER: true for the player's side, false for enemies. Stands in for a " +
                 "proper faction system.")]
        [SerializeField] private bool isPlayerControlled;

        [Tooltip("Leave empty. Added automatically in Awake and builds its own Canvas - assign " +
                 "only to point at a hand-made bar instead.")]
        [SerializeField] private UnitStatusBar statusBar;

        [Tooltip("Flat Defense added while this unit is defending. Applied by RecalculateStats, " +
                 "not by the damage formula.")]
        [SerializeField] private int defenseBonus = 5;

        [Tooltip("Distance from this GameObject's pivot down to its feet. Unity's Capsule is 2 " +
                 "units tall with a centred pivot, so 1.0 fits an unscaled capsule. Use 0 for a " +
                 "model whose pivot already sits at its feet.")]
        [FormerlySerializedAs("PivotHeight")]
        [SerializeField] private float pivotHeight = 1.0f;

        public Vector2Int GridCoords => gridCoords;

        public bool IsPlayerControlled => isPlayerControlled;

        // Read-only for UI and inspection. Changing either at runtime must go through a path that
        // also calls RecalculateStats, or EffectiveStats silently goes stale.
        public JobDefinition CurrentJob => currentJob;
        public int Level => level;

        // Reuses CurrentStats.IsAlive rather than re-testing HP, so "what counts as down" is
        // defined in exactly one place. If that ever grows past HP <= 0 - revival states, a
        // separate downed-but-not-dead flag - only IsAlive changes.
        public bool IsKnockedOut => !CurrentStats.IsAlive;

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

        // One move per turn. Lives on the unit rather than in PlayerActionController because it is
        // a fact about the unit's turn, not about player input: an AI turn handler needs the same
        // answer, and going through the player's input controller to get it would be backwards.
        // Keeping it here also avoids a per-unit dictionary that would have to be pruned whenever
        // a unit is destroyed or leaves the roster.
        //
        // Not serialised - runtime turn state, reset by BeginTurn every time the unit becomes
        // active. Only movement is capped; attacking is unaffected.
        public bool HasMovedThisTurn { get; private set; }

        // Same lifetime as HasMovedThisTurn. Read by the Wait action, which only grants the
        // defensive stance to a unit that spent its turn without attacking.
        public bool HasAttackedThisTurn { get; private set; }

        // Defensive stance: survives the turn it was declared in and is dropped when this unit
        // next becomes active, so the bonus is live for exactly one round of everyone else's
        // turns - which is the whole point of choosing it.
        public bool IsDefending { get; private set; }

        public int DefenseBonus => defenseBonus;

        // Called by CombatManager when this unit becomes the active unit. The natural home for
        // anything else that resets per turn.
        public void BeginTurn()
        {
            HasMovedThisTurn = false;
            HasAttackedThisTurn = false;

            // The stance is cleared here rather than at the moment of being hit: it must protect
            // against every attack made before this unit acts again, not just the first.
            if (IsDefending)
            {
                IsDefending = false;
                RecalculateStats();

                // Logged after the recalculation so the number is read back rather than predicted.
                // TODO: a HUD status icon. Until one exists the console is the only way to see
                // that the stance is active, which makes it easy to mistake for a broken bonus.
                Debug.Log($"{name} leaves defensive stance (Defense back to {EffectiveStats.Defense}).", this);
            }
        }

        public void MarkMoved()
        {
            HasMovedThisTurn = true;
        }

        public void MarkAttacked()
        {
            HasAttackedThisTurn = true;
        }

        // Enters the defensive stance. A method rather than a settable property because the flag
        // feeds EffectiveStats - writing it without recalculating would leave the bonus declared
        // but not applied, the same staleness trap as changing Level directly.
        public void BeginDefending()
        {
            if (IsDefending)
            {
                return;
            }

            IsDefending = true;
            RecalculateStats();

            Debug.Log($"{name} takes a defensive stance (+{defenseBonus} Defense, now {EffectiveStats.Defense}).", this);
        }

        private void Awake()
        {
            if (gridManagerRef == null)
            {
                // Full scene scan - fine once at startup, never per frame.
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            // Bars attach themselves. A GridUnit component is now the whole requirement for a
            // unit - no authored Canvas, which is what makes runtime-spawned units possible.
            // AddComponent runs the new component's Awake synchronously, so its hierarchy exists
            // before RecalculateStats below asks it to draw.
            if (statusBar == null)
            {
                statusBar = GetComponent<UnitStatusBar>();
            }

            if (statusBar == null)
            {
                statusBar = gameObject.AddComponent<UnitStatusBar>();
            }

            // Awake, not Start. Unity guarantees every Awake runs before any Start, so this is the
            // only phase where a value can be published for other scripts to read in their Start -
            // and CombatManager.StartCombat is designed to be callable from one, where
            // BuildInitiativeOrder reads EffectiveStats.Speed off every unit. Two Starts have no
            // defined order between them, so computing this in Start would be a coin flip that
            // fails silently: Speed would read 0 and the turn order would come out arbitrary.
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

            // Modifiers are folded in here, not in ActionResolver.CalculateDamage. The formula asks
            // for "this unit's Defense" and must keep getting one honest answer - the moment a
            // second consumer appears (a UI panel, an AI threat estimate) a bonus applied only
            // inside the damage formula is invisible to it.
            //
            // A CharacterStats of mostly zeroes used as a modifier via operator + is exactly the
            // shape that struct was built for. Real buffs will be a list summed the same way.
            if (IsDefending)
            {
                EffectiveStats += new CharacterStats { Defense = defenseBonus };
            }

            if (!statsInitialised)
            {
                CurrentStats = CurrentStats.FullFrom(EffectiveStats);
                statsInitialised = true;
                RefreshStatusBar();
                return;
            }

            CurrentStats = CurrentStats.ClampedTo(EffectiveStats);
            RefreshStatusBar();
        }

        // One entry point rather than three inline UpdateBars calls at each mutation site.
        //
        // The two-argument shape and the null guard live here once, so adding a fourth mutation
        // site (MP spending, a revive, a status tick) means remembering to call one no-argument
        // method rather than reconstructing the correct arguments and re-checking for null. It
        // also gives a single place to hook anything else that should react to a stat change -
        // floating combat text, a death animation - without touching ApplyDamage or Heal again.
        //
        // KNOWN LIMITATION: CurrentStats is a public field (deliberately - see its comment), so
        // outside code can write HP or MP directly and the bar will silently go stale.
        // ActionResolver already does exactly that when spending MP. The real fix is to route
        // every mutation through methods on GridUnit; until then, anything that writes
        // CurrentStats from outside must call this afterwards.
        public void RefreshStatusBar()
        {
            if (statusBar == null)
            {
                return;
            }

            statusBar.UpdateBars(CurrentStats, EffectiveStats);
        }

        // Reduces HP, floored at 0. Negative and zero amounts are ignored rather than healing -
        // a miscalculated "damage" must never top a unit up.
        public void ApplyDamage(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            CurrentStats.HP = Mathf.Max(0, CurrentStats.HP - amount);
            RefreshStatusBar();
        }

        // Restores HP, capped at the current MaxHP. Reads EffectiveStats rather than storing a cap,
        // so a buff that raises MaxHP immediately allows healing into the new headroom.
        public void Heal(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            CurrentStats.HP = Mathf.Min(EffectiveStats.MaxHP, CurrentStats.HP + amount);
            RefreshStatusBar();
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
