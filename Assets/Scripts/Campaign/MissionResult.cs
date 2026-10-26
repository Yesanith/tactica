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

        // Started on another side and ended on the Player side - hacked, not a guest. Hacked
        // recruits join Locked (fixed job, no JP), as GAME_DESIGN.md specifies.
        public bool Hacked;

        // Progression from this fight, before sharing. Filled by MissionRunner from effective
        // actions; zero for opponents.
        public int EarnedJP;
        public int EarnedXP;

        // Filled by CampaignState.ApplyResult, for the result screen.
        public int SharedJP;
        public int LevelsGained;

        // A story guest who stayed with the crew (MissionDefinition.joinsCrew).
        public bool JoinedCrew;

        // The roster entry this unit was spawned from, or null for an opponent.
        public RosterUnit RosterSource;

        public bool IsFromRoster => RosterSource != null;
    }

    public class MissionResult
    {
        public MissionDefinition Mission;
        public MissionOutcome Outcome;

        // Filled by CampaignState.ApplyResult, for the result screen.
        public bool WasRoadEncounter;
        public int CreditsEarned;
        public List<Tactica.Stats.EquipmentDefinition> LootGained = new List<Tactica.Stats.EquipmentDefinition>();
        public List<string> JoinedCrew = new List<string>();
        public string DistrictUnlocked;
        public List<MissionUnitReport> Units = new List<MissionUnitReport>();
    }
}
