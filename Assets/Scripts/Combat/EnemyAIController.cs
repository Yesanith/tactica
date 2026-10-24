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

            // Player units are driven by the HUD. PlayerActionController applies the mirror-image
            // gate (it refuses any unit that is not Player), so every faction has exactly one
            // driver.
            //
            // Anything not Player is driven from here, Neutral included - a neutral unit that gets
            // a turn still has to do something with it, and "run the same evaluation" is a better
            // default than standing frozen. What it *decides* comes from IsValidTarget, so if
            // neutrals stop being hostile to everyone this needs no change.
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
                        Debug.Log($"AI: {aiUnit.DisplayName} moves to {decision.DestinationTile}.", aiUnit);
                    }
                    else
                    {
                        Debug.Log(
                            $"AI: {aiUnit.DisplayName} could not move to {decision.DestinationTile} - " +
                            "the tile is no longer reachable.",
                            aiUnit);
                    }

                    yield return new WaitForSeconds(actionDelay);
                }

                // Re-tested rather than trusting decision.CanActThisTurn: that was computed for the
                // destination, and the move above may have been refused.
                if (StillActing(aiUnit) && !aiUnit.IsKnockedOut && TryAttack(aiUnit, decision))
                {
                    // The attack ended the encounter. There is no next turn to pass to, and
                    // EndCurrentTurn would only log that combat is already over.
                    turnRoutine = null;
                    yield break;
                }
            }

            EndTurn(aiUnit);
        }

        // Returns true if the attack ENDED COMBAT, not if it landed - the caller only needs to know
        // whether there is still a turn to pass on.
        private bool TryAttack(GridUnit aiUnit, AITargetDecision decision)
        {
            GridUnit target = decision.Target;

            // A knocked-out target is legal for a reboot; the resolver decides. Only a target that is
            // gone for good is dropped here.
            if (target == null || target.IsFlatlined)
            {
                return false;
            }

            // Range is checked inside TryExecuteAbility, which rejects with a message rather than
            // throwing - so an out-of-range attempt after a failed move reports itself instead of
            // needing a second range test here.
            if (!combatManagerRef.Resolver.IsTargetInRange(aiUnit, target, decision.Ability))
            {
                Debug.Log(
                    $"AI: {aiUnit.DisplayName} is not in range of {target.DisplayName} after moving - holding.",
                    aiUnit);
                return false;
            }

            bool success = combatManagerRef.Resolver.TryExecuteAbility(
                aiUnit, target, decision.Ability, out string message);

            // Mirrors ResolveTargetClick: only a landed ability counts as having acted, and the
            // same OK/REJECTED shape so AI and player actions read identically in the console.
            if (success)
            {
                aiUnit.MarkActed();
            }

            Debug.Log($"[{(success ? "OK" : "REJECTED")}] {message}", aiUnit);

            // Only on success: a rejected ability changed no HP, so no side can have been emptied.
            // Same immediate end check the player path makes - an AI wiping out the last player
            // unit must end the encounter there, not after passing the turn to a corpse.
            return success && combatManagerRef.TryEndCombatNow();
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

        // Picks what this unit does and to whom.
        //
        // Priority, in order: reboot a downed ally it can reach THIS turn, then its damage ability.
        // A reboot it cannot reach is not chased - walking toward a corpse while the enemy hits it
        // is worse than fighting - so an unreachable reboot falls through to attacking.
        //
        // Public and side-effect free so the decision can be unit-tested and inspected without
        // running a turn.
        public AITargetDecision FindBestTarget(GridUnit aiUnit)
        {
            if (aiUnit == null || combatManagerRef == null || gridManagerRef == null)
            {
                return default;
            }

            AbilityDefinition reboot = FindAffordable(aiUnit, AbilityEffectType.Revive);

            if (reboot != null)
            {
                AITargetDecision rebootDecision = FindBestTarget(aiUnit, reboot);

                if (rebootDecision.HasTarget && rebootDecision.CanActThisTurn)
                {
                    return rebootDecision;
                }
            }

            return FindBestTarget(aiUnit, SelectAbility(aiUnit));
        }

        // The target search for one specific ability. Which units count is entirely IsValidTarget's
        // call - for Revive that means downed allies, for damage standing enemies.
        public AITargetDecision FindBestTarget(GridUnit aiUnit, AbilityDefinition ability)
        {
            if (aiUnit == null || combatManagerRef == null || gridManagerRef == null || ability == null)
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
            // exactly, rather than approximating cost with straight-line distance.
            List<Vector2Int> standingTiles = new List<Vector2Int> { aiUnit.GridCoords };

            standingTiles.AddRange(
                gridManagerRef.GetTilesInMoveRange(aiUnit.GridCoords, aiUnit.EffectiveStats.Move, aiUnit.EffectiveStats.Jump));

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

                // Knocked-out candidates are NOT skipped here: whether they are valid depends on the
                // ability (Revive wants exactly them), and IsValidTarget below decides that.
                if (candidate == null || candidate == aiUnit || candidate.IsFlatlined)
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
                // FALLBACK: nothing is attackable this turn, so walk toward the nearest one -
                // measured in WALKING steps from the target (GetWalkDistances), so the unit routes
                // around holes and cover instead of parking against them. Straight-line distance
                // only breaks ties, and takes over for tiles the walk cannot reach at all.
                Dictionary<Vector2Int, int> walk = gridManagerRef.GetWalkDistances(
                    nearest.GridCoords, aiUnit.EffectiveStats.Jump);
                Vector2Int approachFrom = ClosestTileTo(standingTiles, nearest.GridCoords, walk);

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
        // Manhattan distance alone ties often: every tile on the same diagonal band around the
        // target is equidistant, so the pick would fall to whatever order the flood fill emitted -
        // deterministic, but it makes the unit drift sideways instead of walking at its target.
        //
        // Squared Euclidean breaks those ties the way a person would expect, and stays exact
        // integer arithmetic (no sqrt, no float comparison). It is only a tie-break: Manhattan
        // still decides which tile is genuinely closer, because that is the metric movement and
        // range are measured in.
        private static Vector2Int ClosestTileTo(List<Vector2Int> standingTiles, Vector2Int target, Dictionary<Vector2Int, int> walk)
        {
            Vector2Int best = standingTiles[0];
            int bestDistance = WalkOrStraight(best, target, walk);
            int bestSpread = SquaredDistance(best, target);

            for (int i = 1; i < standingTiles.Count; i++)
            {
                int distance = WalkOrStraight(standingTiles[i], target, walk);
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

        // Walking steps when the tile is connected to the target; otherwise a large penalty plus the
        // straight-line distance, so any connected tile beats any cut-off one.
        private static int WalkOrStraight(Vector2Int tile, Vector2Int target, Dictionary<Vector2Int, int> walk)
        {
            const int Unreachable = 10000;
            return walk.TryGetValue(tile, out int steps) ? steps : Unreachable + GridManager.GetGridDistance(tile, target);
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

        // PLACEHOLDER: the AI's attack choice. It reboots downed allies (FindBestTarget) but never
        // hacks - an enemy Override on the player's crew needs a counterplay story first. Scoring
        // abilities by expected value (damage, a securable kill, a hack chance) replaces this.
        //
        // Prefers the first Damage ability, so a job that lists Override or Reboot first does not
        // leave the AI hacking at random; falls back to the first unlock of any kind.
        private static AbilityDefinition SelectAbility(GridUnit unit)
        {
            if (unit.CurrentJob == null)
            {
                return null;
            }

            List<AbilityDefinition> unlocked = unit.CurrentJob.GetUnlockedAbilities(unit.Level);

            foreach (AbilityDefinition ability in unlocked)
            {
                if (ability.EffectType == AbilityEffectType.Damage)
                {
                    return ability;
                }
            }

            return unlocked.Count > 0 ? unlocked[0] : null;
        }

        // The first unlocked ability of this effect the unit can pay for, or null.
        private static AbilityDefinition FindAffordable(GridUnit unit, AbilityEffectType effect)
        {
            if (unit.CurrentJob == null)
            {
                return null;
            }

            foreach (AbilityDefinition ability in unit.CurrentJob.GetUnlockedAbilities(unit.Level))
            {
                if (ability.EffectType == effect && unit.CurrentStats.MP >= ability.MPCost)
                {
                    return ability;
                }
            }

            return null;
        }

        // The whole output of this layer. Says who decided, what it picked, and on what grounds -
        // "why" matters more than "who" while the heuristic is still a placeholder, because a
        // sensible-looking target chosen for the wrong reason is the failure worth catching.
        private static void LogDecision(GridUnit aiUnit, AITargetDecision decision)
        {
            if (decision.Ability == null)
            {
                Debug.Log(
                    $"AI: {aiUnit.DisplayName} has no usable ability - check the job's Ability Unlocks " +
                    "list and the unit's Level. No decision made.",
                    aiUnit);
                return;
            }

            if (!decision.HasTarget)
            {
                Debug.Log($"AI: {aiUnit.DisplayName} found no valid target for {decision.Ability.AbilityName}.", aiUnit);
                return;
            }

            GridUnit target = decision.Target;
            string targetHP = $"{target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP";

            if (decision.CanActThisTurn)
            {
                Debug.Log(
                    $"AI: {aiUnit.DisplayName} picks {target.DisplayName} ({targetHP}) with {decision.Ability.AbilityName}. " +
                    $"Reason: lowest HP of {decision.ReachableTargetCount} reachable target(s); " +
                    $"distance {decision.DistanceFromBestStandingTile} vs range {decision.Ability.Range}.",
                    aiUnit);
                return;
            }

            int shortfall = decision.DistanceFromBestStandingTile - decision.Ability.Range;

            Debug.Log(
                $"AI: {aiUnit.DisplayName} wants {target.DisplayName} ({targetHP}) but cannot reach anyone this turn. " +
                $"Reason: nearest valid target, still {shortfall} step(s) short even after moving " +
                $"(distance {decision.DistanceFromBestStandingTile} vs range {decision.Ability.Range}).",
                aiUnit);
        }
    }
}
