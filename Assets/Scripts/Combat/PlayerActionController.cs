using System;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Turns player intent into game actions: which HUD button was pressed, and what a click on the
    // board means given that choice.
    //
    // Also the ONLY bridge between combat and the grid. It owns combatManagerRef, the
    // OnActiveUnitChanged subscription and active-unit resolution - all of which lived on
    // GridManager until that dependency was cut. It already held both references and already gated
    // on CombatState, so moving them removed duplication rather than relocating it, and left
    // GridManager with no Tactica.Combat reference at all.
    //
    // Deliberately NOT in GridManager, which is still ~845 lines across five responsibilities -
    // tile data, coordinate conversion, rendering, highlighting, pathfinding. Action mode is input
    // *policy*, a different job from owning tile state, and it needs both managers anyway. Keeping
    // it out stops GridManager growing a sixth responsibility and gives the eventual AI turn
    // handler an obvious sibling to sit next to.
    //
    // GridCursorHighlighter stays the input *source* (it owns the raycast); this decides what the
    // reported tile means.
    public class PlayerActionController : MonoBehaviour
    {
        [Tooltip("Grid this drives. Defaults to the first GridManager in the scene.")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Source of the current turn owner and the combat state gate. Defaults to the " +
                 "first CombatManager in the scene. Without it no action is ever accepted.")]
        [SerializeField] private CombatManager combatManagerRef;

        public PlayerActionMode Mode { get; private set; } = PlayerActionMode.None;

        // Raised when the player picks a target, or with null when targeting is abandoned.
        //
        // An event rather than a CombatHUD field on purpose. UI already depends on Combat, so a
        // reference the other way would rebuild the namespace cycle that moving the turn-change
        // handler out of GridManager just removed. It also keeps this class driveable by an AI or
        // a keyboard shortcut with no HUD present, and matches how the HUD already learns about
        // OnActiveUnitChanged and OnStatsChanged.
        public event Action<GridUnit> OnTargetSelected;

        // The ability chosen for the current Targeting session. Cleared when targeting ends.
        private AbilityDefinition pendingAbility;

        private void Awake()
        {
            if (gridManagerRef == null)
            {
                gridManagerRef = FindAnyObjectByType<GridManager>();
            }

            if (combatManagerRef == null)
            {
                combatManagerRef = FindAnyObjectByType<CombatManager>();
            }
        }

        // OnEnable/OnDisable rather than Awake/OnDestroy: symmetric, so toggling this component off
        // and on re-subscribes exactly once, and OnDisable runs on the way to destruction anyway.
        // Subscribing in Awake and never unsubscribing would keep this object alive through the
        // event's delegate list after a scene change - the usual C# event leak in Unity.
        //
        // Safe to read combatManagerRef here: Awake resolves it and always precedes OnEnable on the
        // same component.
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

        // Turn passed to someone else: retire the outgoing unit's range. The new one is NOT drawn -
        // BeginMove is the only thing that paints it. Showing it automatically lit tiles that
        // ignored clicks until Move was pressed, which reads as a broken grid rather than a prompt.
        //
        // This lived on GridManager until the grid stopped knowing about combat. Here it also sits
        // next to the mode state, so a future "reset to None on turn change" has an obvious home -
        // today that already happens on the EndTurn path, which is the only way a turn ends.
        //
        // The unit parameter is unused; kept because the signature is the event's.
        private void HandleActiveUnitChanged(GridUnit unit)
        {
            if (gridManagerRef != null)
            {
                gridManagerRef.ClearMoveRangeHighlight();
            }
        }

        // Move: paint the move range and leave targeting if it was active.
        //
        // This is the ONLY thing that shows the range. GridManager used to draw it automatically on
        // OnActiveUnitChanged, which lit tiles that ignored clicks until Move was pressed.
        public void BeginMove()
        {
            GridUnit unit = GetActionableUnit();

            if (unit == null)
            {
                return;
            }

            if (unit.HasMovedThisTurn)
            {
                Debug.Log(
                    $"{nameof(PlayerActionController)}: {unit.name} has already moved this turn. " +
                    "Attack is still available.",
                    this);
                return;
            }

            CancelTargeting();
            gridManagerRef.ShowMoveRangeForUnit(unit);
            Mode = PlayerActionMode.Moving;
        }

        // Attack: pick an ability, light every tile holding a legal target, and wait for a click.
        public void BeginAttack()
        {
            GridUnit unit = GetActionableUnit();

            if (unit == null)
            {
                return;
            }

            AbilityDefinition ability = SelectAbility(unit);

            if (ability == null)
            {
                Debug.Log(
                    $"{nameof(PlayerActionController)}: {unit.name} has no usable ability. " +
                    "Check the job's Ability Unlocks list and the unit's Level.",
                    this);
                return;
            }

            List<Vector2Int> targets = FindTargetableTiles(unit, ability);

            if (targets.Count == 0)
            {
                Debug.Log($"{nameof(PlayerActionController)}: no valid targets for {ability.AbilityName}.", this);
                return;
            }

            pendingAbility = ability;

            // Move range comes down first. Pressing Move then Attack without moving used to leave
            // both layers lit, and backing out of targeting then left move-range tiles glowing with
            // Mode back at None - tiles that look clickable and are not, the same symptom that
            // removing the automatic turn-start range was meant to eliminate.
            gridManagerRef.ClearMoveRangeHighlight();
            gridManagerRef.ShowAttackableTiles(targets);
            Mode = PlayerActionMode.Targeting;

            Debug.Log($"{nameof(PlayerActionController)}: targeting with {ability.AbilityName} ({targets.Count} target(s)).", this);
        }

        // Wait: end the turn, taking a defensive stance if the unit held its attack.
        //
        // The condition is deliberately "did not attack" rather than "did nothing at all" - moving
        // into cover and bracing is the tactical choice this is meant to reward, and requiring the
        // unit to stand still as well would make Wait strictly worse than not moving.
        public void Wait()
        {
            GridUnit unit = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;

            // Not GetActionableUnit: that refuses outside WaitingForInput, and the stance must not
            // silently fail to apply while still ending the turn below.
            if (unit != null && !unit.HasAttackedThisTurn)
            {
                unit.BeginDefending();
            }

            EndTurn();
        }

        // Clears any in-progress targeting first so highlights do not survive into the next unit's
        // turn.
        //
        // Private because Wait is the only way a player ends a turn - the End Turn button that used
        // to call this directly is gone. Make it public again when something needs a stance-free
        // end (an AI handler, a turn timer).
        private void EndTurn()
        {
            if (combatManagerRef == null)
            {
                return;
            }

            CancelTargeting();
            Mode = PlayerActionMode.None;
            combatManagerRef.EndCurrentTurn();
        }

        // Entry point for board clicks. GridCursorHighlighter reports the tile; the meaning is
        // decided here.
        public void HandleTileClicked(Vector2Int coords)
        {
            if (gridManagerRef == null)
            {
                return;
            }

            if (Mode == PlayerActionMode.Targeting)
            {
                ResolveTargetClick(coords);
                return;
            }

            if (Mode == PlayerActionMode.Moving)
            {
                TryMove(coords);
                return;
            }

            // None: no action chosen, so a board click means nothing. Logged rather than ignored -
            // a click that silently does nothing is indistinguishable from a broken one.
            Debug.Log(
                $"{nameof(PlayerActionController)}: no action selected. Press Move or Attack first.",
                this);
        }

        private void TryMove(Vector2Int coords)
        {
            GridUnit unit = GetActionableUnit();

            if (unit == null)
            {
                return;
            }

            // Re-checked here, not just in BeginMove: the mode could have been entered before the
            // move happened by some other path. The economy rule belongs on every route to a move,
            // not only the button that usually starts one.
            if (unit.HasMovedThisTurn)
            {
                Debug.Log($"{nameof(PlayerActionController)}: {unit.name} has already moved this turn.", this);
                Mode = PlayerActionMode.None;
                return;
            }

            // The highlight is the UI gate; TryMoveUnitToTile re-validates against live grid data,
            // so a stale highlight cannot authorise an illegal move.
            if ((gridManagerRef.GetTileHighlightState(coords) & TileHighlightState.InMoveRange) == 0)
            {
                return;
            }

            // Passes the unit GetActionableUnit already resolved, rather than letting the grid
            // re-derive the turn owner from a bare coordinate. MarkMoved below is then provably
            // about the unit that actually moved.
            if (gridManagerRef.TryMoveUnitToTile(unit, coords))
            {
                unit.MarkMoved();

                // Back to neutral, but the turn is NOT over - move then attack is the whole point.
                // Only movement is spent; Attack remains available.
                Mode = PlayerActionMode.None;
            }
        }

        private void ResolveTargetClick(Vector2Int coords)
        {
            bool isAttackable = (gridManagerRef.GetTileHighlightState(coords) & TileHighlightState.Attackable) != 0;

            if (!isAttackable)
            {
                // Clicking anywhere else backs out. Silently ignoring the click would leave the
                // player stuck in targeting with no visible way out.
                Debug.Log($"{nameof(PlayerActionController)}: targeting cancelled.", this);
                OnTargetSelected?.Invoke(null);
                CancelTargeting();
                Mode = PlayerActionMode.None;
                return;
            }

            GridUnit user = GetActionableUnit();
            GridUnit target = GetUnitAt(coords);

            if (user == null || target == null || pendingAbility == null)
            {
                OnTargetSelected?.Invoke(null);
                CancelTargeting();
                Mode = PlayerActionMode.None;
                return;
            }

            // Announced BEFORE the ability resolves, so the panel is populated whether the attempt
            // lands or is rejected - "here is who you tried to hit" is the useful readout either
            // way. On success the OnStatsChanged raised inside TryExecuteAbility then refreshes it
            // with the post-damage numbers.
            OnTargetSelected?.Invoke(target);

            // TryExecuteAbility raises OnStatsChanged itself on success, which is what refreshes
            // the HUD panels and unit bars - nothing extra to raise here.
            bool success = combatManagerRef.Resolver.TryExecuteAbility(user, target, pendingAbility, out string message);

            // Only on success, mirroring MarkMoved: a rejected ability costs nothing, so it must
            // not forfeit the defensive stance Wait would otherwise grant.
            if (success)
            {
                user.MarkAttacked();
            }

            Debug.Log($"[{(success ? "OK" : "REJECTED")}] {message}", this);

            CancelTargeting();
            Mode = PlayerActionMode.None;
        }

        // PLACEHOLDER: first unlocked ability, standing in for a real ability-selection menu.
        // Once that menu exists it picks the ability and calls BeginAttack with it; this method
        // is the only thing that changes.
        private static AbilityDefinition SelectAbility(GridUnit unit)
        {
            if (unit.CurrentJob == null)
            {
                return null;
            }

            List<AbilityDefinition> unlocked = unit.CurrentJob.GetUnlockedAbilities(unit.Level);

            return unlocked.Count > 0 ? unlocked[0] : null;
        }

        // Every tile within the ability's range holding a unit the resolver considers a legal
        // target. Uses GetGridDistance (Chebyshev, path-independent) rather than the move-range
        // flood fill - reach does not care about walls or occupancy, and the flood fill excludes
        // occupied tiles, which is every target by definition.
        private List<Vector2Int> FindTargetableTiles(GridUnit user, AbilityDefinition ability)
        {
            List<Vector2Int> targets = new List<Vector2Int>();

            foreach (KeyValuePair<Vector2Int, GridTile> entry in gridManagerRef.GridTiles)
            {
                if (GridManager.GetGridDistance(user.GridCoords, entry.Key) > ability.Range)
                {
                    continue;
                }

                GridUnit occupant = GetUnitAt(entry.Key);

                if (occupant == null || occupant.IsKnockedOut)
                {
                    continue;
                }

                if (combatManagerRef.Resolver.IsValidTarget(user, occupant, ability))
                {
                    targets.Add(entry.Key);
                }
            }

            return targets;
        }

        private GridUnit GetUnitAt(Vector2Int coords)
        {
            if (!gridManagerRef.TryGetTile(coords, out GridTile tile) || tile.Occupant == null)
            {
                return null;
            }

            return tile.Occupant.GetComponent<GridUnit>();
        }

        // The unit whose turn it is, or null when no action should be accepted at all.
        private GridUnit GetActionableUnit()
        {
            if (gridManagerRef == null || combatManagerRef == null)
            {
                return null;
            }

            if (combatManagerRef.CurrentState != CombatState.WaitingForInput)
            {
                Debug.Log(
                    $"{nameof(PlayerActionController)}: input ignored, combat state is " +
                    $"{combatManagerRef.CurrentState}.",
                    this);
                return null;
            }

            return combatManagerRef.GetActiveUnit();
        }

        private void CancelTargeting()
        {
            pendingAbility = null;

            if (gridManagerRef != null)
            {
                gridManagerRef.ClearAttackableHighlights();
            }
        }
    }
}
