using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
using Tactica.Grid;

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
        public void EndCurrentTurn()
        {
            if (initiativeOrder.Count == 0 || CurrentState == CombatState.CombatEnd)
            {
                return;
            }

            CurrentState = CombatState.TurnTransition;

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
    }
}
