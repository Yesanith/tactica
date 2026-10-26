using System.Collections.Generic;
using System.Linq;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // A unit that belongs to the player between fights. Plain data: it becomes a GridUnit only
    // for the length of a mission, and nothing that happens to that GridUnit flows back here
    // except what CampaignState applies explicitly from a MissionResult.
    //
    // No HP. Pools are refilled at every spawn, so carrying damage between missions would need a
    // rest/heal system to make it mean anything.
    public class RosterUnit
    {
        public string Name;

        // The primary job: stats, MP, weapon permissions and Tech scaling all come from it.
        public JobDefinition Job;

        // Adds its learned Action abilities to the Act menu and nothing else - GAME_DESIGN.md's
        // hybrid rule. Null for none.
        public JobDefinition SecondaryJob;

        // Character level, from XP. Drives stats and the JP bonus; job levels come from JP.
        public int Level;
        public int XP;

        // Whether this unit goes into the next mission. Lives on the unit so it survives a trip
        // through the menus and a save.
        public bool Deployed;

        // Hacked recruits and fixed-kit jobs: the job cannot change, no JP is earned, no slots.
        // FFT treats recruited monsters the same way.
        public bool Locked;

        // The three passive slots, copied onto the GridUnit at spawn. Null for empty.
        public AbilityDefinition Reaction;
        public AbilityDefinition Support;
        public AbilityDefinition Movement;

        // The weapon in hand. Null fights unarmed - the basic Attack then deals only Strength.
        public EquipmentDefinition Weapon;

        private readonly Dictionary<JobDefinition, JobProgress> progress = new Dictionary<JobDefinition, JobProgress>();
        private readonly HashSet<AbilityDefinition> learned = new HashSet<AbilityDefinition>();

        public RosterUnit(string name, JobDefinition job, int level)
        {
            Name = name;
            Job = job;
            Level = level;
        }

        // A new crew member: the job's cheapest Action ability already learned, as the design
        // says starting crew and hires come - so nobody ever deploys with an empty Act menu.
        public static RosterUnit CreateFresh(string name, JobDefinition job, int level)
        {
            RosterUnit unit = new RosterUnit(name, job, level) { Weapon = job.DefaultWeapon };

            AbilityDefinition starter = job.AllAbilities()
                .Where(ability => ability.Slot == AbilitySlot.Action)
                .OrderBy(ability => ability.JPCost)
                .FirstOrDefault();

            if (starter != null)
            {
                unit.learned.Add(starter);
            }

            return unit;
        }

        public string JobName => Job != null ? Job.JobName : "(no job)";

        // Whether this unit may hold a weapon type: its primary job allows it, or its Support slot
        // carries the matching Equip ability.
        public bool CanEquip(WeaponType type)
        {
            if (type == WeaponType.None)
            {
                return true;
            }

            return (Job != null && Job.AllowsWeapon(type)) || (Support != null && Support.EquipWeapon == type);
        }

        public CharacterStats Stats => Job != null ? Job.GetStatsAtLevel(Level) : default;

        public IReadOnlyCollection<AbilityDefinition> Learned => learned;

        public IEnumerable<KeyValuePair<JobDefinition, JobProgress>> AllProgress => progress;

        public JobProgress ProgressFor(JobDefinition job)
        {
            if (!progress.TryGetValue(job, out JobProgress entry))
            {
                entry = new JobProgress();
                progress[job] = entry;
            }

            return entry;
        }

        public int JobLevel(JobDefinition job)
        {
            return job != null && progress.TryGetValue(job, out JobProgress entry)
                ? Progression.JobLevelFor(entry.TotalJP)
                : 0;
        }

        // Earned JP goes to both pools: lifetime (job level) and spendable.
        public void GrantJP(JobDefinition job, int amount)
        {
            if (job == null || amount <= 0 || Locked)
            {
                return;
            }

            JobProgress entry = ProgressFor(job);
            entry.TotalJP += amount;
            entry.AvailableJP += amount;
        }

        public bool HasLearned(AbilityDefinition ability)
        {
            return ability != null && learned.Contains(ability);
        }

        // For loading a save and for CampaignState's purchase rule; no JP check here.
        public void MarkLearned(AbilityDefinition ability)
        {
            if (ability != null)
            {
                learned.Add(ability);
            }
        }

        // What the unit can do in a fight. Locked units use their job's fixed kit by level, like
        // an enemy; everyone else uses what they have learned in their primary job, then the
        // secondary job's learned actives.
        public List<AbilityDefinition> ActionLoadout()
        {
            if (Locked || Job == null)
            {
                return Job != null
                    ? Job.GetUnlockedAbilities(Level).Where(a => a.Slot == AbilitySlot.Action).ToList()
                    : new List<AbilityDefinition>();
            }

            List<AbilityDefinition> loadout = LearnedActions(Job).ToList();

            if (SecondaryJob != null && SecondaryJob != Job)
            {
                loadout.AddRange(LearnedActions(SecondaryJob).Where(a => !loadout.Contains(a)));
            }

            return loadout;
        }

        // In the job's authored order, so the Act menu is stable from fight to fight.
        private IEnumerable<AbilityDefinition> LearnedActions(JobDefinition job)
        {
            return job.AllAbilities().Where(a => a.Slot == AbilitySlot.Action && learned.Contains(a));
        }

        public class JobProgress
        {
            // Every JP ever earned in the job. Decides the job level; never decreases.
            public int TotalJP;

            // What is left to spend on abilities.
            public int AvailableJP;
        }
    }
}
