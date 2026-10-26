using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Tests
{
    // Shared fixtures for the EditMode tests: the real CampaignConfig and its assets, and scene-free
    // GridUnits.
    //
    // The tests use the authored data rather than hand-built assets, and derive expected numbers
    // from it (ability Power, job stats) instead of hard-coding them - so they pin the RULES, and a
    // balance pass that changes a number does not break them.
    public static class TestData
    {
        public const string ConfigPath = "Assets/Resources/CampaignConfig.asset";

        public static CampaignConfig Config
        {
            get
            {
                CampaignConfig config = AssetDatabase.LoadAssetAtPath<CampaignConfig>(ConfigPath);
                Assert.IsNotNull(config, $"No CampaignConfig at {ConfigPath}.");
                return config;
            }
        }

        public static JobDefinition Job(string assetName)
        {
            JobDefinition job = Config.FindJob(assetName);
            Assert.IsNotNull(job, $"Job '{assetName}' is not in the CampaignConfig.");
            return job;
        }

        // The basic Attack is not in any job's list - the config holds it on its own - so it is
        // checked first.
        public static AbilityDefinition Ability(string assetName)
        {
            CampaignConfig config = Config;
            AbilityDefinition ability = config.BasicAttack != null && config.BasicAttack.name == assetName
                ? config.BasicAttack
                : config.FindAbility(assetName);
            Assert.IsNotNull(ability, $"Ability '{assetName}' is not in the CampaignConfig.");
            return ability;
        }

        public static EquipmentDefinition Weapon(string assetName)
        {
            EquipmentDefinition weapon = Config.FindEquipment(assetName);
            Assert.IsNotNull(weapon, $"Equipment '{assetName}' is not in the CampaignConfig.");
            return weapon;
        }
    }

    // Abilities made in memory for a test, so a rule can be pinned without depending on how the
    // authored kits happen to be tuned. `set` writes serialized fields by name.
    public sealed class AbilityFactory
    {
        private readonly List<AbilityDefinition> created = new List<AbilityDefinition>();

        public AbilityDefinition Make(string name, AbilityEffectType effect, int power, int range, System.Action<SerializedObject> set = null)
        {
            AbilityDefinition ability = ScriptableObject.CreateInstance<AbilityDefinition>();
            ability.name = name;
            created.Add(ability);

            SerializedObject so = new SerializedObject(ability);
            so.FindProperty("abilityName").stringValue = name;
            so.FindProperty("effectType").enumValueIndex = (int)effect;
            so.FindProperty("power").intValue = power;
            so.FindProperty("range").intValue = range;
            set?.Invoke(so);
            so.ApplyModifiedPropertiesWithoutUndo();
            return ability;
        }

        public void DestroyAll()
        {
            foreach (AbilityDefinition ability in created)
            {
                Object.DestroyImmediate(ability);
            }

            created.Clear();
        }
    }

    // Builds GridUnits without a scene and destroys them afterwards. In EditMode Awake does not
    // run, so this does what Awake would: Configure, then the first RecalculateStats (full pools).
    public sealed class UnitFactory
    {
        private readonly List<GameObject> created = new List<GameObject>();

        public GridUnit Make(string jobAsset, int level, Faction faction, Vector2Int at,
            Facing facing = Facing.North, string weaponAsset = null)
        {
            GameObject unitObject = new GameObject($"Test {jobAsset}");
            unitObject.SetActive(false);
            created.Add(unitObject);

            GridUnit unit = unitObject.AddComponent<GridUnit>();
            unit.Configure($"{jobAsset} {created.Count}", TestData.Job(jobAsset), level, faction, at);
            unit.RecalculateStats();
            unit.SetFacing(facing);

            if (weaponAsset != null)
            {
                unit.SetWeapon(TestData.Weapon(weaponAsset));
            }

            return unit;
        }

        public void DestroyAll()
        {
            foreach (GameObject unitObject in created)
            {
                if (unitObject != null)
                {
                    Object.DestroyImmediate(unitObject);
                }
            }

            created.Clear();
        }
    }
}
