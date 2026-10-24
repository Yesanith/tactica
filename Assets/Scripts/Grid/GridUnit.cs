using System;
using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Stats;

namespace Tactica.Grid
{
    // A unit occupying a single tile. Owns its coordinate, its job/level, and its live HP/MP,
    // and keeps its transform parked on that tile's surface. No turn logic yet.
    // Assigns no mesh or material - build the visual (e.g. a Capsule) in the Inspector.
    //
    // Knows nothing about how it is displayed. It announces Spawned and StatsChanged, and the UI
    // layer attaches and drives the bars from those - see UnitStatusBar. That is what keeps Grid
    // free of any Tactica.UI reference.
    public class GridUnit : MonoBehaviour
    {
        [Tooltip("Shown in the HUD. Leave empty to fall back to the job name.")]
        [SerializeField] private string displayName;

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

        // NOT [FormerlySerializedAs("isPlayerControlled")]. That attribute redirects by name, not
        // by type, and bool -> enum is not a conversion Unity performs. Worse, if it did coerce the
        // raw value the mapping would be inverted: the old true (1) meant Player, and 1 is Enemy
        // here. The two authored units were migrated in the scene file instead - see SetFaction.
        [Tooltip("Which side this unit fights for. Drives targeting, victory conditions and bar " +
                 "colour.")]
        [SerializeField] private Faction faction = Faction.Player;

        [Tooltip("Which way the unit looks. Attacks from behind ignore its evasion; from the side " +
                 "they halve it. Turns automatically when the unit moves or acts.")]
        [SerializeField] private Facing facing = Facing.North;

        [Tooltip("Flat Defense added while this unit is defending. Applied by RecalculateStats, " +
                 "not by the damage formula.")]
        [SerializeField] private int defenseBonus = 5;

        [Tooltip("Distance from this GameObject's pivot down to its feet. Unity's Capsule is 2 " +
                 "units tall with a centred pivot, so 1.0 fits an unscaled capsule. Use 0 for a " +
                 "model whose pivot already sits at its feet.")]
        [FormerlySerializedAs("PivotHeight")]
        [SerializeField] private float pivotHeight = 1.0f;

        public Vector2Int GridCoords => gridCoords;

        // Raised at the very start of every unit's Awake, before its stats exist. The hook the UI
        // uses to attach a status bar to any unit - authored or spawned - without Grid naming the
        // UI type. Static because a listener cannot subscribe to an instance that does not exist
        // yet.
        public static event Action<GridUnit> Spawned;

        // Raised whenever HP, MP, stats or faction may have changed. Coarse on purpose, like
        // ActionResolver.OnStatsChanged: listeners re-read what they display.
        public event Action StatsChanged;

        // With domain reload disabled (Enter Play Mode Options) statics survive between play
        // sessions, and listeners from the last session would be invoked on destroyed objects.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Spawned = null;
        }

