using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // What an AI unit decided to go after, and whether it can act on that this turn.
    //
    // A struct rather than a bare GridUnit return because the execution layer landing next needs
    // the reachability answer and the chosen ability too, and recomputing them there would mean
    // running the move-range flood fill a second time.
    public readonly struct AITargetDecision
    {
        public readonly GridUnit Target;

        // The ability the reachability test was measured against. Null means the unit had none
        // usable, which is also why Target is null.
        public readonly AbilityDefinition Ability;

        // True when some tile the unit can stand on this turn - including the one it is already on
        // - puts Target inside Ability.Range.
        public readonly bool CanActThisTurn;

        // Steps from the closest tile the unit could stand on this turn, NOT from where it stands
        // now. For an unreachable target this ranks "how close can I get to being able to hit you",
        // which is the number the chase behaviour will want.
        public readonly int DistanceFromBestStandingTile;

        // How many targets were reachable. Kept for the decision log; a lone reachable target and
        // a pick out of six read very differently when something looks wrong.
        public readonly int ReachableTargetCount;

        // Where to stand. When CanActThisTurn it is the closest tile that puts Target in range;
        // otherwise the closest tile to Target the unit can reach, so it walks toward the fight
        // rather than standing still. Equal to the unit's current coordinates when staying put is
        // already the best option - the executor compares the two rather than carrying a flag.
        public readonly Vector2Int DestinationTile;

        // Where the ability is aimed. The target's tile for single-target abilities; for an area
        // ability, the blast centre the evaluation chose.
        public readonly Vector2Int Center;

        // The evaluation's score for the chosen action, for the decision log. 0 for approach moves.
        public readonly float Score;

        // A wounded unit pulling back instead of acting: move to DestinationTile, do nothing else.
        // Target is the nearest threat, kept for the log.
        public readonly bool Retreat;

        public bool HasTarget => Target != null;

        public AITargetDecision(
            GridUnit target,
            AbilityDefinition ability,
            bool canActThisTurn,
            int distanceFromBestStandingTile,
            int reachableTargetCount,
            Vector2Int destinationTile,
            Vector2Int? center = null,
            float score = 0f,
            bool retreat = false)
        {
            Retreat = retreat;
            Target = target;
            Ability = ability;
            CanActThisTurn = canActThisTurn;
            DistanceFromBestStandingTile = distanceFromBestStandingTile;
            ReachableTargetCount = reachableTargetCount;
            DestinationTile = destinationTile;
            Center = center ?? (target != null ? target.GridCoords : destinationTile);
            Score = score;
        }
    }
}
