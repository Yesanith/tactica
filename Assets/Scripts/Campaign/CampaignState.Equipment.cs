using System.Collections.Generic;
using System.Linq;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // Weapons: who may hold what, swapping with the inventory, and the district shop.
    public partial class CampaignState
    {
        // Selling gets half the price back - the usual shop margin, so buying is a decision.
        public const int SellDivisor = 2;

        // A weapon this unit may not hold any more (job changed, Equip support removed) goes back
        // to the inventory rather than vanishing.
        public void ValidateWeapon(RosterUnit unit)
        {
            if (unit.Weapon != null && !unit.CanEquip(unit.Weapon.Type))
            {
                inventory.Add(unit.Weapon);
                unit.Weapon = null;
            }
        }

        // Inventory weapons this unit could take.
        public List<EquipmentDefinition> EquippableFor(RosterUnit unit)
        {
            return inventory.Where(item => item != null && unit.CanEquip(item.Type)).Distinct().ToList();
        }

        // Swaps a weapon in from the inventory (null unequips). The old weapon goes back.
        public bool EquipWeapon(RosterUnit unit, EquipmentDefinition weapon)
        {
            if (unit.Locked || (weapon != null && (!inventory.Contains(weapon) || !unit.CanEquip(weapon.Type))))
            {
                return false;
            }

            if (unit.Weapon != null)
            {
                inventory.Add(unit.Weapon);
            }

            if (weapon != null)
            {
                inventory.Remove(weapon);
            }

            unit.Weapon = weapon;
            return true;
        }

        public static int SellPrice(EquipmentDefinition item)
        {
            return item != null ? item.Price / SellDivisor : 0;
        }

        public bool CanBuy(EquipmentDefinition item, out string reason)
        {
            reason = null;

            if (item == null) reason = "Nothing to buy.";
            else if (!ShopStock(CurrentDistrict).Contains(item)) reason = "Not sold here.";
            else if (Credits < item.Price) reason = $"{item.Price} cr needed.";

            return reason == null;
        }

        public bool Buy(EquipmentDefinition item)
        {
            if (!CanBuy(item, out _))
            {
                return false;
            }

            Credits -= item.Price;
            inventory.Add(item);
            return true;
        }

        // Only spare weapons: what a unit holds must be unequipped first.
        public bool Sell(EquipmentDefinition item)
        {
            if (item == null || !inventory.Remove(item))
            {
                return false;
            }

            Credits += SellPrice(item);
            return true;
        }
    }
}
