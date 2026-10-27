using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Campaign;
using Tactica.Grid;

namespace Tactica.UI
{
    // Deployment: after the board is built and before the first turn, the player places the crew
    // inside the teal starting zone. Click a crew member (on the board or in the panel) to select
    // them - the turn triangle marks who - then click a blue tile to move them there. The rules
    // (zone, who may move, free tiles) are MissionRunner's; this only routes clicks and draws the
    // panel.
    public partial class CampaignMenu
    {
        private RectTransform deployPanel;
        private GridCursorHighlighter deployCursor;
        private GridUnit deploySelected;
        private MissionDefinition deployMission;
        private bool deployRoad;
        private string deployMessage;

        // Top-right like the fight overlay, which it stands in for until the battle starts.
        private void BuildDeployPanel(Canvas canvas)
        {
            deployPanel = MenuBuilder.CreateRect(canvas.transform, "DeployPanel");
            deployPanel.anchorMin = deployPanel.anchorMax = new Vector2(1f, 1f);
            deployPanel.pivot = new Vector2(1f, 1f);
            deployPanel.anchoredPosition = new Vector2(-16f, -16f);
            deployPanel.sizeDelta = new Vector2(480f, 0f);
            NeonTheme.SkinPanel(deployPanel.gameObject.AddComponent<Image>());
            MenuBuilder.AddVerticalLayout(deployPanel.gameObject, 6f, 14);
            deployPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            deployPanel.gameObject.SetActive(false);
        }

        private void BeginDeployment(MissionDefinition mission, bool road)
        {
            deployMission = mission;
            deployRoad = road;
            deployMessage = null;

            notice = null;
            backAction = null;
            background.gameObject.SetActive(false);
            combatBar.gameObject.SetActive(false);

            if (deployCursor == null)
            {
                deployCursor = FindAnyObjectByType<GridCursorHighlighter>();
            }

            GridUnit first = missionRunner.Crew.FirstOrDefault();

            if (deployCursor != null)
            {
                deployCursor.TileClicked += HandleDeployClick;

                if (first != null)
                {
                    deployCursor.MoveCursorTo(first.GridCoords);
                }
            }

            SelectForDeployment(first);
        }

        private void EndDeployment()
        {
            if (deployCursor != null)
            {
                deployCursor.TileClicked -= HandleDeployClick;
            }

            SelectForDeployment(null);
            deployPanel.gameObject.SetActive(false);
        }

        // Clicking a crew member selects them; clicking a tile places the selected one there.
        private void HandleDeployClick(Vector2Int coords)
        {
            if (!missionRunner.IsDeploying)
            {
                return;
            }

            GridUnit clicked = UnitAt(coords);

            if (clicked != null && missionRunner.Crew.Contains(clicked))
            {
                deployMessage = null;
                SelectForDeployment(clicked);
                return;
            }

            if (deploySelected == null)
            {
                deployMessage = "Pick a crew member first.";
            }
            else if (missionRunner.TryPlace(deploySelected, coords, out string reason))
            {
                deployMessage = null;
            }
            else
            {
                deployMessage = reason;
            }

            RefreshDeployPanel();
        }

        // The selection is shown with the turn triangle - the board's existing "this one" marker.
        private void SelectForDeployment(GridUnit unit)
        {
            SetIndicator(deploySelected, false);
            deploySelected = unit;
            SetIndicator(deploySelected, true);
            RefreshDeployPanel();
        }

        private static void SetIndicator(GridUnit unit, bool visible)
        {
            if (unit != null && unit.TryGetComponent(out UnitStatusBar bar))
            {
                bar.SetTurnIndicator(visible);
            }
        }

        private void RefreshDeployPanel()
        {
            if (deployPanel == null)
            {
                return;
            }

            deployPanel.gameObject.SetActive(missionRunner.IsDeploying);

            if (!missionRunner.IsDeploying)
            {
                return;
            }

            MenuBuilder.Clear(deployPanel);
            MenuBuilder.AddText(deployPanel, "DEPLOYMENT", 24f, TextAlignmentOptions.Left, 32f, MenuBuilder.AccentTextColor);
            MenuBuilder.AddText(deployPanel,
                GridInput.GamepadActive
                    ? "A on a crew member, then A on a teal tile.  B drops the pick, Start begins."
                    : "Pick a crew member, then a teal tile.  Esc drops the pick.",
                17f, TextAlignmentOptions.Left, 24f, MenuBuilder.DimTextColor);
            MenuBuilder.AddText(deployPanel, "Objective: " + deployMission.DescribeObjective(), 17f,
                TextAlignmentOptions.Left, 24f, MenuBuilder.AccentTextColor);

            foreach (GridUnit unit in missionRunner.Crew)
            {
                GridUnit crewMember = unit;
                MenuBuilder.AddButton(deployPanel, $"{unit.DisplayName}  <size=16>{unit.CurrentJob.JobName} Lv{unit.Level}</size>",
                    () => SelectForDeployment(crewMember), -1f, SmallButtonHeight, true, MenuBuilder.On(unit == deploySelected));
            }

            if (!string.IsNullOrEmpty(deployMessage))
            {
                MenuBuilder.AddText(deployPanel, deployMessage, 17f, TextAlignmentOptions.Left, 24f, MenuBuilder.WarningTextColor);
            }

            MenuBuilder.AddSpacer(deployPanel, 4f);
            MenuBuilder.AddButton(deployPanel, "Start Battle", StartBattle, -1f, RowButtonHeight, true, MenuBuilder.PrimaryColor);
            MenuBuilder.AddButton(deployPanel, deployRoad ? "Turn Back" : "Back to Briefing", CancelDeploymentToBriefing,
                -1f, SmallButtonHeight);
        }

        private void StartBattle()
        {
            EndDeployment();

            if (!missionRunner.BeginBattle())
            {
                return;
            }

            unitsPanelOpen = false;
            combatBar.gameObject.SetActive(true);
            objectiveLabel.text = "OBJECTIVE: " + deployMission.DescribeObjective();
            RefreshUnitsPanel();
        }

        // Back to the briefing with nothing spent. A road ambush's briefing still offers Turn Back.
        private void CancelDeploymentToBriefing()
        {
            EndDeployment();
            missionRunner.CancelDeployment();
            ShowBriefing(deployMission, deployRoad);
        }

        // Escape during deployment drops the selection rather than leaving.
        private void HandleDeployEscape()
        {
            if (missionRunner.IsDeploying && deploySelected != null)
            {
                SelectForDeployment(null);
            }
        }

        private GridUnit UnitAt(Vector2Int coords)
        {
            return missionRunner.SpawnedUnits.FirstOrDefault(unit => unit != null && unit.GridCoords == coords);
        }
    }
}
