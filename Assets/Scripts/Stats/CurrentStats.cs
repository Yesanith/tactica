using UnityEngine;

namespace Tactica.Stats
{
    // Live pools: what the unit has left right now. Split from CharacterStats deliberately.
    //
    // The two hold different *kinds* of value. CharacterStats is derived - recompute it any time
    // from base + job + equipment + active buffs, and the answer is always the same. CurrentStats
    // is authoritative history: nothing can recompute that this unit is at 12 HP, because that
    // fact is the accumulated result of everything that has happened to it.
    //
    // That is the load-bearing reason. In one struct, recalculating stats after any change -
    // equipping a ring, a buff expiring - would either overwrite HP or force every recalculation
    // path to carefully preserve two fields it otherwise has no business touching. Miss it once
    // and equipping a ring heals you to full. Splitting them makes that bug unrepresentable:
    // there is no code path where recomputing capability can touch the pools.
    //
    // The dependency also only runs one way. CurrentStats needs MaxHP to clamp against;
    // CharacterStats never needs to know current HP.
    //
    // UE5 framing: this is the same split as a UDataTable row (FCharacterStats, static config,
    // loaded once) versus replicated runtime state on the ability system component. GAS makes it
    // explicit - FGameplayAttributeData carries BaseValue and CurrentValue separately for exactly
    // this reason. It also matters for networking: max stats are set-and-forget, current HP
    // changes constantly, and you do not want to push both at the same frequency.
    [System.Serializable]
    public struct CurrentStats
    {
        public int HP;
        public int MP;

        public CurrentStats(int hp, int mp)
        {
            HP = hp;
            MP = mp;
        }

        // The single definition of "still standing". GridUnit.IsKnockedOut negates this.
        public bool IsAlive => HP > 0;

        // Spawn state: full pools from a unit's capabilities.
        public static CurrentStats FullFrom(CharacterStats stats)
        {
            return new CurrentStats(stats.MaxHP, stats.MaxMP);
        }

        // Call after MaxHP/MaxMP change - unequipping +HP gear can leave HP above the new cap.
        // Floors at 0 rather than allowing negative overkill values to persist in the pool.
        public CurrentStats ClampedTo(CharacterStats stats)
        {
            return new CurrentStats(
                Mathf.Clamp(HP, 0, stats.MaxHP),
                Mathf.Clamp(MP, 0, stats.MaxMP));
        }

        public override string ToString()
        {
            return $"HP {HP} MP {MP}";
        }
    }
}
