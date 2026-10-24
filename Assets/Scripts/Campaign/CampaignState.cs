using System;
using System.Collections.Generic;
using System.IO;
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
    // Persisted as JSON in persistentDataPath. Jobs are saved by asset name and resolved through
    // CampaignConfig on load, since a ScriptableObject reference does not survive JsonUtility.
    public class CampaignState
    {
        private const string SaveFileName = "campaign.json";

        private readonly CampaignConfig config;
        private readonly List<RosterUnit> roster = new List<RosterUnit>();
        private readonly List<RosterUnit> recruitPool = new List<RosterUnit>();

        public IReadOnlyList<RosterUnit> Roster => roster;
        public IReadOnlyList<RosterUnit> RecruitPool => recruitPool;
        public int MissionsWon { get; private set; }

        public int MaxRosterSize => config.MaxRosterSize;
        public int MaxDeployed => config.MaxDeployed;
        public int DeployedCount => roster.Count(unit => unit.Deployed);
        public bool HasRosterSpace => roster.Count < config.MaxRosterSize;

        public static string SavePath => Path.Combine(Application.persistentDataPath, SaveFileName);
        public static bool HasSave => File.Exists(SavePath);

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

                RosterUnit unit = new RosterUnit(NameOrJob(entry.unitName, entry.job), entry.job, entry.level);
                unit.Deployed = state.DeployedCount < config.MaxDeployed;
                state.roster.Add(unit);
            }

            state.RefreshRecruitPool();
            return state;
        }

        // Null if there is no save or it cannot be read. A corrupt save is logged and treated as
        // absent rather than thrown, so the main menu still works.
        public static CampaignState Load(CampaignConfig config)
        {
            if (!HasSave)
            {
                return null;
            }

            SaveData data;

            try
            {
                data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{nameof(CampaignState)}: could not read {SavePath}: {exception.Message}");
                return null;
            }

            if (data == null)
            {
                return null;
            }

            CampaignState state = new CampaignState(config);
            state.MissionsWon = data.missionsWon;
            state.RestoreUnits(data.roster, state.roster);
            state.RestoreUnits(data.recruitPool, state.recruitPool);
            return state;
        }

        public void Save()
        {
            SaveData data = new SaveData
            {
                missionsWon = MissionsWon,
                roster = roster.Select(ToSaved).ToList(),
                recruitPool = recruitPool.Select(ToSaved).ToList(),
            };

            try
            {
                File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{nameof(CampaignState)}: could not write {SavePath}: {exception.Message}");
            }
        }

        public List<RosterUnit> GetDeployed()
        {
            return roster.Where(unit => unit.Deployed).ToList();
        }

        // Moves a candidate from the pool into the roster. Undeployed by default, so recruiting
        // never silently changes who goes on the next mission.
        public bool Recruit(RosterUnit candidate)
        {
            if (!HasRosterSpace || !recruitPool.Remove(candidate))
            {
                return false;
            }

            roster.Add(candidate);
            return true;
        }

        // Adds a unit met in a mission. Separate from Recruit because it does not come from the
        // pool - this is the path a converted or defeated opponent takes.
        public RosterUnit RecruitFromMission(MissionUnitReport report)
        {
            if (!HasRosterSpace || report.IsFromRoster || report.Job == null)
            {
                return null;
            }

            RosterUnit unit = new RosterUnit(UniqueName(NameOrJob(report.Name, report.Job)), report.Job, report.Level);
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

        // Debug control for testing stat growth; there is no XP to earn levels with yet.
        public void ChangeLevel(RosterUnit unit, int delta)
        {
            unit.Level = Mathf.Max(1, unit.Level + delta);
        }

        public void RefreshRecruitPool()
        {
            recruitPool.Clear();

            List<JobDefinition> jobs = config.RecruitableJobs.Where(job => job != null).ToList();

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
                recruitPool.Add(new RosterUnit(UniqueName(PickName()), job, level));
            }
        }

        // Applies a finished mission to the roster, whatever the outcome.
        //
        // Flatlined crew are removed - permanent loss, FFT's crystallised units. Knocked out but not
        // flatlined means they were pulled out in time and survive, even from a defeat or retreat.
        //
        // PLACEHOLDER progression: a won contract gives every deployed unit still standing one
        // level, standing in for an XP/JP system.
        public void ApplyResult(MissionResult result)
        {
            foreach (MissionUnitReport report in result.Units)
            {
                if (report.IsFromRoster && report.Flatlined)
                {
                    roster.Remove(report.RosterSource);
                }
            }

            if (result.Outcome != MissionOutcome.Victory)
            {
                return;
            }

            MissionsWon++;

            foreach (MissionUnitReport report in result.Units)
            {
                if (report.IsFromRoster && !report.KnockedOut)
                {
                    report.RosterSource.Level++;
                }
            }
        }

        // Who may join after a mission: anyone who ended it on the player's side and was not
        // flatlined - in practice, whoever was hacked with System Override. FFT's Invite rule: the
        // recruit is won in the fight, not handed out by the result screen. (Before Override
        // existed, any opponent could join after a victory; that stand-in is gone.)
        public static bool CanRecruitAfter(MissionResult result, MissionUnitReport report)
        {
            return !report.IsFromRoster && !report.Flatlined && report.FinalFaction == Faction.Player;
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

        private void RestoreUnits(List<SavedUnit> saved, List<RosterUnit> into)
        {
            if (saved == null)
            {
                return;
            }

            foreach (SavedUnit entry in saved)
            {
                JobDefinition job = config.FindJob(entry.job);

                // Dropped rather than kept jobless: a unit with no job has zero stats and cannot
                // act, so loading it would only produce a unit that breaks the next mission.
                if (job == null)
                {
                    Debug.LogWarning(
                        $"{nameof(CampaignState)}: job '{entry.job}' for '{entry.name}' is not in the " +
                        $"{nameof(CampaignConfig)}. Unit dropped from the save.");
                    continue;
                }

                into.Add(new RosterUnit(entry.name, job, Mathf.Max(1, entry.level)) { Deployed = entry.deployed });
            }
        }

        private static SavedUnit ToSaved(RosterUnit unit)
        {
            return new SavedUnit
            {
                name = unit.Name,
                job = unit.Job != null ? unit.Job.name : string.Empty,
                level = unit.Level,
                deployed = unit.Deployed,
            };
        }

        // JsonUtility needs public fields on [Serializable] classes - no properties, no
        // dictionaries - hence this mirror of the runtime types.
        [Serializable]
        private class SaveData
        {
            public int missionsWon;
            public List<SavedUnit> roster = new List<SavedUnit>();
            public List<SavedUnit> recruitPool = new List<SavedUnit>();
        }

        [Serializable]
        private class SavedUnit
        {
            public string name;
            public string job;
            public int level;
            public bool deployed;
        }
    }
}
