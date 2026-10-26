using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // The player's side of the game between fights: roster, recruit candidates, progress.
    //
    // A plain C# class, like ActionResolver, so the rules (roster cap, deploy cap, who may be
    // recruited after a mission) are testable without a scene. The menu calls in; nothing here
    // knows a menu exists.
    //
    // Split by concern into partial files:
    //   CampaignState.cs            roster, hiring, applying a mission's result
    //   CampaignState.Jobs.cs       jobs, learning, passive slots
    //   CampaignState.Equipment.cs  weapons, inventory, shop
    //   CampaignState.City.cs       districts, story progress, travel
    //   CampaignState.Save.cs       JSON save/load and migration
    public partial class CampaignState
    {
        private readonly CampaignConfig config;
        private readonly List<RosterUnit> roster = new List<RosterUnit>();
        private readonly List<RosterUnit> recruitPool = new List<RosterUnit>();

        // Weapons owned but not in anyone's hands.
        private readonly List<EquipmentDefinition> inventory = new List<EquipmentDefinition>();

        public IReadOnlyList<EquipmentDefinition> Inventory => inventory;

        public IReadOnlyList<RosterUnit> Roster => roster;
        public IReadOnlyList<RosterUnit> RecruitPool => recruitPool;
        public int MissionsWon { get; private set; }

        public int Credits { get; private set; }

        public int MaxRosterSize => config.MaxRosterSize;
        public int MaxDeployed => config.MaxDeployed;
        public int DeployedCount => roster.Count(unit => unit.Deployed);
        public bool HasRosterSpace => roster.Count < config.MaxRosterSize;

        private CampaignState(CampaignConfig config)
        {
            this.config = config;
        }

        public static CampaignState NewGame(CampaignConfig config)
        {
            CampaignState state = new CampaignState(config);

            foreach (UnitEntry entry in config.StartingRoster)
            {
                if (entry.job == null)
                {
                    continue;
                }

                RosterUnit unit = RosterUnit.CreateFresh(NameOrJob(entry.unitName, entry.job), entry.job, entry.level);
                unit.Deployed = state.DeployedCount < config.MaxDeployed;
                state.roster.Add(unit);
            }

            state.inventory.AddRange(config.StartingInventory.Where(item => item != null));
            state.StartCity();
            state.RefreshRecruitPool();
            return state;
        }

        public List<RosterUnit> GetDeployed()
        {
            return roster.Where(unit => unit.Deployed).ToList();
        }

        // Moves a candidate from the pool into the roster. Undeployed by default, so recruiting
        // never silently changes who goes on the next mission.
        public bool Recruit(RosterUnit candidate)
        {
            int cost = HireCost(candidate);

            if (!HasRosterSpace || Credits < cost || !recruitPool.Remove(candidate))
            {
                return false;
            }

            Credits -= cost;
            roster.Add(candidate);
            return true;
        }

        public int HireCost(RosterUnit candidate)
        {
            return candidate.Level * config.HireCostPerLevel;
        }

        // Adds a unit met in a mission. Separate from Recruit because it does not come from the
        // pool - this is the path a converted or defeated opponent takes.
        public RosterUnit RecruitFromMission(MissionUnitReport report)
        {
            if (!HasRosterSpace || report.IsFromRoster || report.Job == null)
            {
                return null;
            }

            string name = UniqueName(NameOrJob(report.Name, report.Job));

            // Hacked units and fixed-kit jobs keep their job and never earn JP - GAME_DESIGN.md's
            // rule for hacked recruits. A rescued guest joins as an ordinary crew member.
            RosterUnit unit = report.Hacked || report.Job.IsFixedKit
                ? new RosterUnit(name, report.Job, report.Level) { Locked = true }
                : RosterUnit.CreateFresh(name, report.Job, report.Level);

            roster.Add(unit);
            return unit;
        }

        public void Dismiss(RosterUnit unit)
        {
            roster.Remove(unit);
        }

        // False when deploying would exceed the cap. Undeploying always succeeds.
        public bool ToggleDeploy(RosterUnit unit)
        {
            if (!unit.Deployed && DeployedCount >= config.MaxDeployed)
            {
                return false;
            }

            unit.Deployed = !unit.Deployed;
            return true;
        }

        public void RefreshRecruitPool()
        {
            recruitPool.Clear();

            DistrictDefinition district = CurrentDistrict;
            IEnumerable<JobDefinition> source = district != null && district.hireJobs.Count > 0
                ? district.hireJobs
                : config.RecruitableJobs;
            List<JobDefinition> jobs = source.Where(job => job != null).ToList();

            if (jobs.Count == 0)
            {
                return;
            }

            Vector2Int levels = config.RecruitLevelRange;
            int minLevel = Mathf.Max(1, Mathf.Min(levels.x, levels.y));
            int maxLevel = Mathf.Max(minLevel, Mathf.Max(levels.x, levels.y));

            for (int i = 0; i < config.RecruitPoolSize; i++)
            {
                JobDefinition job = jobs[UnityEngine.Random.Range(0, jobs.Count)];
                int level = UnityEngine.Random.Range(minLevel, maxLevel + 1);
                recruitPool.Add(RosterUnit.CreateFresh(UniqueName(PickName()), job, level));
            }
        }

        // Applies a finished mission to the roster, whatever the outcome.
        //
        // Flatlined crew are removed - permanent loss, FFT's crystallised units. Knocked out but not
        // flatlined means they were pulled out in time and survive, even from a defeat or retreat.
        //
        // Progression is kept whatever the outcome too: JP and XP were earned by actions that
        // happened. Each crew member's JP goes to its primary job, and every other surviving crew
        // member in the fight gets a quarter of it in theirs (FFT's sharing; locked units neither
        // give nor take). XP raises the character level, 100 per level.
        public void ApplyResult(MissionResult result)
        {
            List<MissionUnitReport> crew = result.Units
                .Where(report => report.IsFromRoster && !report.Flatlined && !report.RosterSource.Locked)
                .ToList();

            foreach (MissionUnitReport report in crew)
            {
                RosterUnit unit = report.RosterSource;
                unit.GrantJP(unit.Job, report.EarnedJP);

                foreach (MissionUnitReport other in crew)
                {
                    if (other != report)
                    {
                        int share = report.EarnedJP / Progression.JPShareDivisor;
                        other.RosterSource.GrantJP(other.RosterSource.Job, share);
                        other.SharedJP += share;
                    }
                }

                unit.XP += report.EarnedXP;

                while (unit.XP >= Progression.XPPerLevel)
                {
                    unit.XP -= Progression.XPPerLevel;
                    unit.Level++;
                    report.LevelsGained++;
                }
            }

            foreach (MissionUnitReport report in result.Units)
            {
                if (report.IsFromRoster && report.Flatlined)
                {
                    roster.Remove(report.RosterSource);
                }
            }

            result.WasRoadEncounter = TravelDestination != null;

            // Every mission moves the job boards on and buys a quiet trip.
            BoardVersion++;
            SafeTrips = SafeTripsAfterAFight;

            if (result.Outcome == MissionOutcome.Victory)
            {
                MissionsWon++;
                ApplyVictoryRewards(result);
            }

            // Won or not, the road encounter is over: winners arrive, anyone else is back where
            // they set out from.
            if (result.WasRoadEncounter)
            {
                if (result.Outcome == MissionOutcome.Victory)
                {
                    ArriveAt(TravelDestination);
                }
                else
                {
                    TravelDestination = null;
                }
            }
        }

        private void ApplyVictoryRewards(MissionResult result)
        {
            MissionDefinition mission = result.Mission;

            result.CreditsEarned = result.WasRoadEncounter ? mission.credits / 2 : mission.credits;
            Credits += result.CreditsEarned;

            // Loot is the contract's promised pay; a road ambush has none.
            if (!result.WasRoadEncounter)
            {
                foreach (EquipmentDefinition item in mission.loot.Where(item => item != null))
                {
                    inventory.Add(item);
                    result.LootGained.Add(item);
                }
            }

            if (mission.kind != MissionKind.Story || result.WasRoadEncounter)
            {
                return;
            }

            finishedStory.Add(mission.missionName);

            if (!string.IsNullOrEmpty(mission.unlocksDistrict) && openDistricts.Add(mission.unlocksDistrict))
            {
                result.DistrictUnlocked = mission.unlocksDistrict;
            }

            // Story guests stay. They join like a rescued guest - ordinary crew, not locked - and
            // ignore the roster cap: the story does not ask whether there is room. A guest who
            // flatlined still joins: the story needs them, and the fight was won. (FFT's story
            // guests cannot be lost either.)
            foreach (MissionUnitReport report in result.Units)
            {
                if (!report.IsFromRoster && report.Job != null
                    && report.FinalFaction == Faction.Player && mission.joinsCrew.Contains(report.Name))
                {
                    RosterUnit unit = RosterUnit.CreateFresh(UniqueName(report.Name), report.Job, report.Level);
                    roster.Add(unit);
                    report.JoinedCrew = true;
                    result.JoinedCrew.Add(unit.Name);
                }
            }
        }

        // Who may join after a mission: anyone who ended it on the player's side and was not
        // flatlined - in practice, whoever was hacked with System Override. FFT's Invite rule: the
        // recruit is won in the fight, not handed out by the result screen. (Before Override
        // existed, any opponent could join after a victory; that stand-in is gone.)
        public static bool CanRecruitAfter(MissionResult result, MissionUnitReport report)
        {
            return !report.IsFromRoster && !report.Flatlined && !report.JoinedCrew && report.FinalFaction == Faction.Player;
        }

        private string PickName()
        {
            IReadOnlyList<string> names = config.RecruitNames;
            List<string> unused = names.Where(candidate => !NameTaken(candidate)).ToList();

            if (unused.Count > 0)
            {
                return unused[UnityEngine.Random.Range(0, unused.Count)];
            }

            return names.Count > 0 ? names[UnityEngine.Random.Range(0, names.Count)] : "Recruit";
        }

        // Appends a number until the name is free, so two units never share a label in the menus.
        private string UniqueName(string baseName)
        {
            if (!NameTaken(baseName))
            {
                return baseName;
            }

            for (int suffix = 2; ; suffix++)
            {
                string candidate = $"{baseName} {suffix}";

                if (!NameTaken(candidate))
                {
                    return candidate;
                }
            }
        }

        private bool NameTaken(string candidate)
        {
            return roster.Any(unit => unit.Name == candidate) || recruitPool.Any(unit => unit.Name == candidate);
        }

        private static string NameOrJob(string name, JobDefinition job)
        {
            return string.IsNullOrEmpty(name) ? job.JobName : name;
        }

    }
}
