using UnityEngine;

namespace Tactica.Grid
{
    // Which way a unit looks. Four directions because movement and range are 4-directional.
    // North is +y on the grid, which is +z in the world.
    //
    // Values PINNED, like Faction: serialised as ints.
    public enum Facing
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
    }

    // Where an attack lands relative to the defender's facing. Decides which evasion applies.
    public enum AttackSide
    {
        Front,
        Side,
        Back,
    }

    public static class FacingUtility
    {
        public static Vector2Int ToVector(Facing facing)
        {
            switch (facing)
            {
                case Facing.North: return new Vector2Int(0, 1);
                case Facing.East: return new Vector2Int(1, 0);
                case Facing.South: return new Vector2Int(0, -1);
                default: return new Vector2Int(-1, 0);
            }
        }

        // World yaw that makes a unit look along its facing.
        public static float ToYaw(Facing facing)
        {
            return (int)facing * 90f;
        }

        // The facing that looks most directly from `from` toward `to`. The longer axis wins;
        // a perfect diagonal tie goes to the vertical axis, so the choice is deterministic.
        // Returns `fallback` when the two coordinates are the same.
        public static Facing Towards(Vector2Int from, Vector2Int to, Facing fallback)
        {
            Vector2Int delta = to - from;

            if (delta == Vector2Int.zero)
            {
                return fallback;
            }

            if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y))
            {
                return delta.x > 0 ? Facing.East : Facing.West;
            }

            return delta.y > 0 ? Facing.North : Facing.South;
        }

        // FFT's rule (BMG 1.4): measure the attacker's offset along the defender's forward axis and
        // across it, and whichever is larger decides. Ties go to the more forward side - front over
        // side, side over back. Height does not matter.
        public static AttackSide SideOf(Vector2Int attacker, Vector2Int defender, Facing defenderFacing)
        {
            Vector2Int offset = attacker - defender;
            Vector2Int forward = ToVector(defenderFacing);

            int along = offset.x * forward.x + offset.y * forward.y;
            int across = Mathf.Abs(offset.x * forward.y - offset.y * forward.x);

            if (along >= across)
            {
                return AttackSide.Front;
            }

            if (-along > across)
            {
                return AttackSide.Back;
            }

            return AttackSide.Side;
        }
    }
}
