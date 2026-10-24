using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Tactica.Campaign;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace Tactica.UI
{
    // Every out-of-combat screen: main menu, headquarters, roster, recruitment, mission select,
    // mission results - plus a small in-combat panel for testing factions and retreating.
    //
    // PROTOTYPE UI. Black background, grey buttons, built entirely in code by MenuBuilder, and each
    // screen is rebuilt from scratch whenever anything on it changes. That is wasteful and
    // completely fine for a handful of buttons; a real UI pass replaces this wholesale rather than
    // styling it.
    //
    // Boots itself (see Bootstrap) into any scene with a CombatManager, so no scene edit is
    // needed. Drives CampaignState for the rules and MissionRunner for the board; it holds no
    // rules of its own beyond which screen is showing.
    public class CampaignMenu : MonoBehaviour
    {
        [Tooltip("Defaults to Resources/CampaignConfig.")]
        [SerializeField] private CampaignConfig config;

        [Tooltip("Defaults to a MissionRunner on this GameObject, added if missing.")]
        [SerializeField] private MissionRunner missionRunner;

        // Above CombatHUD's canvas, so a full-screen menu always covers it.
        private const int SortingOrder = 100;
        private const float ColumnWidth = 1100f;

        private CampaignState campaign;
        private MissionResult lastResult;
        private readonly HashSet<MissionUnitReport> recruitedFromResult = new HashSet<MissionUnitReport>();

        private RectTransform background;
        private RectTransform column;
        private RectTransform combatBar;
        private RectTransform unitsPanel;
        private TextMeshProUGUI objectiveLabel;
        private bool unitsPanelOpen;

        // Shown under the current screen until the next screen change. For refusals ("roster
        // full") that would otherwise only reach the console.
        private string notice;

        // Creates the menu on entering Play mode, unless the scene already has one or opts out.
        //
        // AfterSceneLoad runs once the first scene's objects exist, so the CombatManager check can
        // see them. A scene with no CombatManager is not a combat scene and is left alone.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<CampaignMenu>() != null || FindAnyObjectByType<CombatManager>() == null)
            {
                return;
            }

            CampaignConfig loaded = Resources.Load<CampaignConfig>(CampaignConfig.ResourcePath);

            if (loaded == null)
            {
                Debug.LogWarning(
                    $"{nameof(CampaignMenu)}: no {nameof(CampaignConfig)} at Resources/{CampaignConfig.ResourcePath}. " +
                    "Menus disabled; combat can still be started from the CombatManager context menu.");
                return;
            }

            if (!loaded.StartWithMenu)
            {
                return;
            }

            // Inactive while wiring, so Awake sees the config already assigned.
            GameObject menuObject = new GameObject(nameof(CampaignMenu));
            menuObject.SetActive(false);
            CampaignMenu menu = menuObject.AddComponent<CampaignMenu>();
            menu.config = loaded;
            menuObject.SetActive(true);
        }

        private void Awake()
        {
            if (config == null)
            {
                config = Resources.Load<CampaignConfig>(CampaignConfig.ResourcePath);
            }

            if (missionRunner == null)
            {
                missionRunner = GetComponent<MissionRunner>();
            }

            if (missionRunner == null)
            {
                missionRunner = gameObject.AddComponent<MissionRunner>();
            }

            EnsureEventSystem();
            BuildCanvas();
        }

        private void OnEnable()
        {
            missionRunner.OnMissionFinished += HandleMissionFinished;

            CombatManager combat = missionRunner.Combat;

            if (combat != null)
            {
                combat.OnActiveUnitChanged += HandleActiveUnitChanged;
                combat.Resolver.OnStatsChanged += RefreshUnitsPanel;
            }
        }

        private void OnDisable()
        {
            missionRunner.OnMissionFinished -= HandleMissionFinished;

            CombatManager combat = missionRunner.Combat;

            if (combat != null)
            {
                combat.OnActiveUnitChanged -= HandleActiveUnitChanged;
                combat.Resolver.OnStatsChanged -= RefreshUnitsPanel;
            }
        }

        private void Start()
        {
            if (config == null)
            {
                Debug.LogWarning($"{nameof(CampaignMenu)}: no {nameof(CampaignConfig)} assigned or found.", this);
                return;
            }

            ShowMainMenu();
        }

        // ---------------------------------------------------------------------
        // Screens
        // ---------------------------------------------------------------------

        private void ShowMainMenu()
        {
            BeginScreen("TACTICA");
            MenuBuilder.AddText(column, "NEON DISTRICT OPERATIONS  //  prototype", 24f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 24f);

            MenuBuilder.AddButton(column, "New Game", () =>
            {
                campaign = CampaignState.NewGame(config);
                campaign.Save();
                ShowHub();
            });

            MenuBuilder.AddButton(column, "Continue", () =>
            {
                campaign = CampaignState.Load(config);

                if (campaign == null)
                {
                    SetNotice("The save could not be loaded.");
                    ShowMainMenu();
                    return;
                }

                ShowHub();
            }, interactable: CampaignState.HasSave);

            MenuBuilder.AddButton(column, "Quit", Quit);

            EndScreen();
        }

        private void ShowHub()
        {
            BeginScreen("Safehouse");
            MenuBuilder.AddText(column,
                $"Crew {campaign.Roster.Count}/{campaign.MaxRosterSize}   |   " +
                $"Deployed {campaign.DeployedCount}/{campaign.MaxDeployed}   |   " +
                $"Contracts done {campaign.MissionsWon}",
                24f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 24f);

            MenuBuilder.AddButton(column, "Contracts", ShowMissions);
            MenuBuilder.AddButton(column, "Crew", ShowRoster);
            MenuBuilder.AddButton(column, "Hire Runners", ShowRecruit);
            MenuBuilder.AddSpacer(column, 16f);
            MenuBuilder.AddButton(column, "Save & Jack Out", () =>
            {
                campaign.Save();
                ShowMainMenu();
            });

            EndScreen();
        }

        private void ShowRoster()
        {
            BeginScreen("Crew");
            MenuBuilder.AddText(column,
                $"{campaign.Roster.Count}/{campaign.MaxRosterSize} units   |   " +
                $"{campaign.DeployedCount}/{campaign.MaxDeployed} deployed",
                24f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 8f);

            if (campaign.Roster.Count == 0)
            {
                MenuBuilder.AddText(column, "Nobody here. Hire a runner.", 24f);
            }

            foreach (RosterUnit unit in campaign.Roster)
            {
                RectTransform row = MenuBuilder.AddRow(column, 48f);
                MenuBuilder.AddText(row, DescribeRosterUnit(unit), 22f, TextAlignmentOptions.Left);

                MenuBuilder.AddButton(row, unit.Deployed ? "Deployed" : "Reserve", () =>
                {
                    if (!campaign.ToggleDeploy(unit))
                    {
                        SetNotice($"At most {campaign.MaxDeployed} units can be deployed.");
                    }

                    SaveAndShow(ShowRoster);
                }, 150f, 44f);

                MenuBuilder.AddButton(row, "Lv -", () =>
                {
                    campaign.ChangeLevel(unit, -1);
                    SaveAndShow(ShowRoster);
                }, 80f, 44f, unit.Level > 1);

                MenuBuilder.AddButton(row, "Lv +", () =>
                {
                    campaign.ChangeLevel(unit, +1);
                    SaveAndShow(ShowRoster);
                }, 80f, 44f);

                MenuBuilder.AddButton(row, "Dismiss", () =>
                {
                    campaign.Dismiss(unit);
                    SaveAndShow(ShowRoster);
                }, 120f, 44f);
            }

            MenuBuilder.AddSpacer(column, 16f);
            MenuBuilder.AddButton(column, "Back", ShowHub);

            EndScreen();
        }

        private void ShowRecruit()
        {
            BeginScreen("Hire Runners");
            MenuBuilder.AddText(column,
                campaign.HasRosterSpace
                    ? $"Roster {campaign.Roster.Count}/{campaign.MaxRosterSize}"
                    : $"Roster full ({campaign.MaxRosterSize}). Dismiss someone to make room.",
                24f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 8f);

            if (campaign.RecruitPool.Count == 0)
            {
                MenuBuilder.AddText(column, "No candidates. Refresh the list.", 24f);
            }

            foreach (RosterUnit candidate in campaign.RecruitPool)
            {
                RectTransform row = MenuBuilder.AddRow(column, 48f);
                MenuBuilder.AddText(row, DescribeRosterUnit(candidate), 22f, TextAlignmentOptions.Left);

                MenuBuilder.AddButton(row, "Hire", () =>
                {
                    if (campaign.Recruit(candidate))
                    {
                        SetNotice($"{candidate.Name} joined the crew (in reserve).");
                    }

                    SaveAndShow(ShowRecruit);
                }, 150f, 44f, campaign.HasRosterSpace);
            }

            MenuBuilder.AddSpacer(column, 16f);
            MenuBuilder.AddButton(column, "Refresh Listings", () =>
            {
                campaign.RefreshRecruitPool();
                SaveAndShow(ShowRecruit);
            });
            MenuBuilder.AddButton(column, "Back", ShowHub);

            EndScreen();
        }

        private void ShowMissions()
        {
            BeginScreen("Contracts");

            List<RosterUnit> deployed = campaign.GetDeployed();

            MenuBuilder.AddText(column,
                deployed.Count > 0
                    ? "Deployed: " + string.Join(", ", deployed.Select(unit => $"{unit.Name} (Lv{unit.Level})"))
                    : "Nobody is deployed. Pick runners on the Crew screen.",
                22f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 8f);

            foreach (MissionDefinition mission in config.Missions)
            {
                RectTransform row = MenuBuilder.AddRow(column, 72f);
                MenuBuilder.AddText(row,
                    $"<b>{mission.missionName}</b>\n<size=18>{DescribeOpponents(mission)}</size>",
                    22f, TextAlignmentOptions.Left);

                MenuBuilder.AddButton(row, "Start", () => StartMission(mission), 150f, 52f, deployed.Count > 0);
            }

            MenuBuilder.AddSpacer(column, 16f);
            MenuBuilder.AddButton(column, "Back", ShowHub);

            EndScreen();
        }

        private void ShowResult()
        {
            string title = lastResult.Outcome switch
            {
                MissionOutcome.Victory => "Contract Complete",
                MissionOutcome.Defeat => "Contract Failed",
                _ => "Jacked Out",
            };

            BeginScreen(title);
            MenuBuilder.AddText(column, lastResult.Mission.missionName, 24f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddSpacer(column, 8f);

            MenuBuilder.AddText(column, "Your crew", 26f, TextAlignmentOptions.Left);

            foreach (MissionUnitReport report in lastResult.Units.Where(report => report.IsFromRoster))
            {
                string levelUp = lastResult.Outcome == MissionOutcome.Victory && !report.KnockedOut
                    ? $"   ->  Lv{report.RosterSource.Level}"
                    : string.Empty;

                // Spelled out: the crew screen simply no longer lists them, and a silent removal
                // would read as a save bug.
                if (report.Flatlined)
                {
                    levelUp = "   FLATLINED - lost for good";
                }

                MenuBuilder.AddText(column,
                    $"{report.Name}   {DescribeEnd(report)}{levelUp}",
                    22f, TextAlignmentOptions.Left, 34f);
            }

            List<MissionUnitReport> others = lastResult.Units.Where(report => !report.IsFromRoster).ToList();

            if (others.Count > 0)
            {
                MenuBuilder.AddSpacer(column, 8f);
                MenuBuilder.AddText(column, "Others", 26f, TextAlignmentOptions.Left);
                MenuBuilder.AddText(column,
                    "Only units you hacked onto your side (System Override) can join the crew.",
                    18f, TextAlignmentOptions.Left, color: MenuBuilder.DimTextColor);

                foreach (MissionUnitReport report in others)
                {
                    RectTransform row = MenuBuilder.AddRow(column, 46f);
                    MenuBuilder.AddText(row,
                        $"{report.Name}  {JobNameOf(report.Job)} Lv{report.Level}   {DescribeEnd(report)}",
                        22f, TextAlignmentOptions.Left);

                    bool alreadyRecruited = recruitedFromResult.Contains(report);
                    bool canRecruit = !alreadyRecruited
                        && campaign.HasRosterSpace
                        && CampaignState.CanRecruitAfter(lastResult, report);

                    MenuBuilder.AddButton(row, alreadyRecruited ? "Joined" : "Recruit", () =>
                    {
                        RosterUnit joined = campaign.RecruitFromMission(report);

                        if (joined != null)
                        {
                            recruitedFromResult.Add(report);
                            SetNotice($"{joined.Name} joined the crew (in reserve).");
                        }

                        SaveAndShow(ShowResult);
                    }, 150f, 42f, canRecruit);
                }
            }

            MenuBuilder.AddSpacer(column, 16f);
            MenuBuilder.AddButton(column, "Continue", ShowHub);

            EndScreen();
        }

        // ---------------------------------------------------------------------
        // Combat overlay
        // ---------------------------------------------------------------------

        private void StartMission(MissionDefinition mission)
        {
            if (!missionRunner.StartMission(mission, campaign.GetDeployed(), out string error))
            {
                SetNotice(error);
                ShowMissions();
                return;
            }

            notice = null;
            unitsPanelOpen = false;

            background.gameObject.SetActive(false);
            combatBar.gameObject.SetActive(true);
            objectiveLabel.text = "OBJECTIVE: " + mission.DescribeObjective();
            RefreshUnitsPanel();
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

            MenuBuilder.AddText(unitsPanel, "Units  (click a side to change it)", 20f, TextAlignmentOptions.Left, 30f);

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

        // ---------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------

        private void BuildCanvas()
        {
            Canvas canvas = MenuBuilder.CreateOverlayCanvas("CampaignCanvas", transform, SortingOrder);

            background = MenuBuilder.CreateFullScreenPanel(canvas.transform, "Background", Color.black);
            column = MenuBuilder.CreateColumn(background, "Column", ColumnWidth, 12f);

            // Top-right, outside the background so it stays up while the board is visible. Only
            // these rects take raycasts, so the rest of the board stays clickable.
            combatBar = MenuBuilder.CreateRect(canvas.transform, "CombatBar");
            combatBar.anchorMin = combatBar.anchorMax = new Vector2(1f, 1f);
            combatBar.pivot = new Vector2(1f, 1f);
            combatBar.anchoredPosition = new Vector2(-16f, -16f);
            combatBar.sizeDelta = new Vector2(480f, 0f);
            MenuBuilder.AddVerticalLayout(combatBar.gameObject, 8f);
            combatBar.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform buttons = MenuBuilder.AddRow(combatBar, 44f);
            MenuBuilder.AddButton(buttons, "Units / Factions", () =>
            {
                unitsPanelOpen = !unitsPanelOpen;
                RefreshUnitsPanel();
            }, -1f, 44f);
            MenuBuilder.AddButton(buttons, "Jack Out", () => missionRunner.Retreat(), 140f, 44f);

            // The contract's objective stays on screen for the whole fight - a "keep X alive" that
            // is only shown on the briefing gets forgotten by turn three.
            objectiveLabel = MenuBuilder.AddText(combatBar, string.Empty, 20f, TextAlignmentOptions.Right, 28f,
                new Color(0.3f, 0.95f, 0.95f));

            unitsPanel = MenuBuilder.CreateRect(combatBar, "UnitsPanel");
            unitsPanel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);
            MenuBuilder.AddVerticalLayout(unitsPanel.gameObject, 4f, 10);

            combatBar.gameObject.SetActive(false);
            unitsPanel.gameObject.SetActive(false);
        }

        private void BeginScreen(string title)
        {
            background.gameObject.SetActive(true);
            MenuBuilder.Clear(column);
            MenuBuilder.AddText(column, title, 56f, height: 80f);
        }

        private void EndScreen()
        {
            if (string.IsNullOrEmpty(notice))
            {
                return;
            }

            MenuBuilder.AddSpacer(column, 8f);
            MenuBuilder.AddText(column, notice, 22f, color: new Color(1f, 0.8f, 0.35f));
            notice = null;
        }

        private void SetNotice(string message)
        {
            notice = message;
        }

        private void SaveAndShow(System.Action show)
        {
            campaign.Save();
            show();
        }

        // The scene ships with one, but a scene without it gets dead buttons and no error - the
        // failure ARCHITECTURE.md lists under "fail silently". Cheap to guarantee here.
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
            {
                return;
            }

            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));

