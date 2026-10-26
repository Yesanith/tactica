using System.Collections.Generic;
using UnityEngine;

namespace Tactica.Stats
{
    // A job's stat profile and learn list, authored as an asset rather than in code so jobs can
    // be added and tuned without recompiling.
    //
    // ScriptableObject, not MonoBehaviour: this is shared data with no scene presence. Every Knight
    // in the party points at the same JobDefinition asset instead of carrying its own copy - the
    // rough equivalent of a UDataAsset in UE5, and the reason fields below are read-only.
    [CreateAssetMenu(fileName = "NewJobDefinition", menuName = "Tactica/Job Definition")]
    public class JobDefinition : ScriptableObject
    {
        [SerializeField] private string jobName = "New Job";

        [Tooltip("Stats at job level 1.")]
        [SerializeField] private CharacterStats baseStats;

        [Tooltip("Added once per level beyond the first. Flat growth for now - swap for a curve " +
                 "asset if tuning shows linear scaling feels wrong.")]
        [SerializeField] private CharacterStats statGrowthPerLevel;

        [Tooltip("Abilities this job learns, and the level each becomes available.")]
        [SerializeField] private List<AbilityUnlock> abilityUnlocks = new List<AbilityUnlock>();

        [Header("Progression")]
        [SerializeField] private JobTier tier = JobTier.Start;

        [Tooltip("Every entry must be met to unlock this job. Empty means open from the start.")]
        [SerializeField] private List<JobRequirement> prerequisites = new List<JobRequirement>();

        [Tooltip("Weapon types this job can equip without an Equip support ability.")]
        [SerializeField] private List<WeaponType> allowedWeapons = new List<WeaponType>();

        [Tooltip("Enemy and special jobs: fixed kit, no JP, never offered on the Crew screen.")]
        [SerializeField] private bool isFixedKit;

        [Tooltip("What a new unit of this job holds, and what fixed-kit enemies always use.")]
        [SerializeField] private EquipmentDefinition defaultWeapon;

        [Header("Appearance")]
        [Tooltip("Character model for units of this job, pivot at the feet. Leave empty for the " +
                 "capsule placeholder - also what a clone without the (git-ignored) model pack gets.")]
        [SerializeField] private GameObject model;

        [Tooltip("Controller for the model's Animator. Humanoid clips retarget, so one idle can " +
                 "serve every humanoid model.")]
        [SerializeField] private RuntimeAnimatorController animatorController;

        // Read-only on purpose. A ScriptableObject is a project asset, not per-instance state, so
        // writing to one at runtime edits the asset itself - and in the editor that change sticks
        // after you leave Play mode. Same trap as mutating a sharedMaterial.
        public string JobName => jobName;
        public CharacterStats BaseStats => baseStats;
        public CharacterStats StatGrowthPerLevel => statGrowthPerLevel;
        public IReadOnlyList<AbilityUnlock> AbilityUnlocks => abilityUnlocks;
        public JobTier Tier => tier;
        public IReadOnlyList<JobRequirement> Prerequisites => prerequisites;
        public IReadOnlyList<WeaponType> AllowedWeapons => allowedWeapons;
        public bool IsFixedKit => isFixedKit;
        public EquipmentDefinition DefaultWeapon => defaultWeapon;

        public bool AllowsWeapon(WeaponType weapon)
        {
            return weapon == WeaponType.None || allowedWeapons.Contains(weapon);
        }

        // Every ability this job teaches, in authored order - what the Crew screen offers for JP.
        public IEnumerable<AbilityDefinition> AllAbilities()
        {
            foreach (AbilityUnlock unlock in abilityUnlocks)
            {
                if (unlock.IsAssigned)
                {
                    yield return unlock.Ability;
                }
            }
        }
        public GameObject Model => model;
        public RuntimeAnimatorController AnimatorController => animatorController;

        // Stats for a unit at the given job level. Level 1 returns BaseStats unchanged.
        //
        // Uses CharacterStats' scalar multiply rather than looping the addition: same result for
        // integers, O(1) instead of O(level), and it states the intent directly.
        public CharacterStats GetStatsAtLevel(int level)
        {
            // Guard the subtraction. Level 0 or negative would multiply growth by a negative and
            // hand back stats *below* base - silently, since nothing downstream range-checks.
            int effectiveLevel = Mathf.Max(1, level);

            return baseStats + statGrowthPerLevel * (effectiveLevel - 1);
        }

        // Everything learned at or below the given level. Makes no ordering assumption about the
        // authored list, so entries can be entered in any order in the Inspector.
        //
        // Unassigned rows are skipped rather than returned as nulls - an empty slot in the
        // Inspector is an authoring accident, and callers should not have to null-check every
        // ability they were handed.
        public List<AbilityDefinition> GetUnlockedAbilities(int level)
        {
            List<AbilityDefinition> unlocked = new List<AbilityDefinition>();

            foreach (AbilityUnlock unlock in abilityUnlocks)
            {
                if (unlock.RequiredLevel <= level && unlock.IsAssigned)
                {
                    unlocked.Add(unlock.Ability);
                }
            }

            return unlocked;
        }
    }
}
