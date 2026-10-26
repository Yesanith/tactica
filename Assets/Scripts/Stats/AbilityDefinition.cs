using UnityEngine;

namespace Tactica.Stats
{
    // A single ability, authored as an asset. Shared data - every unit that knows Fire points at
    // the same asset, so nothing here is per-cast state.
    [CreateAssetMenu(fileName = "NewAbilityDefinition", menuName = "Tactica/Ability Definition")]
    public class AbilityDefinition : ScriptableObject
    {
        [SerializeField] private string abilityName = "New Ability";

        [TextArea(2, 5)]
        [SerializeField] private string description = "";

        [Min(0)]
        [SerializeField] private int mpCost;

        [Tooltip("Targeting reach in orthogonal (Manhattan) steps - the same metric as movement, " +
                 "so a range-1 ability reaches the four adjacent tiles. Ignores walls and height. " +
                 "Range 0 means self-only.")]
        [Min(0)]
        [SerializeField] private int range = 1;

        [Tooltip("0 hits a single tile. 1+ is a radius in tiles around the target.")]
        [Min(0)]
        [SerializeField] private int areaOfEffect;

        [SerializeField] private AbilityEffectType effectType = AbilityEffectType.Damage;

        [Tooltip("Base magnitude, read according to EffectType: damage dealt, HP restored, " +
                 "stat delta for a buff.")]
        [SerializeField] private int power;

        // Three independent bools rather than a targeting rule type. Intentionally simple: it
        // covers the common cases and is obvious in the Inspector, which is worth more right now
        // than expressiveness.
        //
        // It cannot express everything, and the gaps are already visible: "allies but not self"
        // needs an exclusion, not a flag combination. Ground-targeted AoE that lands on an empty
        // tile has no unit to test at all, so none of these three apply. Nor can it say "only
        // undead" or "only units below half HP".
        //
        // When those land, replace this with a TargetingRule type (a small serialisable class, or
        // its own asset for reuse across abilities) rather than adding a fourth and fifth bool -
        // the combinatorics get unreadable fast, and half the combinations are meaningless.
        [Header("Scaling and weapon")]
        [Tooltip("Stat added to Power: Strength for physical, Tech for hacks and medicine.")]
        [SerializeField] private StatScaling scaling = StatScaling.Strength;

        [Tooltip("Adds the equipped weapon's Power, and uses the weapon's range and blast if " +
                 "larger. The basic Attack is just this with Power 0.")]
        [SerializeField] private bool usesWeapon;

        [Header("Status (Buff / Debuff / StatusEffect)")]
        [Tooltip("Stat change while the status lasts. Negative fields for a debuff.")]
        [SerializeField] private CharacterStats statModifier;

        [SerializeField] private StatusKind statusKind = StatusKind.None;

        [Tooltip("In the affected unit's own turns.")]
        [Min(0)]
        [SerializeField] private int duration = 2;

        [Header("Reaction / Support")]
        [SerializeField] private ReactionKind reactionKind = ReactionKind.None;

        [Tooltip("Percent chance a reaction fires when its owner is hit.")]
        [Range(0, 100)]
        [SerializeField] private int triggerChance = 50;

        [Tooltip("Support: weapon type this ability lets any job equip (Equip X).")]
        [SerializeField] private WeaponType equipWeapon = WeaponType.None;

        [Tooltip("Support: extra JP earned, in percent (JP Boost).")]
        [Min(0)]
        [SerializeField] private int jpBonusPercent;

        [Header("Progression")]
        [Tooltip("Which slot this ability occupies. Only Action abilities appear in the Act menu.")]
        [SerializeField] private AbilitySlot slot = AbilitySlot.Action;

        [Tooltip("JP to learn it in its job. 0 for abilities nobody buys - enemy kits.")]
        [Min(0)]
        [SerializeField] private int jpCost;

        [Tooltip("Weapon the user must have equipped to use it. None for anything usable unarmed.")]
        [SerializeField] private WeaponType requiredWeapon = WeaponType.None;

        [Header("Special rules")]
        [Tooltip("Damage only: a hit that lands also applies this ability's status - its stat " +
                 "modifier, kind and duration. A marking shot, a disarming cut.")]
        [SerializeField] private bool inflictsStatus;

        [Tooltip("Override only: extra percent chance against a target below half HP (Hijack).")]
        [Min(0)]
        [SerializeField] private int woundedBonus;

        [Tooltip("Override only: percent of the usual chance against bosses. 100 = no change; " +
                 "Mass Override uses 50.")]
        [Range(0, 100)]
        [SerializeField] private int bossChancePercent = 100;

        [Tooltip("Support: the user's harmful area abilities spare its own side (Friendly Fuse).")]
        [SerializeField] private bool sparesAllies;

        [Tooltip("Clock ticks between choosing the ability and it going off - FFT's charge time. " +
                 "0 is instant. The aim point is fixed when chosen; it hits whoever stands there " +
                 "when it fires, and is cancelled if the user is knocked out first.")]
        [Min(0)]
        [SerializeField] private int chargeTime;

        [Header("Valid Targets")]
        [SerializeField] private bool targetsSelf;
        [SerializeField] private bool targetsAllies;
        [SerializeField] private bool targetsEnemies = true;

        // Read-only: writing to a ScriptableObject at runtime edits the project asset, and in the
        // editor that change survives leaving Play mode.
        public string AbilityName => abilityName;
        public string Description => description;
        public int MPCost => mpCost;
        public int Range => range;
        public int AreaOfEffect => areaOfEffect;
        public AbilityEffectType EffectType => effectType;
        public int Power => power;
        public StatScaling Scaling => scaling;
        public bool UsesWeapon => usesWeapon;
        public CharacterStats StatModifier => statModifier;
        public StatusKind StatusKind => statusKind;
        public int Duration => duration;
        public ReactionKind ReactionKind => reactionKind;
        public int TriggerChance => triggerChance;
        public WeaponType EquipWeapon => equipWeapon;
        public int JPBonusPercent => jpBonusPercent;
        public bool InflictsStatus => inflictsStatus;
        public int WoundedBonus => woundedBonus;
        public int BossChancePercent => bossChancePercent;
        public bool SparesAllies => sparesAllies;
        public int ChargeTime => chargeTime;

        // Hurts its targets rather than helping them, which decides friendly fire: a harmful area
        // ability hits everyone in the blast, a helpful one only those it may target.
        public bool IsHarmful =>
            effectType == AbilityEffectType.Damage
            || effectType == AbilityEffectType.Debuff
            || effectType == AbilityEffectType.Override
            || (effectType == AbilityEffectType.StatusEffect
                && (statusKind == StatusKind.Stun || statusKind == StatusKind.DamageOverTime));

        public AbilitySlot Slot => slot;
        public int JPCost => jpCost;
        public WeaponType RequiredWeapon => requiredWeapon;

        public bool TargetsSelf => targetsSelf;
        public bool TargetsAllies => targetsAllies;
        public bool TargetsEnemies => targetsEnemies;

        // All three false means the ability can never be cast on anything - almost always an
        // authoring mistake rather than an intent.
        public bool HasAnyTarget => targetsSelf || targetsAllies || targetsEnemies;

        public override string ToString()
        {
            return $"{abilityName} ({effectType} {power}, MP {mpCost}, range {range}, AoE {areaOfEffect})";
        }
    }
}