        // Falls back to the job name, then the GameObject name, so an unconfigured unit still
        // identifies itself instead of showing an empty box.
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(displayName)) return displayName;
                return currentJob != null ? currentJob.JobName : name;
            }
        }

        // Sets everything a runtime-spawned unit needs in one call.
        //
        // Meant to run BEFORE the GameObject is activated: Awake then computes stats from the
        // configured job and level, exactly as it would for an authored unit. Called on a unit
        // that is already live it re-initialises from scratch - full pools - because this is a
        // spawn, not a level-up.
        public void Configure(string newDisplayName, JobDefinition job, int newLevel, Faction newFaction, Vector2Int coords)
        {
            displayName = newDisplayName;
            currentJob = job;
            level = Mathf.Max(1, newLevel);
            faction = newFaction;
            gridCoords = coords;

            if (statsInitialised)
            {
                statsInitialised = false;
                RecalculateStats();
            }
        }

        public Faction CurrentFaction => faction;

        public Facing CurrentFacing => facing;

        // Turns the unit and its model. The transform is rotated about Y only, so a child visor or
        // weapon on the model's +Z side shows the facing; the status bar billboards separately.
        public void SetFacing(Facing newFacing)
        {
            facing = newFacing;
            transform.rotation = Quaternion.Euler(0f, FacingUtility.ToYaw(facing), 0f);
        }

        // Looks toward a tile. A no-op for the unit's own tile.
        public void FaceTowards(Vector2Int coords)
        {
            SetFacing(FacingUtility.Towards(gridCoords, coords, facing));
        }

        // Changes allegiance. The entry point recruitment will use, and the reason Faction is a
        // field with a setter rather than something read off a prefab once.
        //
        // Bars re-colour immediately; nothing else caches faction, so there is no equivalent of
        // RecalculateStats to run. Turn order is NOT rebuilt - initiative is a snapshot, so a unit
        // that switches sides mid-encounter keeps its slot in the running order and simply fights
        // for the other team from its next turn.
        public void SetFaction(Faction newFaction)
        {
            if (faction == newFaction)
            {
                return;
            }

            Faction previous = faction;
            faction = newFaction;

            NotifyStatsChanged();

            Debug.Log($"{DisplayName} changes faction: {previous} -> {newFaction}.", this);
        }

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
        // active.
        public bool HasMovedThisTurn { get; private set; }

        // One action per turn, FFT's rule: any ability, damaging or not. Same lifetime as
        // HasMovedThisTurn. Gates the action itself, decides what the CT clock charges for the
        // turn, and is read by Wait, which only grants the defensive stance to a unit that held
        // its action.
        public bool HasActedThisTurn { get; private set; }

        // How many more of this unit's turns it can lie knocked out before it flatlines. Set to
        // FlatlineTurns at the moment of the knockout and ticked by CombatManager whenever the CT
        // clock would have given the unit a turn. Only meaningful while IsKnockedOut.
        //
        // FFT's death counter: it starts at 3 and the unit is lost when it drops below 0, so a
        // downed unit has four of its own turns' worth of time to be rebooted.
        public const int FlatlineTurns = 3;
        public int FlatlineCounter { get; private set; }

        // Gone for good: off the board, out of the turn order, and - for a roster unit - out of the
        // crew once the mission ends. Distinct from IsKnockedOut, which can still be undone.
        public bool IsFlatlined { get; private set; }

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
            HasActedThisTurn = false;

            // The stance is cleared here rather than at the moment of being hit: it must protect
            // against every attack made before this unit acts again, not just the first.
            if (IsDefending)
            {
                IsDefending = false;
                RecalculateStats();

                // Logged after the recalculation so the number is read back rather than predicted.
                // TODO: a HUD status icon. Until one exists the console is the only way to see
                // that the stance is active, which makes it easy to mistake for a broken bonus.
                Debug.Log($"{DisplayName} leaves defensive stance (Defense back to {EffectiveStats.Defense}).", this);
            }
        }

        public void MarkMoved()
        {
            HasMovedThisTurn = true;
        }

        public void MarkActed()
        {
            HasActedThisTurn = true;
        }

        // One tick of the flatline counter. True when it has run out and the unit should flatline.
        public bool TickFlatline()
        {
            if (!IsKnockedOut || IsFlatlined)
            {
                return false;
            }

            FlatlineCounter--;
            NotifyStatsChanged();
            return FlatlineCounter < 0;
        }

        // Removes the unit from the board permanently. Deactivated rather than destroyed so the
        // mission can still report on it; MissionRunner destroys it with everything else.
        public void Flatline()
        {
            if (IsFlatlined)
            {
                return;
            }

            IsFlatlined = true;
            NotifyStatsChanged();

            if (gridManagerRef != null
                && gridManagerRef.TryGetTile(gridCoords, out GridTile tile)
                && tile.Occupant == gameObject)
            {
                gridManagerRef.SetOccupant(gridCoords, null);
            }

            gameObject.SetActive(false);
        }

        // Brings a knocked-out unit back before its counter runs out. Refuses a unit that is
        // standing - that is Heal's job - or already flatlined, which nothing can undo.
        public bool Revive(int hp)
        {
            if (!IsKnockedOut || IsFlatlined)
            {
                return false;
            }

            CurrentStats.HP = Mathf.Clamp(hp, 1, Mathf.Max(1, EffectiveStats.MaxHP));
            FlatlineCounter = 0;
            NotifyStatsChanged();
            return true;
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

            Debug.Log($"{DisplayName} takes a defensive stance (+{defenseBonus} Defense, now {EffectiveStats.Defense}).", this);
        }

        private void Awake()
        {
            if (gridManagerRef == null)
            {
                // Full scene scan - fine once at startup, never per frame.
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            // Announced before RecalculateStats, so a listener that attaches a status bar here has
            // it subscribed in time for the first StatsChanged below. A GridUnit component is the
            // whole requirement for a unit - no authored Canvas - which is what makes
            // runtime-spawned units possible.
            Spawned?.Invoke(this);

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

        // Releases the tile, so destroying a unit never leaves a phantom occupant blocking it -
        // previously whoever destroyed a unit had to remember to clear its tile first.
        // Only if the tile still points at this unit: something else may have claimed it since.
        private void OnDestroy()
        {
            if (gridManagerRef != null
                && gridManagerRef.TryGetTile(gridCoords, out GridTile tile)
                && tile.Occupant == gameObject)
            {
                gridManagerRef.SetOccupant(gridCoords, null);
            }
        }

        private void Start()
        {
            // Both of these need GridTiles, which GridManager builds in its own Awake. Awake-to-
            // Awake order is undefined, so they cannot move up alongside RecalculateStats.
            SnapToGridPosition();
            SetFacing(facing);

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
                NotifyStatsChanged();
                return;
            }

            CurrentStats = CurrentStats.ClampedTo(EffectiveStats);
            NotifyStatsChanged();
        }

        // One entry point for every mutation site, so adding another (a revive, a status tick)
        // means calling one method - and anything that should react to a stat change (bars,
        // floating combat text, a death animation) subscribes to StatsChanged instead of being
        // called from ApplyDamage and Heal.
        //
        // KNOWN LIMITATION: CurrentStats is a public field (deliberately - see its comment), so
        // outside code can still write HP or MP directly and listeners go stale. Every write in
        // the codebase goes through ApplyDamage, Heal or SpendMP; anything new that writes
        // CurrentStats from outside must call this afterwards.
        public void NotifyStatsChanged()
        {
            StatsChanged?.Invoke();
        }

        // Reduces HP, floored at 0. Negative and zero amounts are ignored rather than healing -
        // a miscalculated "damage" must never top a unit up.
        public void ApplyDamage(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            bool wasStanding = !IsKnockedOut;
            CurrentStats.HP = Mathf.Max(0, CurrentStats.HP - amount);

            // The counter starts at the moment of the knockout, not at the next turn, so the HUD
            // can show the deadline straight away.
            if (wasStanding && IsKnockedOut)
            {
                FlatlineCounter = FlatlineTurns;
            }

            NotifyStatsChanged();
        }

        // Spends MP, floored at 0. Affordability is the caller's rule (ActionResolver checks it
        // before anything is spent); this only keeps the pool and its listeners in step.
        public void SpendMP(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            CurrentStats.MP = Mathf.Max(0, CurrentStats.MP - amount);
            NotifyStatsChanged();
        }

        // Restores HP, capped at the current MaxHP. Reads EffectiveStats rather than storing a cap,
        // so a buff that raises MaxHP immediately allows healing into the new headroom.
        public void Heal(int amount)
        {
            // Not on the knocked out: standing a unit back up is Revive, which also clears its
            // flatline counter. Healing it here would leave the counter running on a living unit.
            if (amount <= 0 || IsKnockedOut)
            {
                return;
            }

            CurrentStats.HP = Mathf.Min(EffectiveStats.MaxHP, CurrentStats.HP + amount);
            NotifyStatsChanged();
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
