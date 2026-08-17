using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Decides what a non-player unit does with its turn.
    //
    // A sibling of PlayerActionController rather than a branch inside it. The two share every
    // action *primitive* - ShowMoveRangeForUnit, TryMoveUnitToTile, Resolver.TryExecuteAbility -
    // and differ entirely in where the decision comes from: one waits for a button, the other
    // evaluates the board. That is a difference in decision *source*, not in rules, and the rules
    // already live somewhere neutral (GridManager and ActionResolver), so there is nothing to
    // share by merging.
    //
    // Folding them together would produce a class whose every method opens with "if this is an AI
    // turn", and the halves would need untangling again the moment either grows - an ability menu
    // on the player side, threat scoring on this one. Kept apart, the human path cannot regress
    // when AI logic changes, and a third driver later (a replay, a scripted tutorial turn) is
    // another sibling rather than a third branch.
    //
    // A turn runs as a coroutine, and the delays below are NOT only cosmetic. Ending the turn
    // inline would call EndCurrentTurn from inside OnActiveUnitChanged - the very event that
    // dispatched this turn - so an all-AI round would resolve as one nested call stack in a single
    // frame, with the state machine re-entering itself at every level. Yielding first means each
    // turn is dispatched and finished on its own frame, and the stack unwinds between units.
    // Readability is the bonus; not re-entering the turn system is the reason.
    public class EnemyAIController : MonoBehaviour
    {
        [Tooltip("Grid the AI reasons over. Defaults to the first GridManager in the scene.")]
        [SerializeField] private GridManager gridManagerRef;

        [Tooltip("Source of the turn owner and the roster. Defaults to the first CombatManager in " +
                 "the scene. Without it no AI turn is ever noticed.")]
        [SerializeField] private CombatManager combatManagerRef;

        [Header("Pacing")]
        [Tooltip("Pause before an AI unit acts, so its turn is legible. Also what keeps the turn " +
                 "system from re-entering itself - do not set to 0.")]
        [Min(0.01f)]
        [SerializeField] private float thinkDelay = 0.45f;

        [Tooltip("Pause between moving and attacking. Stands in for movement animation, which " +
                 "does not exist yet.")]
        [Min(0f)]
        [SerializeField] private float actionDelay = 0.35f;

        // Tracked so a turn change arriving mid-routine cannot leave two turns running at once.
        private Coroutine turnRoutine;

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

        // Symmetric subscribe/unsubscribe, same reasoning as PlayerActionController: an Awake-only
        // subscription leaks this object through the event's delegate list after a scene change.
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

        // The hand-off point between the two controllers. Both subscribe to the same event and each
        // ignores the turns that are not its own, rather than either one dispatching to the other -
        // so neither has to know the other exists.
        private void HandleActiveUnitChanged(GridUnit unit)
        {
            // Null means combat ended, not that an AI turn started.
            if (unit == null)
            {
                return;
            }

            // Player units are driven by the HUD. This is the whole gate; PlayerActionController
            // has no matching check because its entry points are button presses, which cannot
            // happen during an AI turn once the HUD is disabled for one.
            //
            // Reads IsPlayerControlled, which is the placeholder faction bool - so this inherits
            // that limitation exactly. Replacing it with a real Faction type changes this one line.
            if (unit.IsPlayerControlled)
            {
                return;
            }

            TakeTurn(unit);
        }

        private void TakeTurn(GridUnit aiUnit)
        {
            // Defensive: a turn change should never arrive while a routine is still running, since
            // the routine is what ends the turn. It can if something else ends the turn manually -
            // the CombatManager context menu, say - and two routines acting for different units at
            // once would interleave moves and attacks.
            if (turnRoutine != null)
            {
                StopCoroutine(turnRoutine);
                turnRoutine = null;
            }

            turnRoutine = StartCoroutine(TakeTurnRoutine(aiUnit));
        }

        // Decide, move, attack, end turn. Every step re-checks that this unit is still the one
        // acting, because the delays mean the world can change between them.
        private IEnumerator TakeTurnRoutine(GridUnit aiUnit)
        {
            AITargetDecision decision = FindBestTarget(aiUnit);

            LogDecision(aiUnit, decision);

            yield return new WaitForSeconds(thinkDelay);

            // No ability, no target, or already knocked out: nothing to do but pass. The delay
            // above still applies so a pass reads as a turn rather than a skipped frame.
            if (decision.HasTarget && !aiUnit.IsKnockedOut && StillActing(aiUnit))
            {
                if (decision.DestinationTile != aiUnit.GridCoords)
                {
                    // Same call PlayerActionController.TryMove makes, with the unit passed
                    // explicitly. It re-validates against live grid data, so a destination chosen
                    // before the delay cannot authorise an illegal move if the board shifted.
                    if (gridManagerRef.TryMoveUnitToTile(aiUnit, decision.DestinationTile))
                    {
                        aiUnit.MarkMoved();
                        Debug.Log($"AI: {aiUnit.name} moves to {decision.DestinationTile}.", aiUnit);
                    }
                    else
                    {
                        Debug.Log(
                            $"AI: {aiUnit.name} could not move to {decision.DestinationTile} - " +
                            "the tile is no longer reachable.",
                            aiUnit);
                    }

                    yield return new WaitForSeconds(actionDelay);
                }

                // Re-tested rather than trusting decision.CanActThisTurn: that was computed for the
                // destination, and the move above may have been refused.
                if (StillActing(aiUnit) && !aiUnit.IsKnockedOut)
                {
                    TryAttack(aiUnit, decision);
                }
            }

            EndTurn(aiUnit);
        }

        private void TryAttack(GridUnit aiUnit, AITargetDecision decision)
        {
            GridUnit target = decision.Target;

            if (target == null || target.IsKnockedOut)
            {
                return;
            }

            // Range is checked inside TryExecuteAbility, which rejects with a message rather than
            // throwing - so an out-of-range attempt after a failed move reports itself instead of
            // needing a second range test here.
            if (!combatManagerRef.Resolver.IsTargetInRange(aiUnit, target, decision.Ability))
            {
                Debug.Log(
                    $"AI: {aiUnit.name} is not in range of {target.name} after moving - holding.",
                    aiUnit);
                return;
            }

            bool success = combatManagerRef.Resolver.TryExecuteAbility(
                aiUnit, target, decision.Ability, out string message);

            // Mirrors ResolveTargetClick: only a landed ability counts as having attacked, and the
            // same OK/REJECTED shape so AI and player actions read identically in the console.
            if (success)
            {
                aiUnit.MarkAttacked();
            }

            Debug.Log($"[{(success ? "OK" : "REJECTED")}] {message}", aiUnit);
        }

        // Straight to CombatManager, deliberately not through anything shaped like
        // PlayerActionController.EndTurn. That method also cancels targeting and resets the action
        // mode - both HUD state this controller never touches - and Wait additionally grants the
        // defensive stance, which is a player choice rather than a rule of ending a turn. An AI
        // unit that waited would be taking a decision nobody made.
        private void EndTurn(GridUnit aiUnit)
        {
            // Cleared BEFORE EndCurrentTurn, because that call synchronously raises
            // OnActiveUnitChanged and can start the next unit's routine re-entrantly - which would
            // otherwise stop the coroutine currently executing this line.
            turnRoutine = null;

            if (!StillActing(aiUnit))
            {
                return;
            }

            combatManagerRef.EndCurrentTurn();
        }

        // Whether this unit is still the one whose turn it is. False once combat has ended or the
        // turn has passed on, which can happen across any of the delays above.
        private bool StillActing(GridUnit aiUnit)
        {
            return combatManagerRef != null
                && combatManagerRef.CurrentState != CombatState.CombatEnd
                && combatManagerRef.GetActiveUnit() == aiUnit;
        }

        // Picks who this unit wants to hit.
        //
        // Public and side-effect free so the decision can be unit-tested and inspected without
        // running a turn.
        public AITargetDecision FindBestTarget(GridUnit aiUnit)
        {
            if (aiUnit == null || combatManagerRef == null || gridManagerRef == null)
            {
                return default;
            }

            AbilityDefinition ability = SelectAbility(aiUnit);

            if (ability == null)
            {
                return default;
            }

            // Every tile it could be standing on when it attacks: where it is now, plus everywhere
            // it can reach. GetTilesInMoveRange excludes the start tile (and every occupied tile),
            // so the current position has to be added back - attacking without moving is a legal
            // turn, and for a Move 0 unit it is the only one.
            //
            // Current position goes FIRST, not appended. GetTilesInMoveRange returns BFS order, so
            // prepending 0 steps keeps the whole list sorted by actual path cost - which lets the
            // destination pickers below take the first acceptable tile and get the cheapest one,
            // exactly, rather than approximating cost with Chebyshev distance.
            List<Vector2Int> standingTiles = new List<Vector2Int> { aiUnit.GridCoords };

            standingTiles.AddRange(
                gridManagerRef.GetTilesInMoveRange(aiUnit.GridCoords, aiUnit.EffectiveStats.Move));

            GridUnit bestReachable = null;
            int bestReachableHP = int.MaxValue;
            int bestReachableTravel = int.MaxValue;
            int bestReachableDistance = int.MaxValue;
            int reachableCount = 0;

            GridUnit nearest = null;
            int nearestDistance = int.MaxValue;

            // TurnOrder rather than a scene scan: it is the encounter's actual roster, and it gives
            // a stable iteration order, which is what makes the tie-breaks below deterministic.
            IReadOnlyList<GridUnit> roster = combatManagerRef.TurnOrder;

            for (int i = 0; i < roster.Count; i++)
            {
                GridUnit candidate = roster[i];

                if (candidate == null || candidate == aiUnit || candidate.IsKnockedOut)
                {
                    continue;
                }

                // ActionResolver owns the relationship rule, placeholder and all. Reimplementing
                // "anyone who is not me is an enemy" here would mean two definitions to find and
                // fix when factions land - and they would drift apart in the meantime.
                if (!combatManagerRef.Resolver.IsValidTarget(aiUnit, candidate, ability))
                {
                    continue;
                }

                int distance = DistanceFromClosestStandingTile(standingTiles, candidate.GridCoords);

                if (distance <= ability.Range)
                {
                    reachableCount++;

                    // PLACEHOLDER HEURISTIC: lowest current HP wins - "finish the wounded one".
                    // It is cheap, legible, and completely ignores what a target can actually do.
                    // Real threat evaluation weighs damage output, healers and buffers first,
                    // whether a kill is actually securable this turn, and how exposed the AI
                    // leaves itself by committing. That is future work and will replace this
                    // comparison, not wrap it.
                    int hp = candidate.CurrentStats.HP;

                    // Tie-break on how far the unit actually has to WALK, not on `distance` above.
                    // Distance-from-best-standing-tile barely discriminates among reachable
                    // targets - by definition it is already <= Range for all of them, so with a
                    // melee ability every one of them sits at 1 and the comparison is inert.
                    // Travel distance separates "adjacent already" from "all the way across my
                    // move range", which is the choice actually worth making.
                    //
                    // Roster order is the implicit third tie-break, since a strict < keeps whoever
                    // was found first. Deterministic all the way down, for the same reason
                    // initiative breaks ties on list index.
                    int travel = GridManager.GetGridDistance(aiUnit.GridCoords, candidate.GridCoords);

                    if (hp < bestReachableHP || (hp == bestReachableHP && travel < bestReachableTravel))
                    {
                        bestReachable = candidate;
                        bestReachableHP = hp;
                        bestReachableTravel = travel;
                        bestReachableDistance = distance;
                    }
                }

                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            if (bestReachable != null)
            {
                Vector2Int attackFrom = FirstTileInRangeOf(standingTiles, bestReachable.GridCoords, ability.Range);

                return new AITargetDecision(
                    bestReachable, ability, true, bestReachableDistance, reachableCount, attackFrom);
            }

            if (nearest != null)
            {
                // FALLBACK: nothing is attackable this turn, so walk at the closest one anyway.
                // Greedy - it minimises distance to the target from the tiles available right now,
                // with no lookahead, so a wall between them will park it against the wall rather
                // than route around. Real pathing toward an unreachable target is future work.
                Vector2Int approachFrom = ClosestTileTo(standingTiles, nearest.GridCoords);

                return new AITargetDecision(nearest, ability, false, nearestDistance, 0, approachFrom);
            }

            // No valid target anywhere: stay put and pass.
            return new AITargetDecision(null, ability, false, 0, 0, aiUnit.GridCoords);
        }

        // The cheapest tile to attack from. standingTiles is ordered by path cost with the current
        // position first, so the first acceptable entry is the one requiring the fewest steps -
        // and staying put wins whenever it is already good enough.
        private static Vector2Int FirstTileInRangeOf(List<Vector2Int> standingTiles, Vector2Int target, int range)
        {
            for (int i = 0; i < standingTiles.Count; i++)
            {
                if (GridManager.GetGridDistance(standingTiles[i], target) <= range)
                {
                    return standingTiles[i];
                }
            }

            // Unreachable by construction - the caller only calls this for a target already known
            // to be in range from somewhere. Falling back to the current tile keeps it total.
            return standingTiles[0];
        }

        // The tile that gets closest to the target.
        //
        // Chebyshev alone does not discriminate here: every tile in the same row or column as the
        // target is equidistant under it, so a whole rank of candidates ties and the pick falls to
        // whatever order the flood fill emitted - deterministic, but it makes the unit sidle
        // diagonally across the board instead of walking at its target.
        //
        // Squared Euclidean breaks those ties the way a person would expect, and stays exact
        // integer arithmetic (no sqrt, no float comparison). It is only a tie-break: Chebyshev
        // still decides which tile is genuinely closer, because that is the metric movement and
        // range are measured in.
        private static Vector2Int ClosestTileTo(List<Vector2Int> standingTiles, Vector2Int target)
        {
            Vector2Int best = standingTiles[0];
            int bestDistance = GridManager.GetGridDistance(best, target);
            int bestSpread = SquaredDistance(best, target);

            for (int i = 1; i < standingTiles.Count; i++)
            {
                int distance = GridManager.GetGridDistance(standingTiles[i], target);
                int spread = SquaredDistance(standingTiles[i], target);

                if (distance < bestDistance || (distance == bestDistance && spread < bestSpread))
                {
                    best = standingTiles[i];
                    bestDistance = distance;
                    bestSpread = spread;
                }
            }

            return best;
        }

        private static int SquaredDistance(Vector2Int a, Vector2Int b)
        {
            int dx = a.x - b.x;
            int dy = a.y - b.y;

            return dx * dx + dy * dy;
        }

        // Fewest steps between the target and any tile the unit could attack from.
        //
        // O(reachable tiles x candidates), which at Move 2 on a 10x10 board is a few dozen integer
        // comparisons per turn. Worth revisiting only if move ranges get large enough that this
        // shows up in a profile.
        private static int DistanceFromClosestStandingTile(List<Vector2Int> standingTiles, Vector2Int target)
        {
            int best = int.MaxValue;

            for (int i = 0; i < standingTiles.Count; i++)
            {
                int distance = GridManager.GetGridDistance(standingTiles[i], target);

                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        // PLACEHOLDER: first unlocked ability, matching PlayerActionController.SelectAbility.
        //
        // Duplicated deliberately rather than shared today - it is three lines wrapping a
        // JobDefinition call, and the two will diverge the moment real selection exists (the player
        // gets a menu; this gets scored by expected damage). When that happens neither of these
        // survives, so factoring out the placeholder now would only add a seam to delete later.
        private static AbilityDefinition SelectAbility(GridUnit unit)
        {
            if (unit.CurrentJob == null)
            {
                return null;
            }

            List<AbilityDefinition> unlocked = unit.CurrentJob.GetUnlockedAbilities(unit.Level);

            return unlocked.Count > 0 ? unlocked[0] : null;
        }

        // The whole output of this layer. Says who decided, what it picked, and on what grounds -
        // "why" matters more than "who" while the heuristic is still a placeholder, because a
        // sensible-looking target chosen for the wrong reason is the failure worth catching.
        private static void LogDecision(GridUnit aiUnit, AITargetDecision decision)
        {
            if (decision.Ability == null)
            {
                Debug.Log(
                    $"AI: {aiUnit.name} has no usable ability - check the job's Ability Unlocks " +
                    "list and the unit's Level. No decision made.",
                    aiUnit);
                return;
            }

            if (!decision.HasTarget)
            {
                Debug.Log($"AI: {aiUnit.name} found no valid target for {decision.Ability.AbilityName}.", aiUnit);
                return;
            }

            GridUnit target = decision.Target;
            string targetHP = $"{target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP";

            if (decision.CanActThisTurn)
            {
                Debug.Log(
                    $"AI: {aiUnit.name} picks {target.name} ({targetHP}) with {decision.Ability.AbilityName}. " +
                    $"Reason: lowest HP of {decision.ReachableTargetCount} reachable target(s); " +
                    $"distance {decision.DistanceFromBestStandingTile} vs range {decision.Ability.Range}.",
                    aiUnit);
                return;
            }

            int shortfall = decision.DistanceFromBestStandingTile - decision.Ability.Range;

            Debug.Log(
                $"AI: {aiUnit.name} wants {target.name} ({targetHP}) but cannot reach anyone this turn. " +
                $"Reason: nearest valid target, still {shortfall} step(s) short even after moving " +
                $"(distance {decision.DistanceFromBestStandingTile} vs range {decision.Ability.Range}).",
                aiUnit);
        }
    }
}
