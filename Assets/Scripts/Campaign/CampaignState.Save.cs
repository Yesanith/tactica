using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Persistence: JSON in persistentDataPath. Jobs and abilities are saved by asset name and
    // resolved through CampaignConfig on load, since a ScriptableObject reference does not survive
    // JsonUtility. The file carries a version; older versions are migrated on load, not discarded.
    public partial class CampaignState
    {
        private const string SaveFileName = "campaign.json";

        // 1: name, job, level, deployed. 2: adds XP, secondary job, per-job JP, learned abilities,
        // passive slots and the locked flag. 3: adds the city map - district, open districts,
        // finished story missions, credits. 4: visited districts, the job-board version and the
        // post-fight safe trips. A v1 file has no version field, which reads as 0.
        private const int SaveVersion = 4;

        public static string SavePath => Path.Combine(Application.persistentDataPath, SaveFileName);
        public static bool HasSave => File.Exists(SavePath);

        // Null if there is no save or it cannot be read. A corrupt save is logged and treated as
        // absent rather than thrown, so the main menu still works.
        public static CampaignState Load(CampaignConfig config)
        {
            if (!HasSave)
            {
                return null;
            }

            string json;

            try
            {
                json = File.ReadAllText(SavePath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{nameof(CampaignState)}: could not read {SavePath}: {exception.Message}");
                return null;
            }

            return FromJson(config, json);
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(SavePath, ToJson());
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{nameof(CampaignState)}: could not write {SavePath}: {exception.Message}");
            }
        }

        // The save format itself, apart from the file - what Load and Save read and write, and what
        // the tests round-trip without touching the player's save. Null for unreadable JSON.
        public static CampaignState FromJson(CampaignConfig config, string json)
        {
            SaveData data;

            try
            {
                data = JsonUtility.FromJson<SaveData>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{nameof(CampaignState)}: unreadable save data: {exception.Message}");
                return null;
            }

            if (data == null)
            {
                return null;
            }

            CampaignState state = new CampaignState(config);
            state.MissionsWon = data.missionsWon;

            // Older files are upgraded rather than thrown away: a v1 unit becomes a fresh v2 unit
            // of the same name, job and level - its starter ability learned, no JP yet.
            bool migrate = data.version < SaveVersion;

            if (migrate)
            {
                Debug.Log($"{nameof(CampaignState)}: migrating save from version {data.version} to {SaveVersion}.");
            }

            // Units changed shape at version 2 only.
            state.RestoreUnits(data.roster, state.roster, data.version < 2);
            state.RestoreUnits(data.recruitPool, state.recruitPool, data.version < 2);

            foreach (string itemName in data.inventory ?? new List<string>())
            {
                EquipmentDefinition item = config.FindEquipment(itemName);

                if (item != null)
                {
                    state.inventory.Add(item);
                }
            }

            if (data.version < 2)
            {
                state.inventory.AddRange(config.StartingInventory.Where(item => item != null));
            }

            if (data.version < 3)
            {
                state.StartCity();
            }
            else
            {
                state.Credits = data.credits;
                state.CurrentDistrictName = data.district;
                state.openDistricts.UnionWith(data.openDistricts ?? new List<string>());
                state.finishedStory.UnionWith(data.finishedStory ?? new List<string>());
                state.visitedDistricts.UnionWith(data.visitedDistricts ?? new List<string>());
                state.BoardVersion = data.boardVersion;
                state.SafeTrips = data.safeTrips;

                // A district renamed or removed from the config would strand the crew nowhere.
                if (state.CurrentDistrict == null)
                {
                    state.CurrentDistrictName = config.StartingDistrict;
                }
            }

            // Before version 4 nothing recorded where the crew had been: the district it is in counts.
            if (!string.IsNullOrEmpty(state.CurrentDistrictName))
            {
                state.visitedDistricts.Add(state.CurrentDistrictName);
            }

            return state;
        }

        public string ToJson()
        {
            SaveData data = new SaveData
            {
                version = SaveVersion,
                missionsWon = MissionsWon,
                inventory = inventory.Select(item => item.name).ToList(),
                roster = roster.Select(ToSaved).ToList(),
                recruitPool = recruitPool.Select(ToSaved).ToList(),
                credits = Credits,
                district = CurrentDistrictName,
                openDistricts = openDistricts.ToList(),
                finishedStory = finishedStory.ToList(),
                visitedDistricts = visitedDistricts.ToList(),
                boardVersion = BoardVersion,
                safeTrips = SafeTrips,
            };

            return JsonUtility.ToJson(data, true);
        }

        private void RestoreUnits(List<SavedUnit> saved, List<RosterUnit> into, bool migrate)
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

                int level = Mathf.Max(1, entry.level);

                if (migrate)
                {
                    RosterUnit fresh = RosterUnit.CreateFresh(entry.name, job, level);
                    fresh.Deployed = entry.deployed;
                    into.Add(fresh);
                    continue;
                }

                RosterUnit unit = new RosterUnit(entry.name, job, level)
                {
                    Deployed = entry.deployed,
                    XP = entry.xp,
                    Locked = entry.locked,
                    SecondaryJob = config.FindJob(entry.secondaryJob),
                    Reaction = config.FindAbility(entry.reaction),
                    Support = config.FindAbility(entry.support),
                    Movement = config.FindAbility(entry.movement),
                    Weapon = config.FindEquipment(entry.weapon),
                };

                foreach (SavedJobProgress savedJob in entry.jobs ?? new List<SavedJobProgress>())
                {
                    JobDefinition progressJob = config.FindJob(savedJob.job);

                    if (progressJob != null)
                    {
                        RosterUnit.JobProgress progress = unit.ProgressFor(progressJob);
                        progress.TotalJP = savedJob.totalJP;
                        progress.AvailableJP = savedJob.availableJP;
                    }
                }

                foreach (string abilityName in entry.learned ?? new List<string>())
                {
                    unit.MarkLearned(config.FindAbility(abilityName));
                }

                into.Add(unit);
            }
        }

        private static SavedUnit ToSaved(RosterUnit unit)
        {
            return new SavedUnit
            {
                name = unit.Name,
                job = NameOf(unit.Job),
                level = unit.Level,
                deployed = unit.Deployed,
                xp = unit.XP,
                locked = unit.Locked,
                secondaryJob = NameOf(unit.SecondaryJob),
                reaction = NameOf(unit.Reaction),
                support = NameOf(unit.Support),
                movement = NameOf(unit.Movement),
                weapon = NameOf(unit.Weapon),
                learned = unit.Learned.Select(ability => ability.name).ToList(),
                jobs = unit.AllProgress.Select(pair => new SavedJobProgress
                {
                    job = pair.Key.name,
                    totalJP = pair.Value.TotalJP,
                    availableJP = pair.Value.AvailableJP,
                }).ToList(),
            };
        }

        private static string NameOf(UnityEngine.Object asset)
        {
            return asset != null ? asset.name : string.Empty;
        }

        // JsonUtility needs public fields on [Serializable] classes - no properties, no
        // dictionaries - hence this mirror of the runtime types.
        [Serializable]
        private class SaveData
        {
            public int version;
            public int missionsWon;
            public List<string> inventory = new List<string>();
            public List<SavedUnit> roster = new List<SavedUnit>();
            public List<SavedUnit> recruitPool = new List<SavedUnit>();

            // Version 3.
            public int credits;
            public string district;
            public List<string> openDistricts = new List<string>();
            public List<string> finishedStory = new List<string>();

            // Version 4.
            public List<string> visitedDistricts = new List<string>();
            public int boardVersion;
            public int safeTrips;
        }

        [Serializable]
        private class SavedUnit
        {
            public string name;
            public string job;
            public int level;
            public bool deployed;

            // Version 2.
            public int xp;
            public bool locked;
            public string secondaryJob;
            public string reaction;
            public string support;
            public string movement;
            public string weapon;
            public List<string> learned = new List<string>();
            public List<SavedJobProgress> jobs = new List<SavedJobProgress>();
        }

        [Serializable]
        private class SavedJobProgress
        {
            public string job;
            public int totalJP;
            public int availableJP;
        }
    }
}
