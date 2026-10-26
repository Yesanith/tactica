using UnityEngine;

namespace Tactica.Stats
{
    // The progression numbers in one place: job levels from JP, JP and XP per action. Pure
    // arithmetic, so the tuning can be read and tested without a scene.
    //
    // FFT's shapes (Battle Mechanics Guide 6.3), with Tactica's multiplier - see GAME_DESIGN.md
    // "JP ve sınıf seviyesi".
    public static class Progression
    {
        // Lifetime JP needed for job levels 0..8. FFT's table. Spending JP never lowers a level.
        public static readonly int[] JobLevelThresholds = { 0, 100, 200, 350, 550, 800, 1150, 1550, 2100 };

        public static int MaxJobLevel => JobLevelThresholds.Length - 1;

        // M in the design's formula. 3 is the "medium" pace: the first mid-tier job (Lv3, 350 JP)
        // in roughly 3-4 contracts. Untested - tune after the first real session.
        public const int JPMultiplier = 3;

        // Share of a unit's earned JP that every other crew member in the same fight receives.
        public const int JPShareDivisor = 4;

        public const int XPPerLevel = 100;

        // Extra XP for the action that knocks a target out - FFT's kill bonus.
        public const int KnockoutXPBonus = 10;

        public static int JobLevelFor(int lifetimeJP)
        {
            int level = 0;

            for (int i = 1; i < JobLevelThresholds.Length; i++)
            {
                if (lifetimeJP >= JobLevelThresholds[i])
                {
                    level = i;
                }
            }

            return level;
        }

        // JP still needed for the next job level, or 0 at the cap.
        public static int JPToNextJobLevel(int lifetimeJP)
        {
            int level = JobLevelFor(lifetimeJP);
            return level >= MaxJobLevel ? 0 : JobLevelThresholds[level + 1] - lifetimeJP;
        }

        // JP for one effective action: [(8 + 2 x job level + [character level / 4]) x M].
        public static int JPForAction(int jobLevel, int characterLevel)
        {
            return (8 + 2 * jobLevel + characterLevel / 4) * JPMultiplier;
        }

        // XP for one effective action. A higher-level target is worth more, a lower one less, never
        // under 1 - FFT's level-difference term, which slows grinding weak enemies.
        public static int XPForAction(int userLevel, int targetLevel, bool knockedOut)
        {
            int xp = Mathf.Max(1, 10 + (targetLevel - userLevel));
            return knockedOut ? xp + KnockoutXPBonus : xp;
        }
    }
}
