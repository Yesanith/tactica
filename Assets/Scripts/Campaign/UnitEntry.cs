using System;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // An authored unit: who it is, what it does, how strong. Used both for a mission's opponents
    // and for the starting roster - the roster ignores faction, since everyone in it is Player.
    [Serializable]
    public class UnitEntry
    {
        [Tooltip("Shown in the HUD and menus. Empty falls back to the job name.")]
        public string unitName;

        public JobDefinition job;

        [Min(1)]
        public int level = 1;

        [Tooltip("Side this unit starts on. Ignored for the starting roster.")]
        public Faction faction = Faction.Enemy;
    }
}
