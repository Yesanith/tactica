using System;
using UnityEngine;

namespace Tactica.Stats
{
    // The seven weapon families from GAME_DESIGN.md. A job allows some; an Equip support ability
    // opens one more. None is for unarmed abilities and for jobs that use no weapon at all.
    //
    // Values PINNED - serialised in job, ability and equipment assets.
    public enum WeaponType
    {
        None = 0,
        ShieldGauntlet = 1,
        Monoblade = 2,
        RailRifle = 3,
        Launcher = 4,
        Cyberdeck = 5,
        Injector = 6,
        CommandBaton = 7,
    }

    // Which slot an ability lives in, FFT's five: Action abilities fill the Act menu (the primary
    // job's, plus the secondary job's); the other three are one-of-each passives the unit equips.
    //
    // Values PINNED.
    public enum AbilitySlot
    {
        Action = 0,
        Reaction = 1,
        Support = 2,
        Movement = 3,
    }

    // Start jobs are open from the beginning; Mid and Top jobs need prerequisites. Two Top jobs may
    // never be combined as primary and secondary - the hybrid rule.
    //
    // Values PINNED.
    public enum JobTier
    {
        Start = 0,
        Mid = 1,
        Top = 2,
    }

    // "Job X at job level N or higher" - one entry of a job's unlock condition.
    [Serializable]
    public struct JobRequirement
    {
        public JobDefinition job;

        [Min(0)]
        public int level;

        public override string ToString()
        {
            return job != null ? $"{job.JobName} Lv{level}" : "(unassigned)";
        }
    }
}
