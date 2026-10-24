using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Grid;

namespace Tactica.UI
{
    // Screen-space readout for one unit: name, level, HP and MP. Used for both the active unit and
    // the current target - the same panel prefab twice rather than two near-identical scripts,
    // so a layout tweak or an added stat only has to be made once.
    //
    // Holds a bound unit and re-reads it on Refresh, rather than being handed loose numbers. That
    // keeps the "who is this showing?" question answerable from the panel itself, which is what
    // lets CombatHUD refresh whatever happens to be displayed without tracking it separately.
    public class UnitInfoPanel : MonoBehaviour
    {
        // TextMeshProUGUI rather than the TMP_Text base class: this panel is uGUI-only by
        // design, and the concrete type makes that a compile error rather than a runtime surprise
        // if a 3D TextMeshPro component is ever dragged in by mistake.
        [Tooltip("Shows GridUnit.DisplayName - the unit's own name, or its job name if it has none.")]
        [SerializeField] private TextMeshProUGUI nameLabel;

        [SerializeField] private TextMeshProUGUI levelLabel;

        [Tooltip("Image with Image Type = Filled - same approach as UnitStatusBar, not a Slider.")]
        [SerializeField] private Image hpFill;

        [SerializeField] private Image mpFill;

        [SerializeField] private TextMeshProUGUI hpLabel;
        [SerializeField] private TextMeshProUGUI mpLabel;

        [Header("Faction Colours")]
        // Same three colours as UnitStatusBar, deliberately duplicated as serialized fields rather
        // than shared: these are per-panel art, and a HUD panel may well want a different tone from
        // a bar floating over the board. Keep them in sync by eye.
        [SerializeField] private Color allyColor = new Color(0.25f, 0.85f, 0.3f);
        [SerializeField] private Color enemyColor = new Color(0.9f, 0.25f, 0.25f);
        [SerializeField] private Color neutralColor = new Color(0.95f, 0.75f, 0.2f);

        private GridUnit boundUnit;

        public GridUnit BoundUnit => boundUnit;

        // Binds a unit and draws it immediately. Passing null blanks the panel rather than leaving
        // the previous unit's numbers on screen, which would be actively misleading.
        public void Bind(GridUnit unit)
        {
            boundUnit = unit;
            Refresh();
        }

        // Re-reads the bound unit. Cheap enough to call on every stat change.
        public void Refresh()
        {
            if (boundUnit == null)
            {
                ClearLabels();
                return;
            }

            SetText(nameLabel, boundUnit.DisplayName);
            SetText(levelLabel, $"Lv.{boundUnit.Level}");

            int hp = boundUnit.CurrentStats.HP;
            int mp = boundUnit.CurrentStats.MP;
            int maxHP = boundUnit.EffectiveStats.MaxHP;
            int maxMP = boundUnit.EffectiveStats.MaxMP;

            SetFill(hpFill, hp, maxHP);
            SetFill(mpFill, mp, maxMP);

            SetText(hpLabel, $"{hp}/{maxHP}");
            SetText(mpLabel, $"{mp}/{maxMP}");

            if (hpFill != null)
            {
                hpFill.color = ColorFor(boundUnit.CurrentFaction);
            }
        }

        // Mirrors UnitStatusBar.ColorFor - every member listed, so a future faction shows up as a
        // missing case rather than silently painting itself ally-green.
        private Color ColorFor(Faction faction)
        {
            switch (faction)
            {
                case Faction.Player: return allyColor;
                case Faction.Enemy: return enemyColor;
                case Faction.Neutral: return neutralColor;
            }

            return neutralColor;
        }

        private void ClearLabels()
        {
            SetText(nameLabel, "-");
            SetText(levelLabel, string.Empty);
            SetText(hpLabel, string.Empty);
            SetText(mpLabel, string.Empty);
            SetFill(hpFill, 0, 0);
            SetFill(mpFill, 0, 0);
        }

        private static void SetText(TextMeshProUGUI label, string value)
        {
            if (label != null)
            {
                label.text = value;
            }
        }

        // Same divide guard as UnitStatusBar: an unconfigured job has MaxHP 0, and 0/0 is NaN,
        // which renders as a full bar rather than an empty one.
        private static void SetFill(Image fill, int current, int max)
        {
            if (fill != null)
            {
                fill.fillAmount = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;
            }
        }
    }
}
