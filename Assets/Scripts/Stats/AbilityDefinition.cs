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

        [Tooltip("Targeting reach in tiles, as 8-directional (Chebyshev) step distance - a " +
                 "diagonal neighbour counts as 1, the same metric movement range uses. " +
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
