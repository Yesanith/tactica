using System.Collections.Generic;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Grid
{
    // Timed effects on the unit - buffs, debuffs, stun, damage and healing over time, taunt.
    // Durations count the affected unit's own turns; CombatManager calls TickStatuses at the
    // start of each. Stat-changing statuses fold in through RecalculateStats.
    public partial class GridUnit
    {
        // One status on this unit: which ability applied it, who applied it, how strong, how long.
        public class ActiveStatus
        {
            public readonly AbilityDefinition Ability;
            public readonly GridUnit Source;
            public readonly int Power;
            public int TurnsLeft;

            public ActiveStatus(AbilityDefinition ability, GridUnit source, int power, int turns)
            {
                Ability = ability;
                Source = source;
                Power = power;
                TurnsLeft = turns;
            }

            public StatusKind Kind => Ability.StatusKind;
        }

        private readonly List<ActiveStatus> statuses = new List<ActiveStatus>();
        public IReadOnlyList<ActiveStatus> Statuses => statuses;

        public bool IsStunned => HasStatus(StatusKind.Stun);

        // Whether this exact ability's status is already on the unit - so the AI does not reapply
        // a buff that is still running.
        public bool HasStatusFrom(AbilityDefinition ability)
        {
            return statuses.Exists(status => status.Ability == ability);
        }

        // The first status of this kind, or null.
        public ActiveStatus FindStatus(StatusKind kind)
        {
            return statuses.Find(status => status.Kind == kind);
        }

        // Ends one status early - an Overwatch that has fired.
        public void RemoveStatus(ActiveStatus status)
        {
            if (status != null && statuses.Remove(status))
            {
                RecalculateStats();
            }
        }

        public bool HasStatus(StatusKind kind)
        {
            return statuses.Exists(status => status.Kind == kind);
        }

        // The unit that taunted this one, if a Taunt is active - the AI must go for it.
        public GridUnit Taunter
        {
            get
            {
                foreach (ActiveStatus status in statuses)
                {
                    if (status.Kind == StatusKind.Taunt && status.Source != null && !status.Source.IsKnockedOut)
                    {
                        return status.Source;
                    }
                }

                return null;
            }
        }

        // Applies a status from an ability. The same ability from the same source refreshes its
        // duration instead of stacking - otherwise spamming one debuff would scale without limit.
        public void AddStatus(AbilityDefinition ability, GridUnit source, int power)
        {
            if (ability == null || IsKnockedOut)
            {
                return;
            }

            statuses.RemoveAll(existing => existing.Ability == ability && existing.Source == source);
            statuses.Add(new ActiveStatus(ability, source, power, Mathf.Max(1, ability.Duration)));
            RecalculateStats();
        }

        // Start of one of this unit's turns: damage and healing over time land, then every status
        // counts down and expired ones drop. Returns a line for the log, or null if nothing happened.
        // Called by CombatManager before the turn is handed over - a stunned unit still ticks.
        public string TickStatuses()
        {
            if (statuses.Count == 0)
            {
                return null;
            }

            List<string> events = new List<string>();

            foreach (ActiveStatus status in statuses.ToArray())
            {
                if (status.Kind == StatusKind.DamageOverTime && !IsKnockedOut)
                {
                    ApplyDamage(status.Power);
                    events.Add($"{status.Ability.AbilityName} -{status.Power} HP");
                }
                else if (status.Kind == StatusKind.HealOverTime && !IsKnockedOut)
                {
                    Heal(status.Power);
                    events.Add($"{status.Ability.AbilityName} +{status.Power} HP");
                }

                status.TurnsLeft--;
            }

            int removed = statuses.RemoveAll(status => status.TurnsLeft <= 0 || IsKnockedOut);

            if (removed > 0)
            {
                events.Add($"{removed} status(es) expired");
                RecalculateStats();
            }

            return events.Count > 0 ? $"{DisplayName}: {string.Join(", ", events)}." : null;
        }

        // Short tags for the status bar: "BRACED  Lag Spike 2".
        public string DescribeStatuses()
        {
            List<string> tags = new List<string>();

            if (IsDefending)
            {
                tags.Add("BRACED");
            }

            foreach (ActiveStatus status in statuses)
            {
                tags.Add($"{status.Ability.AbilityName} {status.TurnsLeft}");
            }

            return string.Join("  ", tags);
        }
    }
}
