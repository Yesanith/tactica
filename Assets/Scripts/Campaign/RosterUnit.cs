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
        public JobDefinition Job;
        public int Level;

        // Whether this unit goes into the next mission. Lives on the unit so it survives a trip
        // through the menus and a save.
        public bool Deployed;

        public RosterUnit(string name, JobDefinition job, int level)
        {
            Name = name;
            Job = job;
            Level = level;
        }

        public string JobName => Job != null ? Job.JobName : "(no job)";

        public CharacterStats Stats => Job != null ? Job.GetStatsAtLevel(Level) : default;
    }
}
