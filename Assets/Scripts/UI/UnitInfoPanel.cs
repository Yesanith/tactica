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

        private GridUnit boundUnit;

        public GridUnit BoundUnit => boundUnit;

        public const float Width = 330f;
        public const float Height = 100f;
        private const float Pad = 16f;

        // Places the panel's authored children on a regular grid - name and level on top, HP and
        // MP bars below with their numbers right-aligned. The prefab was laid out by hand and its
        // pieces did not line up; doing it here keeps one source for the layout. Anchoring the
        // panel itself is the caller's job.
        public void ApplyLayout()
        {
            RectTransform panel = (RectTransform)transform;
            panel.sizeDelta = new Vector2(Width, Height);

            Place(nameLabel, new Vector2(0f, 1f), new Vector2(Pad, -10f), new Vector2(Width - 110f, 32f));
            Place(levelLabel, new Vector2(1f, 1f), new Vector2(-Pad, -14f), new Vector2(90f, 24f));
            Place(hpLabel, new Vector2(1f, 1f), new Vector2(-Pad, -44f), new Vector2(90f, 22f));
            Place(mpLabel, new Vector2(1f, 1f), new Vector2(-Pad, -70f), new Vector2(90f, 22f));
            PlaceBar(hpFill, -50f, 12f);
            PlaceBar(mpFill, -76f, 9f);

            SetStyle(nameLabel, 26f, TextAlignmentOptions.Left, NeonTheme.TextRole.Display);
            SetStyle(levelLabel, 18f, TextAlignmentOptions.Right, NeonTheme.TextRole.Data);
            SetStyle(hpLabel, 18f, TextAlignmentOptions.Right, NeonTheme.TextRole.Data);
            SetStyle(mpLabel, 16f, TextAlignmentOptions.Right, NeonTheme.TextRole.Data);

            if (levelLabel != null)
            {
                levelLabel.color = NeonTheme.TextDim;
            }

            if (mpLabel != null)
            {
                mpLabel.color = NeonTheme.TextDim;
            }
        }

        private static void Place(Component item, Vector2 corner, Vector2 position, Vector2 size)
        {
            if (item == null)
            {
                return;
            }

            RectTransform rect = (RectTransform)item.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        // A bar is its fill's parent (the background), from the left padding to the numbers.
        private static void PlaceBar(Image fill, float top, float height)
        {
            if (fill != null && fill.transform.parent is RectTransform)
            {
                Place(fill.transform.parent, new Vector2(0f, 1f), new Vector2(Pad, top), new Vector2(Width - Pad * 2f - 100f, height));
            }
        }

        private static void SetStyle(TextMeshProUGUI label, float size, TextAlignmentOptions alignment, NeonTheme.TextRole role)
        {
            if (label == null)
            {
                return;
            }

            NeonTheme.Style(label, role);
            label.fontSize = size;
            label.alignment = alignment;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }

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
                hpFill.color = NeonTheme.FactionColor(boundUnit.CurrentFaction);
            }

            if (mpFill != null)
            {
                mpFill.color = NeonTheme.Mp;
            }

            if (nameLabel != null)
            {
                nameLabel.color = NeonTheme.FactionColor(boundUnit.CurrentFaction);
            }
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
