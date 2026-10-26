using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Combat;
using Tactica.Grid;

namespace Tactica.UI
{
    // The next few turns as the CT clock forecasts them, top-left of the HUD. Without it the CT
    // system is invisible - "why is the Gooner going again?" has no answer on screen.
    //
    // Built in code like the unit bars, and polled rather than event-driven: the forecast depends
    // on every unit's CT and Speed, and a few times a second is cheaper than wiring an event to
    // each thing that can change them.
    public class TurnQueueStrip : MonoBehaviour
    {
        private const int ForecastLength = 8;
        private const float RefreshInterval = 0.25f;

        // Neon palette, matching the cyberpunk setting rather than the green/red of the HP bars.
        private static readonly Color PlayerColor = new Color(0.3f, 0.95f, 0.95f);
        private static readonly Color EnemyColor = new Color(1f, 0.3f, 0.55f);
        private static readonly Color NeutralColor = new Color(1f, 0.8f, 0.3f);
        private static readonly Color DownColor = new Color(0.55f, 0.55f, 0.6f);

        private CombatManager combat;
        private RectTransform panel;
        private readonly List<TextMeshProUGUI> rows = new List<TextMeshProUGUI>();
        private TextMeshProUGUI chargesLabel;

        private static readonly Color ChargeColor = new Color(1f, 0.6f, 0.2f);
        private float nextRefresh;

        // The component sits on an always-active root and hides only its child panel. Hiding its
        // own GameObject would also stop Update, so a strip hidden between fights never came back.
        public static TurnQueueStrip Create(Transform canvas, CombatManager combat)
        {
            RectTransform root = MenuBuilder.CreateRect(canvas, "TurnQueue");
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0f, 1f);
            root.anchoredPosition = new Vector2(16f, -16f);
            root.sizeDelta = new Vector2(300f, 0f);

            RectTransform rect = MenuBuilder.CreateRect(root, "Panel");
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(300f, 0f);

            Image background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(0.02f, 0.02f, 0.06f, 0.8f);

            // Output only: the strip must never swallow a board click.
            background.raycastTarget = false;

            MenuBuilder.AddVerticalLayout(rect.gameObject, 2f, 10);
            rect.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TurnQueueStrip strip = root.gameObject.AddComponent<TurnQueueStrip>();
            strip.combat = combat;
            strip.panel = rect;

            MenuBuilder.AddText(rect, "TURN QUEUE", 18f, TextAlignmentOptions.Left, 26f, PlayerColor);

            for (int i = 0; i < ForecastLength; i++)
            {
                strip.rows.Add(MenuBuilder.AddText(rect, string.Empty, 18f, TextAlignmentOptions.Left, 24f));
            }

            // Charged abilities on their way, with the ticks left - the orange tiles on the board.
            strip.chargesLabel = MenuBuilder.AddText(rect, string.Empty, 16f, TextAlignmentOptions.Left, color: ChargeColor);

            strip.Refresh();
            return strip;
        }

        private void Update()
        {
            if (Time.unscaledTime < nextRefresh)
            {
                return;
            }

            nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh();
        }

        public void Refresh()
        {
            List<GridUnit> forecast = combat != null ? combat.PredictTurns(ForecastLength) : new List<GridUnit>();

            // Hidden outright between fights; a strip of blank rows reads as a bug.
            panel.gameObject.SetActive(forecast.Count > 0);

            if (forecast.Count == 0)
            {
                return;
            }

            List<string> charges = new List<string>();

            foreach (CombatManager.PendingCharge charge in combat.PendingCharges)
            {
                charges.Add($"CHARGING  {charge.Ability.AbilityName} ({charge.User.DisplayName})  {charge.TicksLeft}");
            }

            chargesLabel.text = string.Join("\n", charges);
            chargesLabel.gameObject.SetActive(charges.Count > 0);

            for (int i = 0; i < rows.Count; i++)
            {
                TextMeshProUGUI row = rows[i];

                if (i >= forecast.Count || forecast[i] == null)
                {
                    row.text = string.Empty;
                    continue;
                }

                GridUnit unit = forecast[i];
                string marker = i == 0 ? "> " : "  ";

                if (unit.IsKnockedOut)
                {
                    row.text = $"{marker}{unit.DisplayName}  [FLATLINE {unit.FlatlineCounter}]";
                    row.color = DownColor;
                }
                else
                {
                    string guest = unit.IsGuest ? "  (guest)" : string.Empty;
                    row.text = $"{marker}{unit.DisplayName}{guest}";
                    row.color = ColorFor(unit.CurrentFaction);
                }
            }
        }

        private static Color ColorFor(Faction faction)
        {
            switch (faction)
            {
                case Faction.Player: return PlayerColor;
                case Faction.Enemy: return EnemyColor;
                case Faction.Neutral: return NeutralColor;
            }

            return NeutralColor;
        }
    }
}
