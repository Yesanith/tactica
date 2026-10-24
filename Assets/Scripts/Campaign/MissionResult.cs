using System.Collections.Generic;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    public enum MissionOutcome
    {
        Victory,
        Defeat,

        // The player left before the fight was decided. Distinct from Defeat so a future
        // penalty can treat running away differently from being wiped out.
        Retreat,
    }

    // How one unit ended the mission. A snapshot, because the GridUnit it was taken from is
    // destroyed when the board is cleared.
    public class MissionUnitReport
    {
        public string Name;
        public JobDefinition Job;
        public int Level;
        public Faction FinalFaction;
        public bool KnockedOut;

        // The flatline counter ran out: the unit is gone. For a crew member this is permanent -
        // CampaignState removes it from the roster.
        public bool Flatlined;

        // The roster entry this unit was spawned from, or null for an opponent.
        public RosterUnit RosterSource;

        public bool IsFromRoster => RosterSource != null;
    }

    public class MissionResult
    {
        public MissionDefinition Mission;
        public MissionOutcome Outcome;
        public List<MissionUnitReport> Units = new List<MissionUnitReport>();
    }
}
