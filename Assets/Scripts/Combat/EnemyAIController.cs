using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public partial class EnemyAIController : MonoBehaviour
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
                if (!decision.Retreat && StillActing(aiUnit) && !aiUnit.IsKnockedOut && TryAttack(aiUnit, decision))
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
            if (!combatManagerRef.Resolver.IsInRange(aiUnit, decision.Center, decision.Ability))
            {
                Debug.Log(
                    $"AI: {aiUnit.DisplayName} is not in range of {decision.Center} after moving - holding.",
                    aiUnit);
                return false;
            }

            bool success = combatManagerRef.Resolver.TryExecuteAt(
                aiUnit, decision.Center, decision.Ability, out string message);

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

            AITargetDecision best = EvaluateActions(aiUnit);

            // Badly hurt and nothing worth the risk (no kill, no heal): pull back toward friends.
            // The last one standing has no friends to fall back on and fights on - otherwise two
            // lone survivors back away from each other forever.
            if (aiUnit.CurrentStats.HP < aiUnit.EffectiveStats.MaxHP * RetreatHealthFraction
                && (!best.HasTarget || best.Score < RetreatUnlessScore) && HasStandingAlly(aiUnit))
            {
                AITargetDecision retreat = FindRetreat(aiUnit);

                if (retreat.HasTarget)
                {
                    return retreat;
                }
            }

            if (best.HasTarget)
            {
                return best;
            }

            // Nothing worth doing from anywhere it can stand this turn: walk toward the nearest
            // enemy with its main attack, as before.
            return FindBestTarget(aiUnit, SelectAbility(aiUnit));
        }

        // ---------------------------------------------------------------------
        // Action evaluation (Phase 13): every usable ability x every target it could reach this
        // turn, scored. Replaces "lowest HP wins" - it weighs what an action actually does: damage
        // times hit chance, a kill it can secure, the target's role, friendly fire, heals where
        // they matter, buffs not already running, stuns and debuffs on enemies.
        // ---------------------------------------------------------------------

        // Score weights. Rough by design - they only have to rank sensible choices above silly ones.
        private const float KillBonus = 12f;
        private const float FriendlyFirePenalty = 2f;
        private const float HealThreshold = 0.6f;
        private const float ReviveValue = 30f;
        private const float BuffValue = 5f;

        // A buff only counts on an ally a hostile unit stands within this many tiles of. Without it
        // a unit with a party buff spent the whole approach recasting it instead of closing in.
        private const int BuffThreatRange = 7;
        private const float DebuffValue = 6f;
        private const float StunValue = 14f;
        private const float DotValue = 8f;
        private const float HackValue = 25f;
        private const float TravelCost = 0.5f;

        // A charged ability may miss its mark - the target can walk out before it fires.
        private const float ChargedValue = 0.6f;

        // Below this share of max HP, a unit with nothing better to do falls back...
        private const float RetreatHealthFraction = 0.3f;

        // ...unless its best action scores at least this - a kill or a real heal.
        private const float RetreatUnlessScore = 12f;

        private const float OverwatchValue = 3f;

        private AITargetDecision EvaluateActions(GridUnit aiUnit)
        {
            ActionResolver resolver = combatManagerRef.Resolver;
            List<Vector2Int> standingTiles = StandingTiles(aiUnit);
            IReadOnlyList<GridUnit> roster = combatManagerRef.TurnOrder;

            // A taunted unit must hit its taunter if any harmful ability can reach it this turn.
            GridUnit taunter = aiUnit.Taunter;
            bool tauntLocked = false;

            List<AbilityDefinition> usable = aiUnit.GetActionAbilities()
                .Where(ability => resolver.CanUse(aiUnit, ability, out _))
                .ToList();

            if (taunter != null)
            {
                tauntLocked = usable.Any(ability => ability.IsHarmful
                    && resolver.IsValidTarget(aiUnit, taunter, ability)
                    && FirstStandingTileFor(aiUnit, standingTiles, taunter.GridCoords, ability).HasValue);
            }

            AITargetDecision best = default;
            float bestScore = 0f;
            int reachable = 0;

            foreach (AbilityDefinition ability in usable)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    GridUnit candidate = roster[i];

                    if (candidate == null || candidate.IsFlatlined || !resolver.IsValidTarget(aiUnit, candidate, ability))
                    {
                        continue;
                    }

                    if (tauntLocked && ability.IsHarmful && candidate != taunter)
                    {
                        continue;
                    }

                    Vector2Int? from = FirstStandingTileFor(aiUnit, standingTiles, candidate.GridCoords, ability);

                    if (!from.HasValue)
                    {
                        continue;
                    }

                    reachable++;

                    int travel = GridManager.GetGridDistance(aiUnit.GridCoords, from.Value);
                    float charged = ability.ChargeTime > 0 ? ChargedValue : 1f;
                    float score = ScoreAction(aiUnit, ability, candidate.GridCoords) * charged - travel * TravelCost;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new AITargetDecision(candidate, ability, true, 0, 0, from.Value, candidate.GridCoords, score);
                    }
                }
            }

            return best.HasTarget
                ? new AITargetDecision(best.Target, best.Ability, true, 0, reachable, best.DestinationTile, best.Center, best.Score)
                : default;
        }

        // What aiming this ability at `center` is worth, summed over everyone it would touch.
        private float ScoreAction(GridUnit aiUnit, AbilityDefinition ability, Vector2Int center)
        {
            ActionResolver resolver = combatManagerRef.Resolver;
            float score = 0f;

            foreach (GridUnit target in resolver.GetAffectedUnits(aiUnit, center, ability))
            {
                bool ally = target.CurrentFaction == aiUnit.CurrentFaction;
                float hit = resolver.GetHitChance(aiUnit, target, ability) / 100f;
                int hp = target.CurrentStats.HP;
                int maxHP = Mathf.Max(1, target.EffectiveStats.MaxHP);

                switch (ability.EffectType)
                {
                    case AbilityEffectType.Damage:
                    {
                        int damage = resolver.CalculateDamage(aiUnit, target, ability);
                        float expected = Mathf.Min(damage, hp) * hit;

                        score += ally
                            ? -expected * FriendlyFirePenalty
                            : expected + (damage >= hp ? KillBonus * hit : 0f) + RoleBonus(target) * hit;
                        break;
                    }

                    case AbilityEffectType.Heal:
                        if (ally && (float)hp / maxHP < HealThreshold)
                        {
                            score += Mathf.Min(resolver.GetPower(aiUnit, ability), maxHP - hp) * 1.2f;
                        }
                        break;

                    case AbilityEffectType.Revive:
                        score += ally ? ReviveValue : 0f;
                        break;

                    case AbilityEffectType.Override:
                        score += ally ? 0f : resolver.GetOverrideChance(aiUnit, target, ability) / 100f * HackValue;
                        break;

                    case AbilityEffectType.Buff:
                        score += ally && !target.HasStatusFrom(ability) && UnderThreat(target) ? BuffValue : 0f;
                        break;

                    default:
                    {
                        if (target.HasStatusFrom(ability))
                        {
                            break;
                        }

                        if (ability.StatusKind == StatusKind.Overwatch)
                        {
                            score += ally ? OverwatchValue : 0f;
                            break;
                        }

                        float value = ability.StatusKind == StatusKind.Stun ? StunValue
                            : ability.StatusKind == StatusKind.DamageOverTime ? DotValue
                            : ability.StatusKind == StatusKind.HealOverTime ? (ally && (float)hp / maxHP < 0.8f ? BuffValue : 0f)
                            : ability.StatusKind == StatusKind.Taunt ? (ally ? 0f : BuffValue)
                            : DebuffValue;

                        bool helpful = ability.StatusKind == StatusKind.HealOverTime;
                        score += helpful ? value : (ally ? -value : value * hit);
                        break;
                    }
                }
            }

            return score;
        }

        // Whether any unit hostile to this one stands close enough to matter this turn or next.
        private bool UnderThreat(GridUnit unit)
        {
            foreach (GridUnit other in combatManagerRef.TurnOrder)
            {
                if (other != null && !other.IsKnockedOut && !other.IsFlatlined && other.CurrentFaction != unit.CurrentFaction
                    && GridManager.GetGridDistance(other.GridCoords, unit.GridCoords) <= BuffThreatRange)
                {
                    return true;
                }
            }

            return false;
        }

        // Medics and hackers first: a unit that heals, reboots, hacks or stuns is worth more dead
        // than its HP suggests.
        private static float RoleBonus(GridUnit target)
        {
            float bonus = 0f;

            foreach (AbilityDefinition ability in target.GetActionAbilities())
            {
                if (ability.EffectType == AbilityEffectType.Heal || ability.EffectType == AbilityEffectType.Revive)
                {
                    bonus = Mathf.Max(bonus, 6f);
                }
                else if (ability.EffectType == AbilityEffectType.Override || ability.StatusKind == StatusKind.Stun)
                {
                    bonus = Mathf.Max(bonus, 4f);
                }
            }

            return bonus;
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
            List<Vector2Int> standingTiles = StandingTiles(aiUnit);

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

                // ActionResolver owns the relationship rule (factions, KO, Revive). A second copy
                // here would drift from it.
                if (!combatManagerRef.Resolver.IsValidTarget(aiUnit, candidate, ability))
                {
                    continue;
                }

                int distance = DistanceFromClosestStandingTile(standingTiles, candidate.GridCoords);

                if (distance <= combatManagerRef.Resolver.GetBaseRange(aiUnit, ability))
                {
                    reachableCount++;

                    // Lowest current HP wins here. This search is now only the fallback when
                    // EvaluateActions finds nothing worth doing - the scored evaluation above makes
                    // the real choice whenever any action is in reach.
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
                Vector2Int attackFrom = FirstTileInRangeOf(
                    standingTiles, bestReachable.GridCoords, combatManagerRef.Resolver.GetBaseRange(aiUnit, ability));

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
                // Walking into Overwatch only when every way forward is covered - then it walks on
                // regardless, rather than freezing in place until the watch runs out.
                List<Vector2Int> safe = standingTiles.Where(tile => tile == aiUnit.GridCoords || !Watched(aiUnit, tile)).ToList();
                Vector2Int approachFrom = ClosestTileTo(safe.Count > 1 ? safe : standingTiles, nearest.GridCoords, walk);

                return new AITargetDecision(nearest, ability, false, nearestDistance, 0, approachFrom);
            }

            // No valid target anywhere: stay put and pass.
            return new AITargetDecision(null, ability, false, 0, 0, aiUnit.GridCoords);
        }

        // The ability the AI walks in with when EvaluateActions finds nothing in reach this turn -
        // only the approach uses it; real choices (heals, hacks, debuffs) are scored there.
        //
        // The strongest usable Damage ability, so the approach is measured against its main
        // attack's range; falls back to anything usable.
        private static AbilityDefinition SelectAbility(GridUnit unit)
        {
            // Only what it can actually use now: right weapon in hand, enough MP. A basic Attack
            // (MP 0, no requirement) is always among these for a unit with a weapon.
            List<AbilityDefinition> usable = unit.GetActionAbilities()
                .Where(ability => ability.MPCost <= unit.CurrentStats.MP && unit.HasWeaponType(ability.RequiredWeapon))
                .ToList();

            // Strongest damage first, so an enemy with a real skill uses it over the plain Attack.
            AbilityDefinition best = usable
                .Where(ability => ability.EffectType == AbilityEffectType.Damage)
                .OrderByDescending(ability => ability.Power + (ability.UsesWeapon && unit.Weapon != null ? unit.Weapon.Power : 0))
                .FirstOrDefault();

            return best != null ? best : usable.FirstOrDefault();
        }

        // The whole output of this layer. Says who decided, what it picked, and on what grounds -
        // "why" matters more than "who" when tuning the scores, because a sensible-looking target
        // chosen for the wrong reason is the failure worth catching.
        private void LogDecision(GridUnit aiUnit, AITargetDecision decision)
        {
            if (decision.Retreat)
            {
                Debug.Log($"AI: {aiUnit.DisplayName} is badly hurt and falls back to {decision.DestinationTile}.", aiUnit);
                return;
            }

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
                    $"Reason: best score {decision.Score:0.0} of {decision.ReachableTargetCount} option(s) in reach, " +
                    $"aimed at {decision.Center} from {decision.DestinationTile}.",
                    aiUnit);
                return;
            }

            // The resolver's reach, not the ability's own Range: a weapon ability's reach comes
            // from the weapon (Aimed Shot authors 0 and fires as far as the rifle).
            int range = combatManagerRef.Resolver.GetBaseRange(aiUnit, decision.Ability);
            int shortfall = decision.DistanceFromBestStandingTile - range;

            Debug.Log(
                $"AI: {aiUnit.DisplayName} wants {target.DisplayName} ({targetHP}) but cannot reach anyone this turn. " +
                $"Reason: nearest valid target, still {shortfall} step(s) short even after moving " +
                $"(distance {decision.DistanceFromBestStandingTile} vs range {range}).",
                aiUnit);
        }
    }
}
