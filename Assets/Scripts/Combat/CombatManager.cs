using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Turn sequencing only: who acts, in what order, and whose turn it is now.
    // Deliberately knows nothing about what an action does.
    //
    // Turns come from a CT (charge time) clock, after Final Fantasy Tactics - see
    // Docs/FFT_DESIGN_NOTES.md section 2. Every clock tick each unit gains CT equal to its Speed;
    // whoever reaches TurnThreshold acts, then pays for what it did. Speed therefore decides how
    // OFTEN a unit acts, not merely who goes first, and holding back is a real choice: a unit that
    // only moved, or only waited, comes round again sooner.
    public partial class CombatManager : MonoBehaviour
    {
        [Tooltip("Units in this encounter. Replaced by StartCombat(units) when a mission spawns " +
                 "its own; the Inspector list is for hand-built test scenes.")]
        [FormerlySerializedAs("ParticipatingUnits")]
        [SerializeField] private List<GridUnit> participatingUnits = new List<GridUnit>();

        // TEMPORARY TEST WIRING - REMOVE once ability selection UI exists.
        [Header("Debug (temporary)")]
        [Tooltip("TEMPORARY: ability used by the Test Execute Ability context-menu action.")]
        [SerializeField] private AbilityDefinition testAbility;

        public CombatState CurrentState { get; private set; } = CombatState.CombatEnd;

        // Raised whenever the turn passes to a different unit. Carries the new active unit, which
        // may be null if combat could not start.
        //
        // An event rather than CombatManager calling GridManager directly: turn order should not
        // need to know what a highlight is, and the next listeners (unit UI, camera focus, AI
        // hand-off) can subscribe without this class growing a reference to each of them.
        public event Action<GridUnit> OnActiveUnitChanged;

        // Raised once when an encounter is decided - never for AbortCombat, which decides nothing.
        // Fired after OnActiveUnitChanged(null), so listeners see the board already cleared.
        public event Action<CombatEndResult> OnCombatEnded;

        // Which sides were present when the encounter began. Snapshotted rather than read off the
        // live roster, because SetFaction can empty a side mid-fight - and recruiting the last
        // enemy must still count as winning, not as "there were never any enemies".
        private bool encounterHadPlayers;
        private bool encounterHadEnemies;

        // FFT's numbers. A turn that moved AND acted costs a full TurnThreshold; doing only one
        // costs 80, doing neither 60. Whatever CT is left over carries into the next cycle, capped
        // at MaxCarryOver so a unit that idles cannot bank several turns in a row.
        public const int TurnThreshold = 100;
        public const int CostMoveAndAct = 100;
        public const int CostMoveOrAct = 80;
        public const int CostNeither = 60;
        public const int MaxCarryOver = 60;

        // A roster where nobody has Speed > 0 would tick forever. This many ticks without anyone
        // becoming ready is treated as that case rather than as a slow unit.
        private const int MaxTicksWithoutTurn = 10000;

        // Raised when a knocked-out unit's flatline counter runs out and it leaves the board for
        // good. MissionRunner reads the unit's IsFlatlined afterwards; the HUD can react here.
        public event Action<GridUnit> OnUnitFlatlined;

        // The roster in a stable order: Speed descending, then authored position. Not a turn order
        // any more - the CT clock decides that - but it is what breaks CT ties, and EnemyAIController
        // iterates it for deterministic target tie-breaks.
        private readonly List<GridUnit> initiativeOrder = new List<GridUnit>();

        private readonly Dictionary<GridUnit, int> chargeTime = new Dictionary<GridUnit, int>();

        private GridUnit activeUnit;

        // Turns handed out this encounter, and clock ticks elapsed. Both exist for the log and the
        // HUD; per-tick logic (poison, regen, status durations) belongs in Tick.
        public int TurnNumber { get; private set; }
        public int ClockTick { get; private set; }

        public IReadOnlyList<GridUnit> TurnOrder => initiativeOrder;

        public int GetChargeTime(GridUnit unit)
        {
            return unit != null && chargeTime.TryGetValue(unit, out int ct) ? ct : 0;
        }

        // Stateless rules engine, so one instance serves the whole encounter. Owned here because
        // CombatManager is what decides when an action may be attempted; the resolver only decides
        // whether it is legal and what it does.
        public ActionResolver Resolver { get; } = new ActionResolver();

        [Tooltip("Board the resolver reads unit positions and heights from. Defaults to the first " +
                 "GridManager in the scene.")]
        [SerializeField] private GridManager gridManagerRef;

        // Gives the resolver its two board lookups - who stands where, and how high - so area
        // abilities and height-adjusted range work, while the resolver itself stays scene-free.
        private void Awake()
        {
            if (gridManagerRef == null)
            {
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            if (gridManagerRef != null)
            {
                GridManager grid = gridManagerRef;

                Resolver.UnitAt = coords =>
                    grid.TryGetTile(coords, out GridTile tile) && tile.Occupant != null
                        ? tile.Occupant.GetComponent<GridUnit>()
                        : null;

                Resolver.HeightAt = coords =>
                    grid.TryGetTile(coords, out GridTile tile) ? tile.Height : (int?)null;
            }

            Resolver.OnActionResolved += HandleActionResolved;
            Resolver.OnChargeStarted += HandleChargeStarted;

            if (gridManagerRef != null)
            {
                gridManagerRef.UnitMoved += HandleUnitMoved;
            }
        }

        private void OnDestroy()
        {
            Resolver.OnActionResolved -= HandleActionResolved;
            Resolver.OnChargeStarted -= HandleChargeStarted;

            if (gridManagerRef != null)
            {
                gridManagerRef.UnitMoved -= HandleUnitMoved;
            }
        }

        // A freshly hacked unit starts its new allegiance with an empty CT gauge - GAME_DESIGN.md's
        // brake, so a hack cannot hand the hacker an immediate extra turn from the target's charge.
        private void HandleActionResolved(ActionOutcome outcome)
        {
            if (outcome.Effective && outcome.Ability != null
                && outcome.Ability.EffectType == AbilityEffectType.Override
                && outcome.Target != null && chargeTime.ContainsKey(outcome.Target))
            {
                chargeTime[outcome.Target] = 0;
            }
        }

        // Builds initiative and hands the first unit its turn.
        //
        // Safe to call from another script's Start: GridUnit computes EffectiveStats in Awake,
        // and every Awake precedes every Start.
        [ContextMenu("Start Combat")]
        public void StartCombat()
        {
            BuildInitiativeOrder();
            SnapshotStartingSides();

            if (initiativeOrder.Count == 0)
            {
                Debug.LogWarning(
                    $"{nameof(CombatManager)}: no usable units in {nameof(participatingUnits)}. " +
                    "Combat cannot start.",
                    this);

                CurrentState = CombatState.CombatEnd;
                return;
            }

            // Everyone starts empty, so the first turn goes to whoever fills fastest - Speed, then
            // roster order on a tie.
            chargeTime.Clear();

            foreach (GridUnit unit in initiativeOrder)
            {
                chargeTime[unit] = 0;
            }

            activeUnit = null;
            TurnNumber = 0;
            ClockTick = 0;
            ClearCharges();

            Debug.Log($"Combat start. Roster: {DescribeOrder()}", this);

            AdvanceToNextTurn();
        }

        // Replaces the roster and starts. The entry point for runtime-spawned encounters; the
        // Inspector list and the context menu remain for hand-built test scenes.
        public void StartCombat(IEnumerable<GridUnit> units)
        {
            participatingUnits.Clear();
            participatingUnits.AddRange(units);
            StartCombat();
        }

        // Ends the encounter with no winner - a retreat, or the mission flow tearing the board
        // down. Does not raise OnCombatEnded, because nothing was decided.
        public void AbortCombat()
        {
            if (CurrentState == CombatState.CombatEnd)
            {
                return;
            }

            CurrentState = CombatState.CombatEnd;
            ClearCharges();

            Debug.Log($"=== COMBAT ABORTED (turn {TurnNumber}) ===", this);

            OnActiveUnitChanged?.Invoke(null);
        }

        // Whoever is currently acting, or null before StartCombat.
        public GridUnit GetActiveUnit()
        {
            return activeUnit;
        }

        // Charges the active unit for its turn and hands the turn to whoever the clock picks next.
        // Refuses to advance once the encounter is over.
        public void EndCurrentTurn()
        {
            if (CurrentState == CombatState.CombatEnd)
            {
                // Logged rather than silently ignored: a turn button that stops responding with no
                // explanation reads as a bug, and this is the most likely reason it happened.
                Debug.Log(
                    $"{nameof(CombatManager)}: combat is over, no further turns. " +
                    $"Call {nameof(StartCombat)} to begin a new encounter.",
                    this);
                return;
            }

            if (initiativeOrder.Count == 0)
            {
                return;
            }

            CurrentState = CombatState.TurnTransition;

            // The turn-boundary check. Whoever acted may have ended the fight, and TryEndCombatNow
            // is also called the moment an action resolves - see its comment. Kept here as well
            // because a side can be emptied by something that is not an attack.
            if (TryEndCombatNow())
            {
                return;
            }

            if (activeUnit != null)
            {
                PayForTurn(activeUnit, CostFor(activeUnit));
            }

            AdvanceToNextTurn();
        }

        // What the turn just taken costs, by FFT's rule: both, one, or neither of move and act.
        private static int CostFor(GridUnit unit)
        {
            bool moved = unit.HasMovedThisTurn;
            bool acted = unit.HasActedThisTurn;

            if (moved && acted) return CostMoveAndAct;
            if (moved || acted) return CostMoveOrAct;
            return CostNeither;
        }

        private void PayForTurn(GridUnit unit, int cost)
        {
            chargeTime[unit] = Mathf.Min(GetChargeTime(unit) - cost, MaxCarryOver);
        }

        // Runs the clock until someone standing is ready, and gives them the turn.
        //
        // Knocked-out units keep charging, as in FFT: when a downed unit's CT would grant a turn,
        // its flatline counter drops instead, and the clock runs on. That is what makes the counter
        // a real deadline - a fast unit goes down to it faster - and why the loop below can hand
        // out several "turns" before anyone acts.
        private void AdvanceToNextTurn()
        {
            int idleTicks = 0;

            while (true)
            {
                GridUnit ready = FindReadyUnit(chargeTime);

                if (ready == null)
                {
                    if (++idleTicks > MaxTicksWithoutTurn)
                    {
                        Debug.LogError(
                            $"{nameof(CombatManager)}: no unit reached {TurnThreshold} CT in " +
                            $"{MaxTicksWithoutTurn} ticks - does anyone have Speed above 0? Aborting.",
                            this);
                        AbortCombat();
                        return;
                    }

                    // A charge that went off may have ended the fight. Asked of the tick itself: the
                    // state reads CombatEnd until the first turn is handed out, so it cannot tell.
                    if (Tick())
                    {
                        return;
                    }

                    continue;
                }

                if (ready.IsKnockedOut)
                {
                    PayForTurn(ready, CostNeither);

                    if (ready.TickFlatline())
                    {
                        Debug.Log($"FLATLINE: {ready.DisplayName} is gone.", ready);
                        ready.Flatline();
                        OnUnitFlatlined?.Invoke(ready);
                    }
                    else
                    {
                        Debug.Log($"{ready.DisplayName} is down - flatline in {ready.FlatlineCounter}.", ready);
                    }

                    continue;
                }

                // Statuses tick at the start of each of the unit's turns - damage and healing over
                // time land, durations count down. A tick can knock the unit out, end the fight, or
                // leave it stunned; each of those means no turn is handed over.
                string statusLog = ready.TickStatuses();

                if (statusLog != null)
                {
                    Debug.Log(statusLog, ready);
                    Resolver.NotifyStatsChanged();
                }

                if (ready.IsKnockedOut)
                {
                    PayForTurn(ready, CostNeither);

                    if (TryEndCombatNow())
                    {
                        return;
                    }

                    continue;
                }

                if (ready.IsStunned)
                {
                    Debug.Log($"{ready.DisplayName} is stunned and loses the turn.", ready);
                    PayForTurn(ready, CostNeither);
                    continue;
                }

                activeUnit = ready;
                TurnNumber++;
                CurrentState = CombatState.WaitingForInput;

                Debug.Log(
                    $"Turn {TurnNumber} (tick {ClockTick}) - {activeUnit.DisplayName}'s turn " +
                    $"(CT {GetChargeTime(activeUnit)}).",
                    this);

                // Fired last, once state is settled, so a listener reading CurrentState or
                // GetActiveUnit from inside the handler sees the finished turn, not a half-built one.
                BeginTurnFor(activeUnit);
                return;
            }
        }

        // One clock tick: every unit still on the board gains its Speed in CT. Per-tick effects
        // (poison, regeneration, status countdowns) belong here, so they run on the same clock as
        // turns rather than per turn - which would make them last longer for slow units.
        // Returns true if a charge that went off this tick ended the encounter.
        private bool Tick()
        {
            ClockTick++;

            foreach (GridUnit unit in initiativeOrder)
            {
                if (unit == null || unit.IsFlatlined)
                {
                    continue;
                }

                chargeTime[unit] = GetChargeTime(unit) + Mathf.Max(0, unit.EffectiveStats.Speed);
            }

            return TickCharges();
        }

        // Highest CT at or over the threshold wins; ties go to roster order (Speed, then authored
        // position), which keeps the clock deterministic for the same reason initiative was.
        private GridUnit FindReadyUnit(Dictionary<GridUnit, int> charges)
        {
            GridUnit best = null;
            int bestCharge = int.MinValue;

            foreach (GridUnit unit in initiativeOrder)
            {
                if (unit == null || unit.IsFlatlined)
                {
                    continue;
                }

                int ct = charges.TryGetValue(unit, out int value) ? value : 0;

                if (ct >= TurnThreshold && ct > bestCharge)
                {
                    best = unit;
                    bestCharge = ct;
                }
            }

            return best;
        }

        // The next `count` turns, starting with the unit acting now. A forecast, not a promise: it
        // assumes everyone - the active unit included - spends a full move-and-act turn, so a unit
        // that holds back will come round earlier than shown. Knocked-out units appear where their
        // flatline counter would tick, which is the deadline worth seeing.
        public List<GridUnit> PredictTurns(int count)
        {
            List<GridUnit> forecast = new List<GridUnit>();

            if (CurrentState == CombatState.CombatEnd || activeUnit == null || count <= 0)
            {
                return forecast;
            }

            Dictionary<GridUnit, int> charges = new Dictionary<GridUnit, int>(chargeTime);

            forecast.Add(activeUnit);
            charges[activeUnit] = Mathf.Min(charges[activeUnit] - CostMoveAndAct, MaxCarryOver);

            int idleTicks = 0;

            while (forecast.Count < count && idleTicks <= MaxTicksWithoutTurn)
            {
                GridUnit ready = FindReadyUnit(charges);

                if (ready == null)
                {
                    idleTicks++;

                    foreach (GridUnit unit in initiativeOrder)
                    {
                        if (unit != null && !unit.IsFlatlined)
                        {
                            charges[unit] = charges[unit] + Mathf.Max(0, unit.EffectiveStats.Speed);
                        }
                    }

                    continue;
                }

                forecast.Add(ready);
                int cost = ready.IsKnockedOut ? CostNeither : CostMoveAndAct;
                charges[ready] = Mathf.Min(charges[ready] - cost, MaxCarryOver);
            }

            return forecast;
        }

        // Ends the encounter if one side is down, and reports whether it did.
        //
        // Public because the end condition has to be tested the instant an action resolves, not
        // only at the turn boundary. Before this existed, knocking out the last enemy left combat
        // running until the player pressed Wait - the corpses were still targetable and the HUD
        // still offered actions, which reads as the win being missed rather than delayed.
        //
        // Callers are the two controllers that already hold a CombatManager reference and already
        // know when an attack just landed: PlayerActionController.ResolveTargetClick and
        // EnemyAIController's attack step.
        //
        // Deliberately NOT called from ActionResolver. That class is a plain C# object with no
        // scene dependency, constructible in a test - the property that makes combat maths testable
        // at all. Giving it a CombatManager reference to call back through would end that, and the
        // resolver would have to learn about turn structure to know when the call was appropriate.
        // The controllers already have both pieces of knowledge, so nothing is gained by pushing it
        // down a layer.
        //
        // Idempotent: returns true without re-announcing if combat is already over, so a caller
        // that checks twice cannot fire the event twice.
        public bool TryEndCombatNow()
        {
            if (CurrentState == CombatState.CombatEnd)
            {
                return true;
            }

            CombatEndResult result = CheckCombatEndCondition();

            if (result == CombatEndResult.Ongoing)
            {
                return false;
            }

            EndCombat(result);
            return true;
        }

        // Ends the encounter with a result decided elsewhere - a mission objective met or failed.
        // CombatManager still knows nothing about objectives; MissionRunner evaluates them and
        // calls this. Ignored once combat is over or for Ongoing, so it can only ever end a fight.
        public void EndCombat(CombatEndResult result)
        {
            if (CurrentState == CombatState.CombatEnd || result == CombatEndResult.Ongoing)
            {
                return;
            }

            CurrentState = CombatState.CombatEnd;
            ClearCharges();

            Debug.Log($"=== COMBAT OVER: {result} (turn {TurnNumber}) ===", this);

            // Null tells listeners there is no active unit any more - PlayerActionController clears
            // its move-range highlight on this, so the board does not keep showing a dead unit's
            // reachable tiles after the encounter ends.
            OnActiveUnitChanged?.Invoke(null);
            OnCombatEnded?.Invoke(result);
        }

        // Resets the incoming unit's per-turn state, then announces the change.
        //
        // Done here rather than by having PlayerActionController subscribe to OnActiveUnitChanged:
        // the reset is an invariant of the turn system, so it must happen for AI-driven turns too,
        // and must not depend on any particular listener existing or being enabled. Resetting
        // before the event also means every listener sees already-reset state.
        //
        // A null unit (combat ended) just notifies - there is no turn to begin.
        private void BeginTurnFor(GridUnit unit)
        {
            if (unit != null)
            {
                unit.BeginTurn();
            }

            OnActiveUnitChanged?.Invoke(unit);
        }

        // Whether one side has been wiped out. Reads participatingUnits, not initiativeOrder, so
        // the answer does not depend on combat having been started.
        //
        // Each side is only considered defeatable if it had members to begin with. Without that
        // guard, "all player units are knocked out" is vacuously true on a roster with no player
        // units at all, and a one-unit test scene would report a result the instant a turn ended.
        // Empty side means "not a participant", not "already lost".
        //
        // Mutual wipeout resolves to PlayerDefeat: if the last player unit and the last enemy fall
        // in the same exchange, that is not a win. Stated here because the order of the two checks
        // below is the only thing that decides it, and it would be easy to swap by accident.
        //
        // NEUTRALS COUNT FOR NEITHER SIDE. A surviving neutral does not keep a won encounter open,
        // and wiping out every neutral wins nothing - only Player and Enemy decide the outcome.
        // The alternative (neutrals block victory) would mean a wandering critter could stall a
        // cleared map indefinitely. If a mission ever needs "keep the merchant alive" or "kill the
        // beast", that is a mission objective evaluated somewhere above this, not a combat end
        // condition: end conditions answer "is there still a fight", objectives answer "did you do
        // what you came for". Conflating them is how "escort" missions become unloseable.
        //
        // Note this also means an encounter of nothing but neutrals is permanently Ongoing, which
        // is correct - nobody in it has anything to win.
        //
        // "Had members" is the live roster OR the snapshot taken at StartCombat. The snapshot is
        // what lets converting the last enemy win: the live roster then has no Enemy left, which
        // on its own reads as "never had any" and would leave combat Ongoing forever. Before
        // StartCombat the snapshot is all false, so the live roster alone decides, as it always did.
        public CombatEndResult CheckCombatEndCondition()
        {
            bool hasPlayerUnits = encounterHadPlayers;
            bool hasEnemyUnits = encounterHadEnemies;
            bool anyPlayerStanding = false;
            bool anyEnemyStanding = false;

            foreach (GridUnit unit in participatingUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                switch (unit.CurrentFaction)
                {
                    // Guests are on the Player side but do not hold it: as in FFT, the fight is lost
                    // when the player's own units are down, even with an escort still standing.
                    // Losing a guest is a mission objective (Protect), not an end condition.
                    case Faction.Player when unit.IsGuest:
                        break;

                    case Faction.Player:
                        hasPlayerUnits = true;
                        anyPlayerStanding |= !unit.IsKnockedOut;
                        break;

                    case Faction.Enemy:
                        hasEnemyUnits = true;
                        anyEnemyStanding |= !unit.IsKnockedOut;
                        break;

                    // Faction.Neutral: deliberately ignored, per the note above.
                }
            }

            if (hasPlayerUnits && !anyPlayerStanding)
            {
                return CombatEndResult.PlayerDefeat;
            }

            if (hasEnemyUnits && !anyEnemyStanding)
            {
                return CombatEndResult.PlayerVictory;
            }

            return CombatEndResult.Ongoing;
        }

        // Sorted by Speed descending, ties broken by position in participatingUnits.
        //
        // The explicit ThenBy on the original index is what makes this deterministic. Sorting by
        // Speed alone would leave ties to the sort implementation, and List<T>.Sort is introsort -
        // documented as *unstable*, so equal-Speed units could come out in a different order
        // between runs or on a different list length. (LINQ's OrderBy is stable, but relying on
        // that reads as an accident; stating the tiebreak says what it means.)
        //
        // Determinism is worth the extra clause: identical inputs must give an identical turn
        // order every run, or a turn-order bug reproduces only sometimes and any test asserting
        // "Knight acts second" becomes flaky. It is also a precondition for replays, deterministic
        // AI, and lockstep netcode, none of which can be retrofitted onto a coin flip.
        //
        // Nulls are dropped rather than kept: an empty Inspector slot is an authoring slip, and a
        // null in the order would break every consumer of GetActiveUnit.
        private void BuildInitiativeOrder()
        {
            initiativeOrder.Clear();

            IEnumerable<GridUnit> ordered = participatingUnits
                .Select((unit, index) => (Unit: unit, Index: index))
                .Where(entry => entry.Unit != null)
                .OrderByDescending(entry => entry.Unit.EffectiveStats.Speed)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Unit);

            initiativeOrder.AddRange(ordered);

            // Only a tie-break order now. Speed changes mid-combat (haste, slow) still take effect
            // immediately, because Tick reads live Speed every clock tick.
        }

        private void SnapshotStartingSides()
        {
            encounterHadPlayers = initiativeOrder.Any(unit => unit.CurrentFaction == Faction.Player && !unit.IsGuest);
            encounterHadEnemies = initiativeOrder.Any(unit => unit.CurrentFaction == Faction.Enemy);
        }

        private string DescribeOrder()
        {
            return string.Join(
                ", ",
                initiativeOrder.Select((unit, index) => $"{index + 1}. {unit.DisplayName} (SPD {unit.EffectiveStats.Speed})"));
        }

        [ContextMenu("End Current Turn")]
        private void EndCurrentTurnFromMenu()
        {
            EndCurrentTurn();
        }

        // TEMPORARY TEST WIRING - REMOVE with testAbility, once ability selection UI exists.
        //
        // Fires testAbility from the active unit at the first other unit in the roster. Enough for
        // a 1v1 bench test; with three or more units "the other one" stops being meaningful and
        // this needs real target selection.
        //
        // Deliberately does not gate on CurrentState. This is a bench probe for the resolver, and
        // making it refuse outside WaitingForInput would hide resolver bugs behind turn-state
        // bugs. The real action path should gate, exactly as TryMoveActiveUnitTo does.
        [ContextMenu("Test: Execute Ability On Other Unit")]
        public void TestExecuteAbilityOnOtherUnit()
        {
            if (testAbility == null)
            {
                Debug.LogWarning($"{nameof(CombatManager)}: no {nameof(testAbility)} assigned.", this);
                return;
            }

            GridUnit activeUnit = GetActiveUnit();

            if (activeUnit == null)
            {
                Debug.LogWarning(
                    $"{nameof(CombatManager)}: no active unit. Run {nameof(StartCombat)} first.",
                    this);
                return;
            }

            GridUnit otherUnit = FindOtherUnit(activeUnit);

            if (otherUnit == null)
            {
                Debug.LogWarning(
                    $"{nameof(CombatManager)}: no second unit in {nameof(participatingUnits)} to target.",
                    this);
                return;
            }

            bool success = Resolver.TryExecuteAbility(activeUnit, otherUnit, testAbility, out string message);

            // Logged on both paths: a rejection message is the whole point of a bench probe, and
            // the HP/MP readout confirms a failed attempt changed nothing.
            Debug.Log(
                $"[{(success ? "OK" : "REJECTED")}] {message}\n" +
                $"  {otherUnit.name}: {otherUnit.CurrentStats.HP}/{otherUnit.EffectiveStats.MaxHP} HP\n" +
                $"  {activeUnit.name}: {activeUnit.CurrentStats.MP}/{activeUnit.EffectiveStats.MaxMP} MP",
                this);
        }

        // First roster entry that is not the given unit. Skips nulls, so an empty Inspector slot
        // does not become the target.
        private GridUnit FindOtherUnit(GridUnit excluding)
        {
            foreach (GridUnit unit in participatingUnits)
            {
                if (unit != null && unit != excluding)
                {
                    return unit;
                }
            }

            return null;
        }
    }
}
