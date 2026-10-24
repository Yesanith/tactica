using UnityEngine;

namespace Tactica.Stats
{
    // A unit's capabilities: the relatively stable numbers that define what it *can* do.
    // Also doubles as a modifier - a job bonus or buff is just a CharacterStats with mostly
    // zeroes, added on with operator +.
    [System.Serializable]
    public struct CharacterStats
    {
        public int MaxHP;
        public int MaxMP;

        public int Strength;
        public int Magic;

        // Flat damage reduction, subtracted in ActionResolver.CalculateDamage.
        public int Defense;

        public int Speed;

        // Tiles per turn. Read by GridManager's move-range flood fill.
        public int Move;

        // Max height difference this unit can traverse in one step. Passed to
        // GridManager.GetTilesInMoveRange for every move-range query.
        public int Jump;

        // Chance, in percent, to dodge a physical attack from the FRONT. Halved from the side and
        // ignored from behind - FFT's facing rule, simplified to one number. Magic-like effects
        // (Override, Reboot) ignore it.
        public int Evasion;

        public CharacterStats(int maxHP, int maxMP, int strength, int magic, int defense, int speed, int move, int jump, int evasion = 0)
        {
            MaxHP = maxHP;
            MaxMP = maxMP;
            Strength = strength;
            Magic = magic;
            Defense = defense;
            Speed = speed;
            Move = move;
            Jump = jump;
            Evasion = evasion;
        }

        // Component-wise. Negative fields are debuffs, so subtraction is just adding a negative.
        //
        // Deliberately unclamped: a stack of debuffs can drive a stat below zero, and clamping
        // here would make the result depend on the order the modifiers were summed in, which is
        // the kind of bug that only shows up with three buffs active. Sum first, clamp at the
        // point of use.
        //
        // Additive only. A percentage modifier ("+20% Strength") is a different operation and
        // does not commute with this one - when that lands it needs its own type, applied in a
        // defined order relative to flat bonuses, not folded in here.
        public static CharacterStats operator +(CharacterStats a, CharacterStats b)
        {
            return new CharacterStats
            {
                MaxHP = a.MaxHP + b.MaxHP,
                MaxMP = a.MaxMP + b.MaxMP,
                Strength = a.Strength + b.Strength,
                Magic = a.Magic + b.Magic,
                Defense = a.Defense + b.Defense,
                Speed = a.Speed + b.Speed,
                Move = a.Move + b.Move,
                Jump = a.Jump + b.Jump,
                Evasion = a.Evasion + b.Evasion,
            };
        }

        // Scales every field. Exists so "this growth applied N times" is one operation instead of
        // a loop that adds the same struct to itself - identical result for ints, no rounding to
        // worry about, and it reads as the arithmetic it actually is.
        //
        // Same unclamped rule as operator +: a negative multiplier is legal and flips a bonus
        // into a penalty.
        public static CharacterStats operator *(CharacterStats stats, int multiplier)
        {
            return new CharacterStats
            {
                MaxHP = stats.MaxHP * multiplier,
                MaxMP = stats.MaxMP * multiplier,
                Strength = stats.Strength * multiplier,
                Magic = stats.Magic * multiplier,
                Defense = stats.Defense * multiplier,
                Speed = stats.Speed * multiplier,
                Move = stats.Move * multiplier,
                Jump = stats.Jump * multiplier,
                Evasion = stats.Evasion * multiplier,
            };
        }

        public override string ToString()
        {
            return $"HP {MaxHP} MP {MaxMP} | STR {Strength} MAG {Magic} DEF {Defense} SPD {Speed} | Move {Move} Jump {Jump} EV {Evasion}";
        }
    }
}
