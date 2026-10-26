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

        [Tooltip("Source of board clicks. Defaults to the first GridCursorHighlighter in the " +
                 "scene. Without it the HUD still works but clicks on the board do nothing.")]
        [SerializeField] private GridCursorHighlighter cursorRef;

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

        public AbilityDefinition PendingAbility => pendingAbility;

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

            if (cursorRef == null)
            {
                cursorRef = FindAnyObjectByType<GridCursorHighlighter>();
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

            if (cursorRef != null)
            {
                cursorRef.TileClicked += HandleTileClicked;
            }
        }

        private void OnDisable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnActiveUnitChanged -= HandleActiveUnitChanged;
            }

            if (cursorRef != null)
            {
                cursorRef.TileClicked -= HandleTileClicked;
            }
        }

        // Turn passed to someone else: retire the outgoing unit's range and any half-finished
        // targeting. The new range is NOT drawn - BeginMove is the only thing that paints it.
        // Showing it automatically lit tiles that ignored clicks until Move was pressed, which
        // reads as a broken grid rather than a prompt.
        //
        // The mode reset used to rely on Wait being the only way a turn ends. It is not any more:
        // a retreat aborts combat mid-targeting, and a converted unit can end a fight from the
        // faction panel. Resetting here covers every route, including the null that ends combat.
        //
        // The unit parameter is unused; kept because the signature is the event's.
        private void HandleActiveUnitChanged(GridUnit unit)
        {
            CancelTargeting();
            Mode = PlayerActionMode.None;

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
                    $"{nameof(PlayerActionController)}: {unit.DisplayName} has already moved this turn. " +
                    "Attack is still available.",
                    this);
                return;
            }

            CancelTargeting();
            gridManagerRef.ShowMoveRangeForUnit(unit);
            Mode = PlayerActionMode.Moving;
        }

        // The abilities the acting unit could choose from right now - its job's unlocks at its level.
        // Empty when it is not a player unit's turn. The HUD builds its Act menu from this.
        public List<AbilityDefinition> GetAvailableAbilities()
        {
            GridUnit unit = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;

            if (unit == null || !unit.IsPlayerControlled)
            {
                return new List<AbilityDefinition>();
            }

            return unit.GetActionAbilities();
        }

        // Act: light every tile holding a legal target for the chosen ability, and wait for a click.
        // The ability comes from the HUD's Act menu; this replaced a placeholder that always used
        // the job's first unlock.
        public bool BeginAttack(AbilityDefinition ability)
        {
            GridUnit unit = GetActionableUnit();

            if (unit == null || ability == null)
            {
                return false;
            }

            // One action per turn, FFT's rule. Moving is still allowed afterwards.
            if (unit.HasActedThisTurn)
            {
                Debug.Log($"{nameof(PlayerActionController)}: {unit.DisplayName} has already acted this turn.", this);
                return false;
            }

            if (!combatManagerRef.Resolver.CanUse(unit, ability, out string reason))
            {
                Debug.Log($"{nameof(PlayerActionController)}: {unit.DisplayName} cannot use {ability.AbilityName}: {reason}", this);
                return false;
            }

            List<Vector2Int> targets = FindTargetableTiles(unit, ability);

            if (targets.Count == 0)
            {
                Debug.Log($"{nameof(PlayerActionController)}: no valid targets for {ability.AbilityName}.", this);
                return false;
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
            return true;
        }

        // What the pending ability would do to whoever stands on coords, or null when that tile is
        // not a legal target right now. Drives the HUD's targeting preview.
        public string DescribeTargetPreview(Vector2Int coords)
        {
            if (Mode != PlayerActionMode.Targeting || pendingAbility == null || gridManagerRef == null)
            {
                return null;
            }

            if ((gridManagerRef.GetTileHighlightState(coords) & TileHighlightState.Attackable) == 0)
            {
                return null;
            }

            GridUnit user = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;

            if (user == null)
            {
                return null;
            }

            string outcome = combatManagerRef.Resolver.DescribeOutcome(user, coords, pendingAbility);

            // A charged ability lands later, on whoever is standing there then.
            return pendingAbility.ChargeTime > 0
                ? $"[charges {pendingAbility.ChargeTime} ticks] {outcome}"
                : outcome;
        }

        // Wait: first press lights the four neighbouring tiles to choose a facing (FFT's end-of-turn
        // choice); a click on one turns the unit and ends the turn, a second Wait ends it as it
        // faces. Either way the turn ends with a defensive stance if the unit held its action.
        //
        // The condition is deliberately "did not act" rather than "did nothing at all" - moving
        // into cover and bracing is the tactical choice this is meant to reward, and requiring the
        // unit to stand still as well would make Wait strictly worse than not moving.
        public void Wait()
        {
            GridUnit actor = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;

            if (actor != null && actor.IsPlayerControlled && Mode != PlayerActionMode.ChoosingFacing
                && combatManagerRef.CurrentState == CombatState.WaitingForInput)
            {
                BeginFacingChoice(actor);
                return;
            }

            ConfirmWait();
        }

        // Ends the turn now, without a facing prompt. What the facing click and the second Wait
        // both arrive at.
        public void ConfirmWait()
        {
            GridUnit unit = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;

            // Same ownership gate as GetActionableUnit. Without it, pressing Wait during an AI turn
            // ended that turn for the AI - and handed it a defensive stance nobody chose.
            if (unit == null || !unit.IsPlayerControlled)
            {
                Debug.Log($"{nameof(PlayerActionController)}: not a player unit's turn - Wait ignored.", this);
                return;
            }

            // Not GetActionableUnit: that refuses outside WaitingForInput, and the stance must not
            // silently fail to apply while still ending the turn below.
            if (!unit.HasActedThisTurn)
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
            // No fight yet (deployment) or none any more: board clicks belong to someone else.
            if (gridManagerRef == null || combatManagerRef == null || combatManagerRef.CurrentState == CombatState.CombatEnd)
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

            if (Mode == PlayerActionMode.ChoosingFacing)
            {
                ChooseFacing(coords);
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
                Debug.Log($"{nameof(PlayerActionController)}: {unit.DisplayName} has already moved this turn.", this);
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

                // Unless Overwatch dropped the unit on arrival: a knocked-out unit's turn is over.
                if (unit.IsKnockedOut && combatManagerRef.CurrentState != CombatState.CombatEnd)
                {
                    EndTurn();
                }
            }
        }

        private void BeginFacingChoice(GridUnit unit)
        {
            CancelTargeting();
            gridManagerRef.ClearMoveRangeHighlight();

            List<Vector2Int> around = new List<Vector2Int>();

            foreach (Vector2Int direction in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
            {
                Vector2Int coords = unit.GridCoords + direction;

                if (gridManagerRef.HasTile(coords))
                {
                    around.Add(coords);
                }
            }

            gridManagerRef.ShowAttackableTiles(around);
            Mode = PlayerActionMode.ChoosingFacing;
            Debug.Log($"{nameof(PlayerActionController)}: click a lit tile to face it, or Wait again to keep facing {unit.CurrentFacing}.", this);
        }

        private void ChooseFacing(Vector2Int coords)
        {
            GridUnit unit = combatManagerRef.GetActiveUnit();

            if (unit != null && (gridManagerRef.GetTileHighlightState(coords) & TileHighlightState.Attackable) != 0)
            {
                unit.FaceTowards(coords);
                ConfirmWait();
                return;
            }

            // Anything else backs out to the turn, like cancelling targeting.
            CancelTargeting();
            Mode = PlayerActionMode.None;
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

            // The unit on the clicked tile, or - for an area ability aimed at empty ground - the
            // first unit in the blast, so the target panel still has someone to show.
            GridUnit target = GetUnitAt(coords);

            if (target == null && user != null && pendingAbility != null)
            {
                List<GridUnit> affected = combatManagerRef.Resolver.GetAffectedUnits(user, coords, pendingAbility);
                target = affected.Count > 0 ? affected[0] : null;
            }

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
            bool success = combatManagerRef.Resolver.TryExecuteAt(user, coords, pendingAbility, out string message);

            // Only on success, mirroring MarkMoved: a rejected ability costs nothing, so it must
            // not forfeit the defensive stance Wait would otherwise grant.
            if (success)
            {
                user.MarkActed();
            }

            Debug.Log($"[{(success ? "OK" : "REJECTED")}] {message}", this);

            // Clean up FIRST, then ask whether that ended the fight.
            //
            // The clean-up runs either way, combat over or not: the Attackable highlights belong to
            // this controller, and the OnActiveUnitChanged(null) that TryEndCombatNow raises only
            // clears the move-range layer. Skipping it on a win would leave the losing side's tiles
            // lit over a finished encounter.
            //
            // Order matters the other way too - TryEndCombatNow raises that event synchronously, so
            // a listener reading Mode from inside the handler must see the settled value. Same rule
            // CombatManager follows when it fires OnActiveUnitChanged last.
            CancelTargeting();
            Mode = PlayerActionMode.None;

            // Only on success: a rejected ability changed no HP, so no side can have been emptied
            // by it. Closes the gap where knocking out the last enemy left combat running until the
            // player pressed Wait.
            if (success && combatManagerRef.TryEndCombatNow())
            {
                Debug.Log(
                    $"{nameof(PlayerActionController)}: that action ended the encounter - no " +
                    "further input accepted.",
                    this);
            }
        }

        // Every tile within the ability's range holding a unit the resolver considers a legal
        // target. Uses GetGridDistance (Manhattan, path-independent) rather than the move-range
        // flood fill - reach does not care about walls or occupancy, and the flood fill excludes
        // occupied tiles, which is every target by definition.
        private List<Vector2Int> FindTargetableTiles(GridUnit user, AbilityDefinition ability)
        {
            List<Vector2Int> targets = new List<Vector2Int>();

            ActionResolver resolver = combatManagerRef.Resolver;
            bool area = resolver.GetArea(user, ability) > 0;

            foreach (KeyValuePair<Vector2Int, GridTile> entry in gridManagerRef.GridTiles)
            {
                // Range includes the height bonus or penalty toward this very tile.
                if (!resolver.IsInRange(user, entry.Key, ability))
                {
                    continue;
                }

                if (!area)
                {
                    // Knocked-out occupants are left to IsValidTarget: they are illegal for
                    // everything except Revive, and only the resolver knows which ability is which.
                    GridUnit occupant = GetUnitAt(entry.Key);

                    if (occupant != null && resolver.IsValidTarget(user, occupant, ability))
                    {
                        targets.Add(entry.Key);
                    }

                    continue;
                }

                // An area ability can be aimed at any tile - empty ground included - whose blast
                // would touch at least one unit it is meant for. A blast that would only catch
                // allies is not offered, though one that catches allies too still is.
                foreach (GridUnit unit in resolver.GetAffectedUnits(user, entry.Key, ability))
                {
                    if (resolver.IsValidTarget(user, unit, ability))
                    {
                        targets.Add(entry.Key);
                        break;
                    }
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

            GridUnit unit = combatManagerRef.GetActiveUnit();

            // Only player-controlled units take orders from here - Player side, not a guest. Without
            // this gate the HUD drove whichever unit held the turn - press Move during an AI turn
            // and the player walked the enemy around. EnemyAIController drives everyone else.
            if (unit == null || !unit.IsPlayerControlled)
            {
                Debug.Log($"{nameof(PlayerActionController)}: input ignored, not a player unit's turn.", this);
                return null;
            }

            return unit;
        }

        // Backs out of whatever command is half-done - a move range on show, a target being
        // picked, the facing prompt - without spending anything. The HUD's Escape / right click.
        public void CancelCommand()
        {
            if (Mode == PlayerActionMode.None && pendingAbility == null)
            {
                return;
            }

            CancelTargeting();

            if (gridManagerRef != null)
            {
                gridManagerRef.ClearMoveRangeHighlight();
            }

            Mode = PlayerActionMode.None;
            OnTargetSelected?.Invoke(null);
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
