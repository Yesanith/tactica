using System.Collections.Generic;
using System.Linq;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Jobs, abilities and passive slots - the unit screen's rules. Kept here rather than in the
    // UI so they can be tested, and so no menu can bypass them. Mostly static: they read and
    // change one RosterUnit and need nothing else from the campaign.
    public partial class CampaignState
    {
        public IEnumerable<JobDefinition> PlayerJobs => config.PlayerJobs();

        // Every prerequisite met. Start-tier jobs have none.
        public static bool IsJobUnlocked(RosterUnit unit, JobDefinition job)
        {
            if (job == null || job.IsFixedKit)
            {
                return false;
            }

            foreach (JobRequirement requirement in job.Prerequisites)
            {
                if (requirement.job != null && unit.JobLevel(requirement.job) < requirement.level)
                {
                    return false;
                }
            }

            return true;
        }

        public static string DescribeRequirements(JobDefinition job)
        {
            List<string> parts = job.Prerequisites.Where(r => r.job != null).Select(r => r.ToString()).ToList();
            return parts.Count > 0 ? string.Join(" + ", parts) : "open";
        }

        // The hybrid rule: two Top-tier jobs may never be combined.
        private static bool BreaksHybridRule(JobDefinition primary, JobDefinition secondary)
        {
            return primary != null && secondary != null
                && primary.Tier == JobTier.Top && secondary.Tier == JobTier.Top;
        }

        public static bool CanSetPrimary(RosterUnit unit, JobDefinition job, out string reason)
        {
            reason = null;

            if (unit.Locked) reason = "Locked units keep their job.";
            else if (!IsJobUnlocked(unit, job)) reason = $"Needs {DescribeRequirements(job)}.";
            else if (BreaksHybridRule(job, unit.SecondaryJob)) reason = "Two top-tier jobs cannot be combined.";

            return reason == null;
        }

        // Switching primary keeps every JP total and learned ability - progress belongs to the
        // unit, the job only decides what it fights as. A secondary equal to the new primary is
        // cleared, since it would add nothing.
        public static bool SetPrimaryJob(RosterUnit unit, JobDefinition job)
        {
            if (!CanSetPrimary(unit, job, out _))
            {
                return false;
            }

            unit.Job = job;

            if (unit.SecondaryJob == job)
            {
                unit.SecondaryJob = null;
            }

            return true;
        }

        public static bool CanSetSecondary(RosterUnit unit, JobDefinition job, out string reason)
        {
            reason = null;

            if (job == null) return !unit.Locked;

            if (unit.Locked) reason = "Locked units have no secondary.";
            else if (job == unit.Job) reason = "Already the primary job.";
            else if (!IsJobUnlocked(unit, job)) reason = $"Needs {DescribeRequirements(job)}.";
            else if (BreaksHybridRule(unit.Job, job)) reason = "Two top-tier jobs cannot be combined.";

            return reason == null;
        }

        public static bool SetSecondaryJob(RosterUnit unit, JobDefinition job)
        {
            if (!CanSetSecondary(unit, job, out _))
            {
                return false;
            }

            unit.SecondaryJob = job;
            return true;
        }

        // Abilities are bought with the JP of the job that teaches them - FFT's rule. Learning in a
        // job does not require it to be the current one, but the job must be unlocked.
        public static bool CanLearn(RosterUnit unit, JobDefinition job, AbilityDefinition ability, out string reason)
        {
            reason = null;

            if (unit.Locked) reason = "Locked units cannot learn.";
            else if (unit.HasLearned(ability)) reason = "Learned.";
            else if (!IsJobUnlocked(unit, job)) reason = $"Needs {DescribeRequirements(job)}.";
            else if (!job.AllAbilities().Contains(ability)) reason = "Not taught by this job.";
            else if (unit.ProgressFor(job).AvailableJP < ability.JPCost) reason = $"{ability.JPCost} JP needed.";

            return reason == null;
        }

        public static bool Learn(RosterUnit unit, JobDefinition job, AbilityDefinition ability)
        {
            if (!CanLearn(unit, job, ability, out _))
            {
                return false;
            }

            unit.ProgressFor(job).AvailableJP -= ability.JPCost;
            unit.MarkLearned(ability);
            return true;
        }

        // Learned abilities that fit a passive slot, in a stable order for the menu.
        public static List<AbilityDefinition> LearnedFor(RosterUnit unit, AbilitySlot slot)
        {
            return unit.Learned.Where(a => a.Slot == slot).OrderBy(a => a.AbilityName).ToList();
        }

        // Sets a passive slot; null empties it. Refuses anything not learned or of another slot.
        public bool EquipPassiveAndValidate(RosterUnit unit, AbilitySlot slot, AbilityDefinition ability)
        {
            bool done = EquipPassive(unit, slot, ability);
            ValidateWeapon(unit);
            return done;
        }

        public static bool EquipPassive(RosterUnit unit, AbilitySlot slot, AbilityDefinition ability)
        {
            if (slot == AbilitySlot.Action || unit.Locked)
            {
                return false;
            }

            if (ability != null && (ability.Slot != slot || !unit.HasLearned(ability)))
            {
                return false;
            }

            switch (slot)
            {
                case AbilitySlot.Reaction: unit.Reaction = ability; break;
                case AbilitySlot.Support: unit.Support = ability; break;
                case AbilitySlot.Movement: unit.Movement = ability; break;
            }

            return true;
        }
    }
}
