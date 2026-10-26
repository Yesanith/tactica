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

        [Tooltip("AI-driven even on the Player side - an escort or civilian, FFT's guest. Ignored " +
                 "for the starting roster.")]
        public bool guest;

        [Tooltip("Model for this unit instead of its job's - a boss or a named character.")]
        public GameObject modelOverride;

        [Tooltip("Uniform scale for the model. A bigger silhouette is the cheapest way to read a " +
                 "boss at a glance. 0 or less is treated as 1.")]
        public float modelScale = 1f;

        [Tooltip("Percent taken off Override chances against this unit. Bosses resist.")]
        [Min(0)]
        public int hackResistance;

        [Tooltip("A boss: area Overrides work at reduced strength against it.")]
        public bool boss;
    }
}
