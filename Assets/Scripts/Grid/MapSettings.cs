using System;
using UnityEngine;

namespace Tactica.Grid
{
    // The recipe for a generated battlefield. Same settings and seed, same map, every time - so a
    // contract always plays on the board its author tuned.
    //
    // Lives in Grid rather than Campaign: it describes tiles, and GridManager consumes it. A
    // MissionDefinition just carries one.
    [Serializable]
    public struct MapSettings
    {
        [Tooltip("Tiles across (x). 0 = keep the scene's default flat grid.")]
        [Min(0)] public int width;

        [Tooltip("Tiles from the player's edge to the enemy's edge (y). Each side spawns on its own edge.")]
        [Min(0)] public int depth;

        public int seed;

        [Tooltip("Highest terrace level. Neighbouring tiles never differ by more than 1, so any unit " +
                 "with Jump 1 can walk the whole map - except cover blocks.")]
        [Min(0)] public int maxHeight;

        [Tooltip("World units per height level. 0 keeps the GridManager's own value.")]
        [Min(0f)] public float heightStep;

        [Tooltip("How many tiles deep the ragged edges bite into the map.")]
        [Min(0)] public int edgeDepth;

        [Tooltip("Chance per interior tile of a hole - a gap units must walk around.")]
        [Range(0f, 0.3f)] public float holeChance;

        [Tooltip("Chance per interior tile of a cover block raised 2 above its surroundings - too " +
                 "tall for Jump 1 units to climb, so it shapes movement and lines of approach.")]
        [Range(0f, 0.3f)] public float coverChance;

        public bool IsDefined => width > 0 && depth > 0;
    }
}
