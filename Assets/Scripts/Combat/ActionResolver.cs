using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Rules for using an ability: who can be hit, from how far, for how much.
    //
    // Plain C# class, not a MonoBehaviour - it holds no state and touches no scene objects beyond
    // the units handed to it. That makes it constructible in a test without a scene, which is the
    // main reason to keep combat maths out of components.
    public class ActionResolver
    {
        // Distance is Chebyshev (8-directional) steps, ignoring terrain and obstacles - a
        // diagonal neighbour is range 1, same as an orthogonal one. See
        // GridManager.GetGridDistance for why the move-range flood fill is the wrong tool here.
        // Range 0 means self-only, since only the user is at distance 0 from itself.
        public bool IsTargetInRange(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return false;
            }

            return GridManager.GetGridDistance(user.GridCoords, target.GridCoords) <= ability.Range;
        }

        // Checks the ability's targeting flags against the user/target relationship.
        //
        // PLACEHOLDER: there is no faction or team data yet, so every unit that is not the user is
        // treated as an enemy. Two consequences worth knowing before authoring abilities:
        //   - TargetsAllies is currently unreachable. An ability with TargetsAllies but not
        //     TargetsEnemies can only ever hit the user (and only if TargetsSelf is also set), so
        //     ally-only heals cannot be tested until factions exist.
        //   - Nothing stops an area heal from mending the enemy it was aimed past.
        //
        // Real faction checking is the near-term fix: a Faction field on GridUnit (or an owning
        // team reference), with this method comparing user and target factions. Everything else
        // here stays as-is; only this relationship test changes.
        public bool IsValidTarget(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return false;
            }

            if (user == target)
            {
                return ability.TargetsSelf;
            }

            return ability.TargetsEnemies;
        }

        // Flat formula: power + attacker's Strength - defender's Defense, floored at 1.
        //
        // The floor matters for more than tuning. Without it a well-armoured defender produces a
        // negative result, and a negative damage value passed to ApplyDamage would heal the
        // target - an attack that repairs its victim. Flooring at 1 also keeps a chip of progress
        // in every hit, so no matchup can stall forever.
        //
        // Returns 0 for non-damage abilities rather than a nonsense number, so a caller that
        // forgets to branch on EffectType gets nothing rather than something wrong.
        //
        // Note Magic is unused: a fireball currently scales off Strength like a sword swing.
        // Selecting the stat by EffectType (or a per-ability scaling field) is the obvious
        // refinement once there are magic-using jobs to tune.
        public int CalculateDamage(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return 0;
            }

            if (ability.EffectType != AbilityEffectType.Damage)
            {
                return 0;
            }

            int raw = ability.Power + user.EffectiveStats.Strength - target.EffectiveStats.Defense;

            return Mathf.Max(1, raw);
        }

        // Validates and applies an ability. resultMessage always explains the outcome, whether it
        // succeeded or why it did not, so callers can surface it without re-deriving the reason.
        //
        // Validation runs before any mutation: MP is only spent once the action is known to be
        // legal, so a rejected ability costs nothing.
        public bool TryExecuteAbility(GridUnit user, GridUnit target, AbilityDefinition ability, out string resultMessage)
        {
            if (user == null || target == null || ability == null)
            {
                resultMessage = "Invalid action: user, target, or ability is missing.";
                return false;
            }

            if (!IsValidTarget(user, target, ability))
            {
                resultMessage = $"{user.name} cannot target {target.name} with {ability.AbilityName}.";
                return false;
            }

            if (!IsTargetInRange(user, target, ability))
            {
                int distance = GridManager.GetGridDistance(user.GridCoords, target.GridCoords);
                resultMessage = $"{target.name} is out of range for {ability.AbilityName} " +
                                $"(distance {distance}, range {ability.Range}).";
                return false;
            }

            if (user.CurrentStats.MP < ability.MPCost)
            {
                resultMessage = $"{user.name} lacks MP for {ability.AbilityName} " +
                                $"({user.CurrentStats.MP}/{ability.MPCost}).";
                return false;
            }

            user.CurrentStats.MP -= ability.MPCost;

            switch (ability.EffectType)
            {
                case AbilityEffectType.Damage:
                {
                    int damage = CalculateDamage(user, target, ability);
                    target.ApplyDamage(damage);

                    resultMessage = $"{user.name} hits {target.name} with {ability.AbilityName} for {damage} damage. " +
                                    $"({target.name}: {target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP)";

                    // Separate line rather than folded into the damage text: a knockout is the
                    // event other systems care about, and it should be greppable in the console
                    // without parsing an HP total out of a sentence.
                    if (target.IsKnockedOut)
                    {
                        resultMessage += $" {target.name} is knocked out!";
                        Debug.Log($"KO: {target.name} was knocked out by {user.name} ({ability.AbilityName}).");
                    }

                    break;
                }

                case AbilityEffectType.Heal:
                {
                    // Measured either side of the call rather than trusting Power, so the message
                    // reports what actually landed after the MaxHP cap.
                    int before = target.CurrentStats.HP;
                    target.Heal(ability.Power);
                    int restored = target.CurrentStats.HP - before;

                    resultMessage = $"{user.name} heals {target.name} with {ability.AbilityName} for {restored} HP. " +
                                    $"({target.name}: {target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP)";
                    break;
                }

                default:
                {
                    // Buff/Debuff/StatusEffect have no modifier system to apply yet. MP is still
                    // spent, because validation already passed and refunding here would make
                    // "succeeded" mean two different things.
                    resultMessage = $"{ability.EffectType} effects are not yet implemented " +
                                    $"({ability.AbilityName}). MP was still spent.";

                    Debug.LogWarning(resultMessage);
                    break;
                }
            }

            return true;
        }
    }
}
