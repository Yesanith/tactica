using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // The targeting preview: what an ability would do, in words, before it is used.
    public partial class ActionResolver
    {
        // One line describing what the ability would do aimed at `center`, for the targeting
        // preview. Reads the same formulas execution uses, so the preview cannot disagree with the
        // result. Area abilities list every unit in the blast, allies flagged.
        public string DescribeOutcome(GridUnit user, Vector2Int center, AbilityDefinition ability)
        {
            if (user == null || ability == null)
            {
                return string.Empty;
            }

            List<GridUnit> affected = GetAffectedUnits(user, center, ability);

            if (affected.Count == 0)
            {
                return $"{ability.AbilityName}: no target";
            }

            if (affected.Count == 1 && GetArea(user, ability) == 0)
            {
                return $"{ability.AbilityName}: {DescribeEffectOn(user, affected[0], ability)}";
            }

            List<string> parts = new List<string>();

            foreach (GridUnit target in affected)
            {
                string ally = target.CurrentFaction == user.CurrentFaction && ability.IsHarmful ? " (ALLY!)" : string.Empty;
                parts.Add($"{target.DisplayName}{ally} {DescribeEffectOn(user, target, ability)}");
            }

            return $"{ability.AbilityName}: " + string.Join(" | ", parts);
        }

        private string DescribeEffectOn(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            int hp = target.CurrentStats.HP;
            int maxHP = target.EffectiveStats.MaxHP;

            switch (ability.EffectType)
            {
                case AbilityEffectType.Damage:
                {
                    int damage = CalculateDamage(user, target, ability);
                    int after = Mathf.Max(0, hp - damage);
                    string ko = after == 0 ? " KO" : string.Empty;
                    string side = GetAttackSide(user, target).ToString().ToLowerInvariant();
                    string rider = ability.InflictsStatus ? $" + {DescribeStatus(ability)}" : string.Empty;
                    return $"{damage} dmg ({GetHitChance(user, target, ability)}% {side}) -> {after}/{maxHP}{ko}{rider}";
                }

                case AbilityEffectType.Heal:
                {
                    int restored = Mathf.Min(GetPower(user, ability), maxHP - hp);
                    return $"+{restored} HP -> {hp + restored}/{maxHP}";
                }

                case AbilityEffectType.Revive:
                    return $"reboot with {Mathf.Clamp(GetPower(user, ability), 1, maxHP)} HP";

                case AbilityEffectType.Override:
                    return $"{GetOverrideChance(user, target, ability)}% to hack";

                default:
                    return $"{DescribeStatus(ability)} for {ability.Duration} turn(s)" +
                           (ability.IsHarmful ? $" ({GetHitChance(user, target, ability)}%)" : string.Empty);
            }
        }

        private static string DescribeStatus(AbilityDefinition ability)
        {
            switch (ability.StatusKind)
            {
                case StatusKind.Stun: return "stun";
                case StatusKind.DamageOverTime: return "damage over time";
                case StatusKind.HealOverTime: return "regen";
                case StatusKind.Taunt: return "taunt";
                case StatusKind.Overwatch: return "overwatch";
                default: return ability.EffectType == AbilityEffectType.Debuff ? "debuff" : "buff";
            }
        }
    }
}
