using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Campaign;
using Tactica.Grid;

namespace Tactica.UI
{
    // The menu's part in a fight: starting a mission from the briefing, the small overlay that
    // stays up during it (objective, Jack Out, the debug faction panel), and taking the result
    // back to the menus when it ends.
    public partial class CampaignMenu
    {
        private RectTransform combatBar;
        private RectTransform unitsPanel;
        private TextMeshProUGUI objectiveLabel;
        private bool unitsPanelOpen;

        // Top-right, outside the menu background so it stays up while the board is visible: the
        // objective, Jack Out, and the debug faction panel. Only these rects take raycasts, so the
        // rest of the board stays clickable.
        private void BuildCombatBar(Canvas canvas)
        {
            combatBar = MenuBuilder.CreateRect(canvas.transform, "CombatBar");
            combatBar.anchorMin = combatBar.anchorMax = new Vector2(1f, 1f);
            combatBar.pivot = new Vector2(1f, 1f);
            combatBar.anchoredPosition = new Vector2(-16f, -16f);
            combatBar.sizeDelta = new Vector2(480f, 0f);
            MenuBuilder.AddVerticalLayout(combatBar.gameObject, 8f);
            combatBar.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform buttons = MenuBuilder.AddRow(combatBar, 44f);

            // The faction test bench, only with CampaignConfig's Show Debug Tools on. Without it an
            // empty label takes the slack, so Jack Out stays in the corner.
            if (config != null && config.ShowDebugTools)
            {
                MenuBuilder.AddButton(buttons, "Debug: Factions", () =>
                {
                    unitsPanelOpen = !unitsPanelOpen;
                    RefreshUnitsPanel();
                }, -1f, 44f);
            }
            else
            {
                MenuBuilder.AddText(buttons, string.Empty, 18f);
            }

            // Jacking out keeps whatever JP and XP were earned, but flatlines nothing and wins
            // nothing - worth a second click.
            AddCombatJackOut(buttons);

            // The contract's objective stays on screen for the whole fight - a "keep X alive" that
            // is only shown on the briefing gets forgotten by turn three.
            objectiveLabel = MenuBuilder.AddText(combatBar, string.Empty, 20f, TextAlignmentOptions.Right, 28f,
                MenuBuilder.AccentTextColor);

            unitsPanel = MenuBuilder.CreateRect(combatBar, "UnitsPanel");
            NeonTheme.SkinPanel(unitsPanel.gameObject.AddComponent<Image>());
            MenuBuilder.AddVerticalLayout(unitsPanel.gameObject, 4f, 10);

            combatBar.gameObject.SetActive(false);
            unitsPanel.gameObject.SetActive(false);
        }

        private void StartMission(MissionDefinition mission, bool road)
        {
            if (!missionRunner.StartMission(mission, campaign.GetDeployed(), out string error))
            {
                SetNotice(error);
                ShowBriefing(mission, road);
                return;
            }

            // The board is up; the crew is placed before the first turn (CampaignMenu.Deploy.cs).
            BeginDeployment(mission, road);
        }

        private void HandleMissionFinished(MissionResult result)
        {
            lastResult = result;
            recruitedFromResult.Clear();

            campaign.ApplyResult(result);
            campaign.Save();

            combatBar.gameObject.SetActive(false);
            ShowResult();
        }

        private void HandleActiveUnitChanged(GridUnit unit)
        {
            RefreshUnitsPanel();

            // Survive counts turns; the label keeps the count current.
            if (objectiveLabel != null && missionRunner.IsRunning && !missionRunner.IsDeploying)
            {
                objectiveLabel.text = "OBJECTIVE: " + missionRunner.ObjectiveStatus;
            }
        }

        // Every unit in the encounter with a button that cycles its faction. The faction test bench:
        // convert the last enemy and the fight should end as a win on the spot.
        private void RefreshUnitsPanel()
        {
            if (unitsPanel == null)
            {
                return;
            }

            unitsPanel.gameObject.SetActive(unitsPanelOpen && missionRunner.IsRunning);

            if (!unitsPanel.gameObject.activeSelf)
            {
                return;
            }

            MenuBuilder.Clear(unitsPanel);

            MenuBuilder.AddText(unitsPanel, "DEBUG  -  click a side to change it", 18f, TextAlignmentOptions.Left, 28f,
                MenuBuilder.WarningTextColor);

            GridUnit active = missionRunner.Combat != null ? missionRunner.Combat.GetActiveUnit() : null;

            foreach (GridUnit unit in missionRunner.SpawnedUnits)
            {
                if (unit == null)
                {
                    continue;
                }

                RectTransform row = MenuBuilder.AddRow(unitsPanel, 38f, 6f);

                string marker = unit == active ? "> " : string.Empty;
                string status = unit.IsFlatlined
                    ? "FLATLINED"
                    : unit.IsKnockedOut
                        ? $"KO (flatline {unit.FlatlineCounter})"
                        : $"{unit.CurrentStats.HP}/{unit.EffectiveStats.MaxHP}";

                MenuBuilder.AddText(row, $"{marker}{unit.DisplayName} Lv{unit.Level}  {status}", 18f, TextAlignmentOptions.Left);
                MenuBuilder.AddButton(row, unit.CurrentFaction.ToString(), () => CycleFaction(unit), 110f, 34f);
            }
        }

        private void CycleFaction(GridUnit unit)
        {
            Faction next = unit.CurrentFaction switch
            {
                Faction.Player => Faction.Enemy,
                Faction.Enemy => Faction.Neutral,
                _ => Faction.Player,
            };

            unit.SetFaction(next);

            // A side can now be empty. Checked here because no attack happened to check it -
            // exactly the case CombatManager's turn-boundary check comment anticipates.
            missionRunner.Combat.TryEndCombatNow();

            RefreshUnitsPanel();
        }

        // The combat bar is built once, not per screen, so its confirm cannot use the screen-level
        // arming; it relabels itself instead and disarms after a few seconds.
        // Kept so the pad's View button can press it.
        private Button jackOutButton;

        private void AddCombatJackOut(RectTransform parent)
        {
            Button button = null;
            float armedUntil = -1f;

            button = MenuBuilder.AddButton(parent, "Jack Out", () =>
            {
                TMP_Text label = button.GetComponentInChildren<TMP_Text>();

                if (Time.unscaledTime > armedUntil)
                {
                    armedUntil = Time.unscaledTime + 3f;
                    label.text = "Leave fight?";
                    NeonTheme.SetButtonKind(button, NeonTheme.ButtonKind.Danger);
                    return;
                }

                armedUntil = -1f;
                label.text = "Jack Out";
                NeonTheme.SetButtonKind(button, NeonTheme.ButtonKind.Plain);
                missionRunner.Retreat();
            }, 160f, 44f);

            jackOutButton = button;
        }
    }
}
