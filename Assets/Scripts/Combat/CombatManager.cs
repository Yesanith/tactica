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
    // Deliberately knows nothing about what an action does - that lands next.
    public class CombatManager : MonoBehaviour
    {
        [Tooltip("Units in this encounter. Inspector-assigned for now; real registration arrives " +
                 "with encounter setup.")]
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

        // Initiative order, resolved once by StartCombat. Separate from participatingUnits so the
        // authored list stays untouched and re-rollable.
        private readonly List<GridUnit> initiativeOrder = new List<GridUnit>();

        private int activeIndex;

        // Rounds have no logic yet, but the counter makes the wrap visible in the log and gives
        // the concept somewhere to live.
        public int RoundNumber { get; private set; }

        // INTENTIONAL API: unused today, for a turn-order UI strip.
        public IReadOnlyList<GridUnit> TurnOrder => initiativeOrder;

        // Stateless rules engine, so one instance serves the whole encounter. Owned here because
        // CombatManager is what decides when an action may be attempted; the resolver only decides
        // whether it is legal and what it does.
        public ActionResolver Resolver { get; } = new ActionResolver();

        // Builds initiative and hands the first unit its turn.
        //
        // Safe to call from another script's Start: GridUnit computes EffectiveStats in Awake,
        // and every Awake precedes every Start.
        [ContextMenu("Start Combat")]
        public void StartCombat()
        {
            BuildInitiativeOrder();

            if (initiativeOrder.Count == 0)
            {
                Debug.LogWarning(
                    $"{nameof(CombatManager)}: no usable units in {nameof(participatingUnits)}. " +
                    "Combat cannot start.",
                    this);

                CurrentState = CombatState.CombatEnd;
                return;
            }

            activeIndex = 0;
            RoundNumber = 1;
            CurrentState = CombatState.WaitingForInput;

            Debug.Log($"Combat start. Turn order: {DescribeOrder()}", this);
            Debug.Log($"Round {RoundNumber} - {GetActiveUnit().name}'s turn.", this);

            // Fired last, once state and index are settled, so a listener reading CurrentState or
            // GetActiveUnit from inside the handler sees the finished turn, not a half-built one.
            OnActiveUnitChanged?.Invoke(GetActiveUnit());
        }

        // Whoever is currently acting, or null before StartCombat or after CombatEnd.
        public GridUnit GetActiveUnit()
        {
            if (initiativeOrder.Count == 0 || activeIndex < 0 || activeIndex >= initiativeOrder.Count)
            {
                return null;
            }

            return initiativeOrder[activeIndex];
        }

        // Passes the turn to the next unit in initiative order, wrapping to the start.
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

            // TIMING GAP: the only end-condition check is here, at the turn boundary. A unit KO'd
            // mid-turn therefore does not end combat until the acting player presses End Turn, so
            // a wiped-out side can still be "targeted" for the rest of that turn. Closing this
            // means ActionResolver calling back into CombatManager after each action, which is a
            // bigger change (the resolver currently knows nothing about turn structure and is
            // testable precisely because of that). Checking here is enough for now.
            CombatEndResult result = CheckCombatEndCondition();

            if (result != CombatEndResult.Ongoing)
            {
                CurrentState = CombatState.CombatEnd;

                Debug.Log($"=== COMBAT OVER: {result} (round {RoundNumber}) ===", this);

                // Null tells listeners there is no active unit any more - GridManager clears its
                // move-range highlight on this, so the board does not keep showing a dead unit's
                // reachable tiles after the encounter ends.
                OnActiveUnitChanged?.Invoke(null);
                return;
            }

            activeIndex = (activeIndex + 1) % initiativeOrder.Count;

            // Wrapping to index 0 is a new round. Per-round logic belongs exactly here: poison and
            // regeneration ticks, buff durations counting down, weather, reinforcement spawns.
            // Doing that per-turn instead would make effects last longer for units late in the
            // order, which is the kind of unfairness players notice without being able to name.
            if (activeIndex == 0)
            {
                RoundNumber++;
                Debug.Log($"--- Round {RoundNumber} ---", this);
            }

            CurrentState = CombatState.WaitingForInput;

            Debug.Log($"Round {RoundNumber} - {GetActiveUnit().name}'s turn.", this);

            OnActiveUnitChanged?.Invoke(GetActiveUnit());
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
        public CombatEndResult CheckCombatEndCondition()
        {
            bool hasPlayerUnits = false;
            bool hasEnemyUnits = false;
            bool anyPlayerStanding = false;
            bool anyEnemyStanding = false;

            foreach (GridUnit unit in participatingUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                if (unit.IsPlayerControlled)
                {
                    hasPlayerUnits = true;
                    anyPlayerStanding |= !unit.IsKnockedOut;
                }
                else
                {
                    hasEnemyUnits = true;
                    anyEnemyStanding |= !unit.IsKnockedOut;
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

            // Snapshot, not a live view: Speed changes mid-combat (haste, slow) will not reshuffle
            // an encounter already underway. Fine for fixed turn order; a CT/charge-time system
            // like FFT's would instead recompute who acts next after every turn, which is a change
            // to this method rather than to its callers.
        }

        private string DescribeOrder()
        {
            return string.Join(
                ", ",
                initiativeOrder.Select((unit, index) => $"{index + 1}. {unit.name} (SPD {unit.EffectiveStats.Speed})"));
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