#if ENABLE_INPUT_SYSTEM
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
#else
            eventSystemObject.AddComponent<StandaloneInputModule>();
#endif
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------------------------------------------------------------------
        // Text
        // ---------------------------------------------------------------------

        private static string DescribeRosterUnit(RosterUnit unit)
        {
            CharacterStats stats = unit.Stats;
            return $"<b>{unit.Name}</b>  {unit.JobName} Lv{unit.Level}   " +
                   $"<size=18>HP {stats.MaxHP}  MP {stats.MaxMP}  STR {stats.Strength}  " +
                   $"DEF {stats.Defense}  SPD {stats.Speed}  MOV {stats.Move}</size>";
        }

        private static string DescribeOpponents(MissionDefinition mission)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(mission.DescribeObjective().ToUpperInvariant()).Append("  -  ");

            if (!string.IsNullOrEmpty(mission.description))
            {
                builder.Append(mission.description).Append("  -  ");
            }

            builder.Append(string.Join(", ", mission.opponents.Select(entry =>
                $"{(string.IsNullOrEmpty(entry.unitName) ? JobNameOf(entry.job) : entry.unitName)} " +
                $"Lv{entry.level} ({entry.faction})")));

            return builder.ToString();
        }

        private static string DescribeEnd(MissionUnitReport report)
        {
            string state = report.Flatlined ? ", FLATLINED" : report.KnockedOut ? ", KO" : string.Empty;
            return $"[{report.FinalFaction}{state}]";
        }

        private static string JobNameOf(JobDefinition job)
        {
            return job != null ? job.JobName : "(no job)";
        }
    }
}
