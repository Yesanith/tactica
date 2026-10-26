using System.Collections.Generic;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Everything the out-of-combat flow needs that is authored rather than computed: which jobs
    // can be recruited, the starting roster, the mission list, and the size limits.
    //
    // Loaded from Resources so the flow can bootstrap itself into any scene with a CombatManager,
    // without a scene reference to wire up. One asset, one path - see ResourcePath.
    [CreateAssetMenu(fileName = "CampaignConfig", menuName = "Tactica/Campaign Config")]
    public class CampaignConfig : ScriptableObject
    {
        // Resources.Load path. The asset must live at Assets/Resources/CampaignConfig.asset.
        public const string ResourcePath = "CampaignConfig";

        [Tooltip("When on, entering Play mode opens the main menu instead of leaving the scene's " +
                 "authored units in place. Turn off to test combat with hand-placed units and the " +
                 "CombatManager context menu.")]
        [SerializeField] private bool startWithMenu = true;

        [Tooltip("Shows test tools in a fight: the faction panel, where any unit's side can be " +
                 "changed by hand. Off for normal play.")]
        [SerializeField] private bool showDebugTools;

        [Header("Roster")]
        [SerializeField] private List<UnitEntry> startingRoster = new List<UnitEntry>();

        [Tooltip("Every job a crew member can take on the Crew screen, in display order. Fixed-kit " +
                 "(enemy) jobs are ignored here even if listed.")]
        [SerializeField] private List<JobDefinition> playerJobs = new List<JobDefinition>();

        [Tooltip("Jobs the recruit screen draws candidates from.")]
        [SerializeField] private List<JobDefinition> recruitableJobs = new List<JobDefinition>();

        [Tooltip("Names handed to new recruits. Reused with a number once exhausted.")]
        [SerializeField] private List<string> recruitNames = new List<string>();

        [Min(1)]
        [SerializeField] private int maxRosterSize = 8;

        [Min(1)]
        [SerializeField] private int maxDeployed = 4;

        [Min(1)]
        [SerializeField] private int recruitPoolSize = 3;

        [Tooltip("Inclusive level range for recruit candidates.")]
        [SerializeField] private Vector2Int recruitLevelRange = new Vector2Int(1, 3);

        [Header("Combat and equipment")]
        [Tooltip("The weapon attack every unit gets as the first entry of its Act menu.")]
        [SerializeField] private AbilityDefinition basicAttack;

        [Tooltip("Every weapon the game knows - saves resolve weapons by name through this list.")]
        [SerializeField] private List<EquipmentDefinition> equipment = new List<EquipmentDefinition>();

        [Tooltip("Spare weapons a new game starts with, besides what each unit holds.")]
        [SerializeField] private List<EquipmentDefinition> startingInventory = new List<EquipmentDefinition>();

        [Header("City map")]
        [SerializeField] private List<DistrictDefinition> districts = new List<DistrictDefinition>();

        [SerializeField] private string startingDistrict;

        [Min(0)]
        [SerializeField] private int startingCredits = 300;

        [Tooltip("Credits per character level to hire a runner.")]
        [Min(0)]
        [SerializeField] private int hireCostPerLevel = 80;

        [Tooltip("Generated contracts on each district's job board, besides the authored ones. " +
                 "They change after every mission.")]
        [Min(0)]
        [SerializeField] private int generatedListings = 2;

        [Tooltip("Job of the contact an escort contract asks you to protect.")]
        [SerializeField] private JobDefinition escortJob;

        [Header("Missions")]
        [SerializeField] private List<MissionDefinition> missions = new List<MissionDefinition>();

        public bool StartWithMenu => startWithMenu;
        public bool ShowDebugTools => showDebugTools;
        public IReadOnlyList<UnitEntry> StartingRoster => startingRoster;
        public IReadOnlyList<JobDefinition> RecruitableJobs => recruitableJobs;

        // The Crew screen's job list. Falls back to every non-fixed job the config mentions, so an
        // older config without the list still offers something.
        public IEnumerable<JobDefinition> PlayerJobs()
        {
            IEnumerable<JobDefinition> source = playerJobs.Count > 0 ? playerJobs : AllJobs();

            HashSet<JobDefinition> seen = new HashSet<JobDefinition>();

            foreach (JobDefinition job in source)
            {
                if (job != null && !job.IsFixedKit && seen.Add(job))
                {
                    yield return job;
                }
            }
        }
        public IReadOnlyList<string> RecruitNames => recruitNames;
        public int MaxRosterSize => maxRosterSize;
        public int MaxDeployed => maxDeployed;
        public int RecruitPoolSize => recruitPoolSize;
        public Vector2Int RecruitLevelRange => recruitLevelRange;
        public IReadOnlyList<MissionDefinition> Missions => missions;
        public AbilityDefinition BasicAttack => basicAttack;
        public IReadOnlyList<DistrictDefinition> Districts => districts;
        public string StartingDistrict => startingDistrict;
        public int StartingCredits => startingCredits;
        public int HireCostPerLevel => hireCostPerLevel;
        public int GeneratedListings => generatedListings;
        public JobDefinition EscortJob => escortJob;

        public DistrictDefinition FindDistrict(string name)
        {
            foreach (DistrictDefinition district in districts)
            {
                if (district != null && district.districtName == name)
                {
                    return district;
                }
            }

            return null;
        }

        public MissionDefinition FindMission(string name)
        {
            foreach (MissionDefinition mission in missions)
            {
                if (mission != null && mission.missionName == name)
                {
                    return mission;
                }
            }

            return null;
        }
        public IReadOnlyList<EquipmentDefinition> Equipment => equipment;
        public IReadOnlyList<EquipmentDefinition> StartingInventory => startingInventory;

        // Opponents' weapon tier by level: Mk I to level 3, Mk II to 6, Mk III after - about when a
        // crew can afford each, so enemies keep pace with the shop instead of swinging Mk I forever.
        public static int WeaponTierFor(int level)
        {
            return level <= 3 ? 1 : level <= 6 ? 2 : 3;
        }

        // The catalogue's weapon of the same type at the tier for this level. The default itself
        // when it is already that good (Chrome Brute's Mk II) or the catalogue has nothing better.
        public EquipmentDefinition WeaponForLevel(EquipmentDefinition weapon, int level)
        {
            int tier = WeaponTierFor(level);

            if (weapon == null || weapon.Tier >= tier)
            {
                return weapon;
            }

            foreach (EquipmentDefinition item in equipment)
            {
                if (item != null && item.Type == weapon.Type && item.Tier == tier)
                {
                    return item;
                }
            }

            return weapon;
        }

        public EquipmentDefinition FindEquipment(string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                return null;
            }

            foreach (EquipmentDefinition item in equipment)
            {
                if (item != null && item.name == assetName)
                {
                    return item;
                }
            }

            // A job's default weapon may not be in the catalogue; look there too.
            foreach (JobDefinition job in EnumerateJobs())
            {
                if (job != null && job.DefaultWeapon != null && job.DefaultWeapon.name == assetName)
                {
                    return job.DefaultWeapon;
                }
            }

            return null;
        }

        // Resolves a saved job by asset name. Searches every job this config mentions, because a
        // unit recruited off a mission can hold a job that is not in recruitableJobs.
        public JobDefinition FindJob(string assetName)
        {
            foreach (JobDefinition job in EnumerateJobs())
            {
                if (job != null && job.name == assetName)
                {
                    return job;
                }
            }

            return null;
        }

        // Resolves a saved ability by asset name, through every job's ability list.
        public AbilityDefinition FindAbility(string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                return null;
            }

            foreach (JobDefinition job in EnumerateJobs())
            {
                if (job == null)
                {
                    continue;
                }

                foreach (AbilityDefinition ability in job.AllAbilities())
                {
                    if (ability.name == assetName)
                    {
                        return ability;
                    }
                }
            }

            return null;
        }

        // Every distinct job this config mentions - recruitable, starting, and opponents.
        public IEnumerable<JobDefinition> AllJobs()
        {
            HashSet<JobDefinition> seen = new HashSet<JobDefinition>();

            foreach (JobDefinition job in EnumerateJobs())
            {
                if (job != null && seen.Add(job))
                {
                    yield return job;
                }
            }
        }

        private IEnumerable<JobDefinition> EnumerateJobs()
        {
            foreach (JobDefinition job in playerJobs) yield return job;
            foreach (JobDefinition job in recruitableJobs) yield return job;
            foreach (UnitEntry entry in startingRoster) yield return entry.job;

            foreach (MissionDefinition mission in missions)
            {
                foreach (UnitEntry entry in mission.opponents) yield return entry.job;
            }
        }
    }
}
