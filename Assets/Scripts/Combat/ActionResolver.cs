using System;
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
        // Raised after an action has finished mutating stats. An event rather than a direct call
        // into CombatHUD on purpose: this class is a plain C# object constructible in a test
        // without a scene, and holding a reference to a MonoBehaviour would end that. Listeners
        // decide what "something changed" means to them - HUD refresh today, floating combat text
        // and audio later - without this class learning about any of them.
        //
        // Coarse by design: it says stats changed, not which. Panels re-read whatever they display,
        // which is cheap and cannot go out of sync with a more precise payload.
        public event Action OnStatsChanged;

        // Override can never be a sure thing nor a lost cause: a 100% hack would make every enemy a
        // future recruit, and a 0% one is a button that only wastes MP.
        public const int MinOverrideChance = 5;
        public const int MaxOverrideChance = 95;

        // Seedable so a test can pin the Override roll. Owned here rather than using
        // UnityEngine.Random, which would tie the resolver to engine state it otherwise never needs.
        private readonly System.Random random;

        public ActionResolver(int? seed = null)
        {
            random = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
        }

        // Distance is Manhattan (orthogonal) steps, ignoring terrain and obstacles - the same
        // metric movement uses. See GridManager.GetGridDistance for why the move-range flood fill
        // is still the wrong tool here.
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
        // Three cases, in order: the unit itself, someone on its own side, someone on another.
        // Self is tested first because a unit shares its own faction, so without that check
        // TargetsAllies alone would make every ability self-targetable.
        //
        // TargetsAllies is now reachable - an ally-only heal finds its target. Under the old
        // everyone-is-an-enemy placeholder it could hit nobody but the caster.
        //
        // NEUTRAL IS HOSTILE TO EVERYONE, on purpose. "Different faction" is the only rule, so a
        // Neutral is a legal target for both sides and targets both. Neutral is for genuine third
        // parties - rogue drones, a feral AI - which is exactly that behaviour.
        //
        // Civilians and escorts are NOT Neutral: they are guests (GridUnit.IsGuest), on the Player
        // side and driven by the AI, as in FFT. That split is why no faction relationship table is
        // needed - "who may hit the civilian" is answered by the civilian being on your side.
        public bool IsValidTarget(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return false;
            }

            // Flatlined units are off the board. Otherwise the knocked out are valid ONLY for
            // Revive, and Revive is valid only for them - this is the single place that rule lives,
            // so the player's highlight, the AI's search and execution all agree.
            if (target.IsFlatlined)
            {
                return false;
            }

            bool wantsDowned = ability.EffectType == AbilityEffectType.Revive;

            if (target.IsKnockedOut != wantsDowned)
            {
                return false;
            }

            if (user == target)
            {
                return ability.TargetsSelf;
            }

            if (user.CurrentFaction == target.CurrentFaction)
            {
                return ability.TargetsAllies;
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

        // Where the user would strike the target from, relative to the target's facing.
        public AttackSide GetAttackSide(GridUnit user, GridUnit target)
        {
            if (user == null || target == null || user == target)
            {
                return AttackSide.Front;
            }

            return FacingUtility.SideOf(user.GridCoords, target.GridCoords, target.CurrentFacing);
        }

        // Chance, in percent, that a damaging ability connects. FFT's facing rule, simplified:
        // the target's Evasion applies in full from the front, halved from the side, not at all
        // from behind. A defending target's evasion doubles, as FFT's Defend does. Non-damage
        // effects always land (Override has its own roll).
        public int GetHitChance(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return 0;
            }

            if (ability.EffectType != AbilityEffectType.Damage || user == target)
            {
                return 100;
            }

            int evasion = target.EffectiveStats.Evasion;

            if (target.IsDefending)
            {
                evasion *= 2;
            }

            switch (GetAttackSide(user, target))
            {
                case AttackSide.Side: evasion /= 2; break;
                case AttackSide.Back: evasion = 0; break;
            }

            return Mathf.Clamp(100 - evasion, 0, 100);
        }

        // Chance, in percent, that an Override lands. Public and side-effect free so the HUD can
        // show it before the player commits.
        public int GetOverrideChance(GridUnit user, AbilityDefinition ability)
        {
            if (user == null || ability == null)
            {
                return 0;
            }

            return Mathf.Clamp(ability.Power + user.EffectiveStats.Magic, MinOverrideChance, MaxOverrideChance);
        }

        // One line describing what the ability would do to this target, for the targeting preview.
        // Reads the same formulas execution uses, so the preview cannot disagree with the result.
        public string DescribeOutcome(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return string.Empty;
            }

            int hp = target.CurrentStats.HP;
            int maxHP = target.EffectiveStats.MaxHP;

            switch (ability.EffectType)
            {
                case AbilityEffectType.Damage:
                {
                    int damage = CalculateDamage(user, target, ability);
                    int after = Mathf.Max(0, hp - damage);
                    string ko = after == 0 ? "  KO" : string.Empty;
                    string side = GetAttackSide(user, target).ToString().ToLowerInvariant();
                    return $"{ability.AbilityName}: {damage} dmg ({GetHitChance(user, target, ability)}% hit, {side})" +
                           $"  ->  {target.DisplayName} {after}/{maxHP}{ko}";
                }

                case AbilityEffectType.Heal:
                {
                    int restored = Mathf.Min(ability.Power, maxHP - hp);
                    return $"{ability.AbilityName}: +{restored} HP  ->  {target.DisplayName} {hp + restored}/{maxHP}";
                }

                case AbilityEffectType.Revive:
                    return $"{ability.AbilityName}: reboot {target.DisplayName} with {Mathf.Clamp(ability.Power, 1, maxHP)} HP";

                case AbilityEffectType.Override:
                    return $"{ability.AbilityName}: {GetOverrideChance(user, ability)}% to hack {target.DisplayName}";

                default:
                    return $"{ability.AbilityName}: {ability.EffectType} (not implemented)";
            }
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
                resultMessage = $"{user.DisplayName} cannot target {target.DisplayName} with {ability.AbilityName}.";
                return false;
            }

            if (!IsTargetInRange(user, target, ability))
            {
                int distance = GridManager.GetGridDistance(user.GridCoords, target.GridCoords);
                resultMessage = $"{target.DisplayName} is out of range for {ability.AbilityName} " +
                                $"(distance {distance}, range {ability.Range}).";
                return false;
            }

            if (user.CurrentStats.MP < ability.MPCost)
            {
                resultMessage = $"{user.DisplayName} lacks MP for {ability.AbilityName} " +
                                $"({user.CurrentStats.MP}/{ability.MPCost}).";
                return false;
            }

            user.SpendMP(ability.MPCost);

            // The attacker turns to its target as it acts. Done after validation so a rejected
            // ability does not swing the unit around, and before the hit roll - the attacker's own
            // facing does not affect it, but this keeps the visual and the result in step.
            if (target != user)
            {
                user.FaceTowards(target.GridCoords);
            }

            switch (ability.EffectType)
            {
                case AbilityEffectType.Damage:
                {
                    // A miss still spends the action and MP, like a failed hack.
                    int hitChance = GetHitChance(user, target, ability);

                    if (random.Next(100) >= hitChance)
                    {
                        resultMessage = $"{user.DisplayName}'s {ability.AbilityName} misses {target.DisplayName} " +
                                        $"({hitChance}% from the {GetAttackSide(user, target).ToString().ToLowerInvariant()}).";
                        break;
                    }

                    int damage = CalculateDamage(user, target, ability);
                    target.ApplyDamage(damage);

                    resultMessage = $"{user.DisplayName} hits {target.DisplayName} with {ability.AbilityName} for {damage} damage. " +
                                    $"({target.DisplayName}: {target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP)";

                    // Separate line rather than folded into the damage text: a knockout is the
                    // event other systems care about, and it should be greppable in the console
                    // without parsing an HP total out of a sentence.
                    if (target.IsKnockedOut)
                    {
                        resultMessage += $" {target.DisplayName} is knocked out!";
                        Debug.Log($"KO: {target.DisplayName} was knocked out by {user.DisplayName} ({ability.AbilityName}).");
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

                    resultMessage = $"{user.DisplayName} heals {target.DisplayName} with {ability.AbilityName} for {restored} HP. " +
                                    $"({target.DisplayName}: {target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP)";
                    break;
                }

                case AbilityEffectType.Revive:
                {
                    target.Revive(ability.Power);

                    resultMessage = $"{user.DisplayName} reboots {target.DisplayName} with {ability.AbilityName}. " +
                                    $"({target.DisplayName}: {target.CurrentStats.HP}/{target.EffectiveStats.MaxHP} HP)";
                    break;
                }

                case AbilityEffectType.Override:
                {
                    // A failed hack still spends the action and the MP - the attempt is the action,
                    // as with FFT's Invite. "Succeeded" from this method means "was carried out".
                    int chance = GetOverrideChance(user, ability);
                    bool hacked = random.Next(100) < chance;

                    if (hacked)
                    {
                        target.SetFaction(user.CurrentFaction);
                        resultMessage = $"{user.DisplayName} overrides {target.DisplayName} ({chance}%) - " +
                                        $"it now fights for {user.CurrentFaction}.";
                    }
                    else
                    {
                        resultMessage = $"{user.DisplayName}'s {ability.AbilityName} fails against " +
                                        $"{target.DisplayName} ({chance}%).";
                    }

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

            // After the switch, so listeners observe the finished result rather than a half-applied
            // one. Not raised on any rejection path above - nothing changed there.
            OnStatsChanged?.Invoke();

            return true;
        }
    }
}
