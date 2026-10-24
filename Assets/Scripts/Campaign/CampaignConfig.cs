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

        [Header("Roster")]
        [SerializeField] private List<UnitEntry> startingRoster = new List<UnitEntry>();

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

        [Header("Missions")]
        [SerializeField] private List<MissionDefinition> missions = new List<MissionDefinition>();

        public bool StartWithMenu => startWithMenu;
        public IReadOnlyList<UnitEntry> StartingRoster => startingRoster;
        public IReadOnlyList<JobDefinition> RecruitableJobs => recruitableJobs;
        public IReadOnlyList<string> RecruitNames => recruitNames;
        public int MaxRosterSize => maxRosterSize;
        public int MaxDeployed => maxDeployed;
        public int RecruitPoolSize => recruitPoolSize;
        public Vector2Int RecruitLevelRange => recruitLevelRange;
        public IReadOnlyList<MissionDefinition> Missions => missions;

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

        private IEnumerable<JobDefinition> EnumerateJobs()
        {
            foreach (JobDefinition job in recruitableJobs) yield return job;
            foreach (UnitEntry entry in startingRoster) yield return entry.job;

            foreach (MissionDefinition mission in missions)
            {
                foreach (UnitEntry entry in mission.opponents) yield return entry.job;
            }
        }
    }
}
