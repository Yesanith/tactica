using UnityEngine;

namespace Tactica.Stats
{
    // A weapon. Its type decides who may equip it (the job's allowed list, or an Equip support
    // ability); its numbers feed the basic attack and any ability flagged UsesWeapon.
    //
    // GAME_DESIGN.md's tiers: Mk I +0, Mk II +2, Mk III +4 Power over the type's base, plus named
    // uniques. The tier is data here, not a rule - the shop and loot read it.
    [CreateAssetMenu(fileName = "NewEquipment", menuName = "Tactica/Equipment Definition")]
    public class EquipmentDefinition : ScriptableObject
    {
        [SerializeField] private string displayName = "New Weapon";

        [SerializeField] private WeaponType weaponType = WeaponType.Monoblade;

        [Tooltip("1-3 for Mk I-III; 0 for a named unique.")]
        [Min(0)]
        [SerializeField] private int tier = 1;

        [Tooltip("Added to the Power of the basic attack and of UsesWeapon abilities.")]
        [SerializeField] private int power;

        [Tooltip("Reach of the basic attack, in Manhattan steps.")]
        [Min(1)]
        [SerializeField] private int range = 1;

        [Tooltip("Radius of the basic attack's blast. 0 for a single target - only Launchers use it.")]
        [Min(0)]
        [SerializeField] private int areaOfEffect;

        [Tooltip("Flat stat bonus while equipped - Defense for a gauntlet, Evasion for a blade.")]
        [SerializeField] private CharacterStats statBonus;

        [Tooltip("Shop price. 0 for loot-only items.")]
        [Min(0)]
        [SerializeField] private int price;

        public string DisplayName => displayName;
        public WeaponType Type => weaponType;
        public int Tier => tier;
        public int Power => power;
        public int Range => range;
        public int AreaOfEffect => areaOfEffect;
        public CharacterStats StatBonus => statBonus;
        public int Price => price;

        public override string ToString()
        {
            return $"{displayName} ({weaponType} Mk{tier}, Power {power}, range {range})";
        }
    }
}
