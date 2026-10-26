using System.Collections.Generic;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Grid
{
    // What the unit fights with: its Act menu, weapon, passive slots and hack resistance. All
    // fold into EffectiveStats through RecalculateStats, so every consumer (damage, HUD, AI)
    // reads one honest number.
    public partial class GridUnit
    {
        // The Act menu for this unit, when something other than its job's level unlocks decides
        // it - a crew member's learned abilities across primary and secondary job. Null means
        // "use the job's unlocks", which is what enemies and hand-placed units get.
        private List<AbilityDefinition> actionLoadout;

        public EquipmentDefinition Weapon { get; private set; }

        public AbilityDefinition Reaction { get; private set; }
        public AbilityDefinition Support { get; private set; }
        public AbilityDefinition Movement { get; private set; }

        // Percent subtracted from Override chances against this unit - bosses resist hacking.
        public int HackResistance { get; private set; }

        public void SetWeapon(EquipmentDefinition weapon)
        {
            Weapon = weapon;

            if (statsInitialised)
            {
                RecalculateStats();
            }
        }

        public void SetPassives(AbilityDefinition reaction, AbilityDefinition support, AbilityDefinition movement)
        {
            Reaction = reaction;
            Support = support;
            Movement = movement;

            if (statsInitialised)
            {
                RecalculateStats();
            }
        }

        // A boss: some abilities (Mass Override) work at reduced strength against it.
        public bool IsBoss { get; private set; }

        public void SetBoss(bool boss)
        {
            IsBoss = boss;
        }

        public void SetHackResistance(int resistance)
        {
            HackResistance = Mathf.Max(0, resistance);
        }

        // Whether this unit may use abilities that need the given weapon type: the right weapon in
        // hand. Permission to *equip* it (job or Equip support) is checked when it is equipped.
        public bool HasWeaponType(WeaponType type)
        {
            return type == WeaponType.None || (Weapon != null && Weapon.Type == type);
        }

        public void SetActionLoadout(IEnumerable<AbilityDefinition> abilities)
        {
            actionLoadout = abilities != null ? new List<AbilityDefinition>(abilities) : null;
        }

        // What this unit can pick from the Act menu right now. A fresh list each call, so callers
        // can filter it freely.
        public List<AbilityDefinition> GetActionAbilities()
        {
            if (actionLoadout != null)
            {
                return new List<AbilityDefinition>(actionLoadout);
            }

            List<AbilityDefinition> abilities = new List<AbilityDefinition>();

            if (currentJob == null)
            {
                return abilities;
            }

            foreach (AbilityDefinition ability in currentJob.GetUnlockedAbilities(level))
            {
                if (ability.Slot == AbilitySlot.Action)
                {
                    abilities.Add(ability);
                }
            }

            return abilities;
        }
    }
}
