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
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace Tactica.UI
{
    // Every out-of-combat screen. Split into partial files by area:
    //   CampaignMenu.cs         boot, the shared screen frame, main menu, crew, hiring, results
    //   CampaignMenu.Map.cs     district hub, job board, briefing, travel, shop
    //   CampaignMenu.Unit.cs    the crew member screen (card + Jobs / Equipment / Learn tabs)
    //   CampaignMenu.Deploy.cs  placing the crew in the starting zone before the first turn
    //   CampaignMenu.Combat.cs  starting a fight, the in-fight overlay, taking the result back
    //   CampaignMenu.Text.cs    shared descriptions (status strip, weapons, roster rows)
    //
    // PROTOTYPE UI. Black background, grey buttons, built entirely in code by MenuBuilder, and each
    // screen is rebuilt from scratch whenever anything on it changes. That is wasteful and
    // completely fine for a handful of buttons; a real UI pass replaces this wholesale rather than
    // styling it.
    //
    // Every screen follows the same frame, so they read as one tool: title, the status strip
    // (district, credits, crew), the content (scrolls when it is taller than the window), any
    // notice, then Back. Escape does what Back does. Colour carries meaning only - see
    // MenuBuilder's SelectedColor / PrimaryColor / DangerColor.
    //
    // Boots itself (see Bootstrap) into any scene with a CombatManager, so no scene edit is
    // needed. Drives CampaignState for the rules and MissionRunner for the board; it holds no
    // rules of its own beyond which screen is showing.
    public partial class CampaignMenu : MonoBehaviour
    {
        [Tooltip("Defaults to Resources/CampaignConfig.")]
        [SerializeField] private CampaignConfig config;

        [Tooltip("Defaults to a MissionRunner on this GameObject, added if missing.")]
        [SerializeField] private MissionRunner missionRunner;

        // Above CombatHUD's canvas, so a full-screen menu always covers it.
        private const int SortingOrder = 100;
        private const float ColumnWidth = 1100f;

        // Row heights, so every list on every screen lines up the same way.
        private const float ListRowHeight = 64f;
        private const float SmallRowHeight = 42f;
        private const float RowButtonHeight = 44f;
        private const float SmallButtonHeight = 36f;

        private CampaignState campaign;
        private MissionResult lastResult;
        private readonly HashSet<MissionUnitReport> recruitedFromResult = new HashSet<MissionUnitReport>();

        private RectTransform background;
        private RectTransform column;
        private ScrollRect columnScroll;
        // Shown under the current screen until the next screen change. For refusals ("roster
        // full") that would otherwise only reach the console.
        private string notice;

        // Where Back and Escape go from the current screen. Null on screens with no way back
        // (main menu, the district hub).
        private System.Action backAction;
        private string currentBackLabel = "Back";

        // Two-click confirmation for anything that cannot be undone. The first click arms the
        // button (it turns red and says what will happen) and redraws; any other navigation
        // disarms it, because BeginScreen moves nextConfirm into armedConfirm and clears it.
        private string armedConfirm;
        private string nextConfirm;

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

        // Escape (or B on the pad) is Back on every menu screen. Not during a fight: there it
        // belongs to the combat HUD (cancel the current command). While deploying, the board has
        // the pad - A picks and places on the cursor's tile - and Start begins the battle.
        private void Update()
        {
            // Menus take the pad's focus; in a fight or while deploying the board has it, so a
            // button clicked with the mouse must not stay selected.
            if (background.gameObject.activeSelf)
            {
                PadFocus.Keep(column, currentScreen);
                HandleMenuShortcuts();
            }
            else
            {
                PadFocus.Release(background.parent);

                if (missionRunner.IsDeploying)
                {
                    PromptBar.Set(PromptBar.Join(PromptBar.A, "Pick / place", PromptBar.B, "Drop pick",
                        PromptBar.Glyph("Start"), "Begin", PromptBar.Glyph("LB/RB"), "Turn camera"));
                }
                else if (combatBar.gameObject.activeSelf && GridInput.WasPadPressed(GridInput.PadButton.View) && jackOutButton != null)
                {
                    // Pressed twice, like the button: the first press arms it.
                    jackOutButton.onClick.Invoke();
                }
            }

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            bool back = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || GridInput.WasPadPressed(GridInput.PadButton.Cancel);

            if (back)
            {
                if (background.gameObject.activeSelf && backAction != null)
                {
                    backAction();
                }
                else if (missionRunner.IsDeploying)
                {
                    HandleDeployEscape();
                }
            }
            else if (missionRunner.IsDeploying && !background.gameObject.activeSelf && GridInput.WasPadPressed(GridInput.PadButton.Wait))
            {
                StartBattle();
            }
#endif
        }

        // ---------------------------------------------------------------------
        // Screens
        // ---------------------------------------------------------------------

        private void ShowMainMenu()
        {
            BeginScreen("TACTICA", null, showStatus: false);
            TMPro.TextMeshProUGUI tagline = MenuBuilder.AddText(column, "NEON DISTRICT OPERATIONS  //  PROTOTYPE", 24f, color: MenuBuilder.StoryTextColor);
            NeonTheme.Style(tagline, NeonTheme.TextRole.Display);
            MenuBuilder.AddSpacer(column, 24f);

            if (CampaignState.HasSave)
            {
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
                }, color: MenuBuilder.PrimaryColor);
            }

            // A new game replaces the save, so with one on disk it asks first.
            System.Action newGame = () =>
            {
                campaign = CampaignState.NewGame(config);
                campaign.Save();
                ShowHub();
            };

            if (CampaignState.HasSave)
            {
                AddConfirmButton(column, "new-game", "New Game", "Start over? The current save is replaced", newGame, ShowMainMenu);
            }
            else
            {
                MenuBuilder.AddButton(column, "New Game", () => newGame(), color: MenuBuilder.PrimaryColor);
            }

            MenuBuilder.AddButton(column, "Quit", Quit);

            EndScreen();
        }

        private void ShowRoster()
        {
            BeginScreen("Crew", ShowHub);

            if (campaign.Roster.Count == 0)
            {
                MenuBuilder.AddText(column, "Nobody here. Hire a runner.", 24f);
            }

            foreach (RosterUnit unit in campaign.Roster)
            {
                RectTransform row = MenuBuilder.AddRow(column, ListRowHeight);
                MenuBuilder.AddText(row, DescribeRosterUnit(unit), 22f, TextAlignmentOptions.Left);

                AddDeployToggle(row, unit, ShowRoster);
                MenuBuilder.AddButton(row, "Manage", () => ShowUnit(unit, unit.Job), 130f, RowButtonHeight);

                // The last crew member cannot be dismissed: a crew of nobody cannot take a contract
                // and has no money-free way back.
                AddConfirmButton(row, "dismiss-" + unit.Name, "Dismiss", "Sure?", () =>
                {
                    campaign.Dismiss(unit);
                    SetNotice($"{unit.Name} left the crew.");
                    SaveAndShow(ShowRoster);
                }, ShowRoster, 120f, RowButtonHeight, campaign.Roster.Count > 1);
            }

            EndScreen();
        }

        private void ShowRecruit()
        {
            BeginScreen("Hire Runners", ShowHub);

            if (!campaign.HasRosterSpace)
            {
                MenuBuilder.AddText(column, $"Roster full ({campaign.MaxRosterSize}). Dismiss someone to make room.",
                    22f, color: MenuBuilder.WarningTextColor);
            }

            if (campaign.RecruitPool.Count == 0)
            {
                MenuBuilder.AddText(column, "No candidates. Refresh the list.", 24f);
            }

            foreach (RosterUnit candidate in campaign.RecruitPool)
            {
                RectTransform row = MenuBuilder.AddRow(column, ListRowHeight);
                MenuBuilder.AddText(row, DescribeRosterUnit(candidate), 22f, TextAlignmentOptions.Left);

                int cost = campaign.HireCost(candidate);
                bool affordable = campaign.Credits >= cost;

                MenuBuilder.AddButton(row, affordable ? $"Hire {cost} cr" : $"{cost} cr - short", () =>
                {
                    if (campaign.Recruit(candidate))
                    {
                        SetNotice($"{candidate.Name} joined the crew (in reserve).");
                    }

                    SaveAndShow(ShowRecruit);
                }, 190f, RowButtonHeight, campaign.HasRosterSpace && affordable);
            }

            MenuBuilder.AddSpacer(column, 8f);
            MenuBuilder.AddButton(column, "Refresh Listings", () =>
            {
                campaign.RefreshRecruitPool();
                SaveAndShow(ShowRecruit);
            }, -1f, RowButtonHeight);

            EndScreen();
        }

        private void ShowResult()
        {
            bool won = lastResult.Outcome == MissionOutcome.Victory;
            string title = lastResult.WasRoadEncounter
                ? (won ? "Road Cleared" : lastResult.Outcome == MissionOutcome.Defeat ? "Ambush Lost" : "Turned Back")
                : lastResult.Outcome switch
                {
                    MissionOutcome.Victory => lastResult.Mission.kind == MissionKind.Story ? "Story Complete" : "Contract Complete",
                    MissionOutcome.Defeat => "Contract Failed",
                    _ => "Jacked Out",
                };

            BeginScreen(title, ShowHub, backLabel: "Continue");
            MenuBuilder.AddText(column, lastResult.Mission.missionName, 24f, color: MenuBuilder.DimTextColor);
            AddRewardLines();

            MenuBuilder.AddHeader(column, "Your crew");

            foreach (MissionUnitReport report in lastResult.Units.Where(report => report.IsFromRoster))
            {
                // What the fight was worth: JP into the primary job (plus the quarter shared by the
                // rest of the crew) and XP toward the next character level.
                string shared = report.SharedJP > 0 ? $" (+{report.SharedJP} shared)" : string.Empty;
                string levels = report.LevelsGained > 0
                    ? $"   <color={MenuBuilder.CreditsHex}>LEVEL UP -> Lv{report.RosterSource.Level}</color>"
                    : string.Empty;
                string gains = report.RosterSource.Locked
                    ? "   (locked - no JP)"
                    : $"   +{report.EarnedJP} JP{shared}   +{report.EarnedXP} XP{levels}";

                // Spelled out: the crew screen simply no longer lists them, and a silent removal
                // would read as a save bug.
                if (report.Flatlined)
                {
                    gains = $"   <color={MenuBuilder.DangerHex}>FLATLINED - lost for good</color>";
                }

                MenuBuilder.AddText(column, $"<b>{report.Name}</b>   {DescribeEnd(report)}{gains}",
                    22f, TextAlignmentOptions.Left, 34f);
            }

            List<MissionUnitReport> others = lastResult.Units.Where(report => !report.IsFromRoster).ToList();

            if (others.Count > 0)
            {
                MenuBuilder.AddHeader(column, "Others");
                MenuBuilder.AddText(column,
                    "Only units you hacked onto your side (System Override) can join the crew.",
                    17f, TextAlignmentOptions.Left, color: MenuBuilder.DimTextColor);

                foreach (MissionUnitReport report in others)
                {
                    RectTransform row = MenuBuilder.AddRow(column, SmallRowHeight);
                    MenuBuilder.AddText(row,
                        $"{report.Name}  <color={MenuBuilder.DimHex}>{JobNameOf(report.Job)} Lv{report.Level}</color>   {DescribeEnd(report)}",
                        20f, TextAlignmentOptions.Left);

                    bool alreadyRecruited = recruitedFromResult.Contains(report) || report.JoinedCrew;
                    bool canRecruit = CampaignState.CanRecruitAfter(lastResult, report);

                    // Only offered to those who could join; everyone else just gets the line.
                    if (!alreadyRecruited && !canRecruit)
                    {
                        continue;
                    }

                    MenuBuilder.AddButton(row, alreadyRecruited ? "Joined" : campaign.HasRosterSpace ? "Recruit" : "Roster full", () =>
                    {
                        RosterUnit joined = campaign.RecruitFromMission(report);

                        if (joined != null)
                        {
                            recruitedFromResult.Add(report);
                            SetNotice($"{joined.Name} joined the crew (in reserve).");
                        }

                        SaveAndShow(ShowResult);
                    }, 160f, SmallButtonHeight, !alreadyRecruited && campaign.HasRosterSpace,
                        alreadyRecruited ? MenuBuilder.SelectedColor : MenuBuilder.PrimaryColor);
                }
            }

            EndScreen();
        }

        // ---------------------------------------------------------------------
        // Construction
        // ---------------------------------------------------------------------

        private void BuildCanvas()
        {
            Canvas canvas = MenuBuilder.CreateOverlayCanvas("CampaignCanvas", transform, SortingOrder);

            background = MenuBuilder.CreateFullScreenPanel(canvas.transform, "Background", Color.black);
            column = MenuBuilder.CreateScrollColumn(background, "Column", ColumnWidth, 12f, out columnScroll);

            BuildCombatBar(canvas);
            BuildDeployPanel(canvas);
        }

        // The common frame of every screen. back: where Back and Escape go (null for none);
        // backLabel renames the button when "Back" is the wrong word (the result screen's
        // Continue). showStatus puts the campaign strip under the title. width widens the column
        // for screens laid out side by side (the unit screen); everything else uses ColumnWidth.
        // The title of the screen on show - the pad's focus keeps its place only within one.
        private string currentScreen;

        // A screen's pad shortcuts, set after BeginScreen and cleared by it: the shoulders page
        // (the crew screen flips between members), the triggers switch tabs. -1 back, +1 forward.
        private System.Action<int> pageAction;
        private string pageLabel;
        private System.Action<int> tabAction;

        // The shoulders and triggers on a menu screen, and the strip saying what the pad does here.
        private void HandleMenuShortcuts()
        {
            if (pageAction != null)
            {
                if (GridInput.WasPadPressed(GridInput.PadButton.ShoulderLeft)) pageAction(-1);
                else if (GridInput.WasPadPressed(GridInput.PadButton.ShoulderRight)) pageAction(1);
            }

            if (tabAction != null)
            {
                if (GridInput.WasPadPressed(GridInput.PadButton.TriggerLeft)) tabAction(-1);
                else if (GridInput.WasPadPressed(GridInput.PadButton.TriggerRight)) tabAction(1);
            }

            PromptBar.Set(PromptBar.Join(
                PromptBar.A, "Select",
                backAction != null ? PromptBar.B : null, currentBackLabel,
                pageAction != null ? PromptBar.Glyph("LB/RB") : null, pageLabel,
                tabAction != null ? PromptBar.Glyph("LT/RT") : null, "Tab"));
        }

        private void BeginScreen(string title, System.Action back, bool showStatus = true, string backLabel = "Back",
            float width = ColumnWidth)
        {
            background.gameObject.SetActive(true);
            MenuBuilder.Clear(column);
            column.sizeDelta = new Vector2(width, column.sizeDelta.y);
            columnScroll.verticalNormalizedPosition = 1f;
            column.anchoredPosition = Vector2.zero;

            backAction = back;
            currentBackLabel = backLabel;
            pageAction = null;
            pageLabel = null;
            tabAction = null;
            armedConfirm = nextConfirm;
            nextConfirm = null;

            MenuBuilder.AddTitle(column, title);
            currentScreen = title;

            if (showStatus && campaign != null)
            {
                MenuBuilder.AddText(column, DescribeStatusStrip(), 20f, height: 30f, color: MenuBuilder.DimTextColor);
            }

            MenuBuilder.AddSpacer(column, 8f);
        }

        private void EndScreen()
        {
            if (!string.IsNullOrEmpty(notice))
            {
                MenuBuilder.AddSpacer(column, 4f);
                MenuBuilder.AddText(column, notice, 22f, color: MenuBuilder.WarningTextColor);
                notice = null;
            }

            if (backAction != null)
            {
                System.Action back = backAction;
                MenuBuilder.AddSpacer(column, 12f);
                MenuBuilder.AddButton(column, $"{currentBackLabel}   <size=16><color={MenuBuilder.DimHex}>[Esc]</color></size>", () => back());
            }

            MenuBuilder.SettleScroll(columnScroll);
        }

        // A button that needs two clicks. redraw rebuilds the current screen so the armed state
        // shows; any other click elsewhere disarms it (see armedConfirm).
        private void AddConfirmButton(
            Transform parent, string key, string label, string confirmLabel, System.Action action, System.Action redraw,
            float width = -1f, float height = 52f, bool interactable = true)
        {
            bool armed = armedConfirm == key;

            MenuBuilder.AddButton(parent, armed ? confirmLabel : label, () =>
            {
                if (!armed)
                {
                    nextConfirm = key;
                    redraw();
                    return;
                }

                action();
            }, width, height, interactable, armed ? MenuBuilder.DangerColor : (Color?)null);
        }

        // On (teal) when deployed. The same control on the crew list and the briefing.
        private void AddDeployToggle(RectTransform row, RosterUnit unit, System.Action redraw)
        {
            MenuBuilder.AddButton(row, unit.Deployed ? "Deployed" : "Reserve", () =>
            {
                if (!campaign.ToggleDeploy(unit))
                {
                    SetNotice($"At most {campaign.MaxDeployed} units can be deployed.");
                }

                SaveAndShow(redraw);
            }, 150f, RowButtonHeight, color: MenuBuilder.On(unit.Deployed));
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

    }
}
