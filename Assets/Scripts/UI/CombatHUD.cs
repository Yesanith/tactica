using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Tactica.UI
{
    // Screen-space combat HUD: who is acting, who is targeted, and what they can do.
    // Attach to a Screen Space - Overlay Canvas.
    public class CombatHUD : MonoBehaviour
    {
        [Tooltip("Combat to follow. Required - without it the HUD never learns whose turn it is.")]
        [SerializeField] private CombatManager combatManagerRef;

        [Header("Panels")]
        [Tooltip("Bottom-left. Shows whoever currently holds the turn.")]
        [SerializeField] private UnitInfoPanel activeUnitPanel;

        [Tooltip("Beside the active panel. Hidden until something is targeted.")]
        [SerializeField] private UnitInfoPanel targetUnitPanel;

        [Header("Action Menu")]
        // Button, not a bare Image, because these are interactive: they need pointer enter/exit and
        // pressed visuals, keyboard and gamepad navigation, a disabled state for actions the unit
        // cannot afford, and an onClick event. That is precisely what Selectable provides - and
        // precisely what the HP/MP bars do not need, which is why those stayed plain Images.
        [Tooltip("Handles what each action actually does. Without it the buttons only log.")]
        [SerializeField] private PlayerActionController actionController;

        [SerializeField] private Button attackButton;
        [SerializeField] private Button moveButton;

        // Wait is the only way to end a turn. There was a separate End Turn button that called the
        // same code with a different log line; it was removed once Wait grew a rule of its own
        // (the defensive stance), which is the distinction the two buttons never had.
        [SerializeField] private Button waitButton;

        // Whose bar currently shows the turn triangle, so it can be switched off when the turn
        // moves on. Unity's null check covers a unit destroyed since - e.g. a mission torn down.
        private GridUnit indicatedUnit;

        // Built in code at Awake, on this HUD's own canvas: the Act menu (one button per unlocked
        // ability), the targeting preview line, and the CT turn queue. Code-built so the authored
        // HUD prefab did not need to change.
        private RectTransform abilityMenu;
        private TextMeshProUGUI previewLabel;
        private GridCursorHighlighter cursor;

        private void Awake()
        {
            // Hidden by default. Done in Awake rather than relying on the prefab being saved
            // deactivated, so the panel state is guaranteed regardless of how the scene was left.
            HideTargetPanel();

            cursor = FindAnyObjectByType<GridCursorHighlighter>();

            BuildAbilityMenu();
            BuildPreviewLabel();
            TurnQueueStrip.Create(transform, combatManagerRef);

            // FFT's command is "Act", not "Attack": the menu behind it holds hacks and reboots too.
            // The number is the keyboard shortcut (see HandleHotkeys).
            SetButtonLabel(attackButton, "Act  [1]");
            SetButtonLabel(moveButton, "Move  [2]");
            SetButtonLabel(waitButton, "Wait  [3]");

            // The authored buttons are Unity's default white; match them to the menus.
            StyleButton(attackButton);
            StyleButton(moveButton);
            StyleButton(waitButton);
        }

        private static void StyleButton(Button button)
        {
            if (button == null)
            {
                return;
            }

            if (button.image != null)
            {
                button.image.color = MenuBuilder.ButtonColor;
            }

            ColorBlock colors = button.colors;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            button.colors = colors;

            TMP_Text text = button.GetComponentInChildren<TMP_Text>();

            if (text != null)
            {
                text.color = MenuBuilder.TextColor;
            }
        }

        // The targeting preview follows the cursor, so it is polled rather than evented - the
        // hovered tile changes with the camera as well as the mouse.
        private void Update()
        {
            RefreshCommandButtons();
            HandleHotkeys();

            if (previewLabel == null)
            {
                return;
            }

            string preview = null;

            if (actionController != null && actionController.Mode == PlayerActionMode.ChoosingFacing)
            {
                preview = "Choose facing: click a lit tile, or press Wait again to keep it";
            }
            else if (actionController != null && cursor != null && cursor.HoveredTile.HasValue)
            {
                preview = actionController.DescribeTargetPreview(cursor.HoveredTile.Value);
            }

            // With nothing hovered, say what the board is waiting for.
            if (string.IsNullOrEmpty(preview) && actionController != null && actionController.Mode == PlayerActionMode.Moving)
            {
                preview = "Move: click a lit tile   (Esc / right click to cancel)";
            }
            else if (string.IsNullOrEmpty(preview) && actionController != null && actionController.PendingAbility != null)
            {
                preview = $"{actionController.PendingAbility.AbilityName}: pick a target   (Esc / right click to cancel)";
            }

            previewLabel.transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(preview));

            if (!string.IsNullOrEmpty(preview))
            {
                previewLabel.text = preview;
            }
        }

        // OnEnable/OnDisable for both the combat event and the button callbacks. Button listeners
        // in particular stack: AddListener in Awake without a matching RemoveListener means a
        // second subscription every time the object is re-enabled, and the handler then fires
        // twice per click.
        private void OnEnable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnActiveUnitChanged += HandleActiveUnitChanged;

                // Stat changes have no event of their own - CurrentStats is a plain field, so
                // nothing observes a write. ActionResolver raises this after an action lands.
                combatManagerRef.Resolver.OnStatsChanged += HandleStatsChanged;
            }

            if (actionController != null)
            {
                actionController.OnTargetSelected += ShowTargetPanel;
            }

            if (attackButton != null) attackButton.onClick.AddListener(OnAttackPressed);
            if (moveButton != null) moveButton.onClick.AddListener(OnMovePressed);
            if (waitButton != null) waitButton.onClick.AddListener(OnWaitPressed);
        }

        private void OnDisable()
        {
            if (combatManagerRef != null)
            {
                combatManagerRef.OnActiveUnitChanged -= HandleActiveUnitChanged;
                combatManagerRef.Resolver.OnStatsChanged -= HandleStatsChanged;
            }

            if (actionController != null)
            {
                actionController.OnTargetSelected -= ShowTargetPanel;
            }

            if (attackButton != null) attackButton.onClick.RemoveListener(OnAttackPressed);
            if (moveButton != null) moveButton.onClick.RemoveListener(OnMovePressed);
            if (waitButton != null) waitButton.onClick.RemoveListener(OnWaitPressed);

            // Stops following turns, so must not leave a stale triangle behind.
            SetTurnIndicator(null);
        }

        private void Start()
        {
            if (combatManagerRef == null)
            {
                Debug.LogWarning(
                    $"{nameof(CombatHUD)}: no {nameof(combatManagerRef)} assigned. The HUD will never update.",
                    this);
                return;
            }

            // Catches the case where combat started before this HUD was enabled - otherwise the
            // panel stays blank until the next turn change.
            HandleActiveUnitChanged(combatManagerRef.GetActiveUnit());
        }

        // Redraws the active unit's panel from its current stats.
        public void RefreshActivePanel()
        {
            if (activeUnitPanel != null)
            {
                activeUnitPanel.Refresh();
            }
        }

        // Subscribed to PlayerActionController.OnTargetSelected, which passes null when targeting is
        // abandoned - so the same handler covers both showing and hiding.
        public void ShowTargetPanel(GridUnit target)
        {
            if (targetUnitPanel == null)
            {
                return;
            }

            if (target == null)
            {
                HideTargetPanel();
                return;
            }

            targetUnitPanel.gameObject.SetActive(true);
            targetUnitPanel.Bind(target);
        }

        public void HideTargetPanel()
        {
            if (targetUnitPanel != null)
            {
                targetUnitPanel.gameObject.SetActive(false);
            }
        }

        private void HandleActiveUnitChanged(GridUnit unit)
        {
            if (activeUnitPanel != null)
            {
                activeUnitPanel.Bind(unit);

                // Combat ending passes null; blank the panel rather than leaving the last unit up.
                activeUnitPanel.gameObject.SetActive(unit != null);
            }

            SetTurnIndicator(unit);
            CloseAbilityMenu();

            // A new turn means the previous turn's target is no longer meaningful.
            HideTargetPanel();
        }

        // Polled: acting and moving have no event of their own. Grey on anyone else's turn (the
        // controller refuses those presses anyway - this is so they do not look clickable while
        // the AI acts), and Act / Move grey once spent, so the buttons show what is left of the turn.
        private void RefreshCommandButtons()
        {
            GridUnit unit = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;
            bool playerTurn = unit != null && unit.IsPlayerControlled
                && combatManagerRef.CurrentState == CombatState.WaitingForInput;

            if (attackButton != null) attackButton.interactable = playerTurn && !unit.HasActedThisTurn;
            if (moveButton != null) moveButton.interactable = playerTurn && !unit.HasMovedThisTurn;
            if (waitButton != null) waitButton.interactable = playerTurn;
        }

        // 1 / 2 / 3 press Act / Move / Wait; Escape or a right click backs out of the open menu or
        // the command in progress. WASD and Q/E already belong to the camera.
        private void HandleHotkeys()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            bool cancel = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                || (mouse != null && mouse.rightButton.wasPressedThisFrame);

            if (cancel)
            {
                if (abilityMenu != null && abilityMenu.gameObject.activeSelf)
                {
                    CloseAbilityMenu();
                }
                else if (actionController != null)
                {
                    actionController.CancelCommand();
                }
            }

            if (keyboard == null)
            {
                return;
            }

            if (keyboard.digit1Key.wasPressedThisFrame && attackButton != null && attackButton.interactable) OnAttackPressed();
            if (keyboard.digit2Key.wasPressedThisFrame && moveButton != null && moveButton.interactable) OnMovePressed();
            if (keyboard.digit3Key.wasPressedThisFrame && waitButton != null && waitButton.interactable) OnWaitPressed();
#endif
        }

        // Moves the triangle from the previous turn holder's bar to this unit's. Null clears it -
        // combat ending passes null, and no one should look like they are still acting.
        private void SetTurnIndicator(GridUnit unit)
        {
            if (indicatedUnit != null && indicatedUnit != unit)
            {
                UnitStatusBar previousBar = indicatedUnit.GetComponent<UnitStatusBar>();

                if (previousBar != null)
                {
                    previousBar.SetTurnIndicator(false);
                }
            }

            indicatedUnit = unit;

            if (unit != null)
            {
                UnitStatusBar bar = unit.GetComponent<UnitStatusBar>();

                if (bar != null)
                {
                    bar.SetTurnIndicator(true);
                }
            }
        }

        // Refresh whatever is on screen, not just the active panel: an attack changes the target's
        // HP too, and a target panel showing stale numbers right after a hit is the most visible
        // possible bug.
        private void HandleStatsChanged()
        {
            RefreshActivePanel();

            if (targetUnitPanel != null && targetUnitPanel.gameObject.activeSelf)
            {
                targetUnitPanel.Refresh();
            }
        }

        // The HUD says what was pressed; PlayerActionController decides what that does. Keeping
        // the rules out of the button handlers means the same actions can later be driven by a
        // keyboard shortcut or an AI without duplicating any of this.
        // Act opens the ability list rather than acting directly; pressing it again closes it.
        private void OnAttackPressed()
        {
            if (actionController == null)
            {
                Debug.LogWarning($"{nameof(CombatHUD)}: no {nameof(actionController)} assigned.", this);
                return;
            }

            if (abilityMenu.gameObject.activeSelf)
            {
                CloseAbilityMenu();
                return;
            }

            OpenAbilityMenu();
        }

        private void OpenAbilityMenu()
        {
            MenuBuilder.Clear(abilityMenu);

            GridUnit unit = combatManagerRef != null ? combatManagerRef.GetActiveUnit() : null;
            List<AbilityDefinition> abilities = actionController.GetAvailableAbilities();

            if (unit == null || abilities.Count == 0)
            {
                MenuBuilder.AddText(abilityMenu, "No abilities.", 18f, TextAlignmentOptions.Left, 28f);
            }
            else if (unit.HasActedThisTurn)
            {
                MenuBuilder.AddText(abilityMenu, "Already acted this turn.", 18f, TextAlignmentOptions.Left, 28f);
            }

            foreach (AbilityDefinition ability in abilities)
            {
                // The resolver's own check (MP, weapon in hand), so the menu cannot offer what
                // execution would refuse; the reason rides on the label.
                bool canUse = combatManagerRef.Resolver.CanUse(unit, ability, out string reason);
                bool usable = canUse && unit != null && !unit.HasActedThisTurn;
                string cost = ability.MPCost > 0 ? $"  {ability.MPCost} MP" : string.Empty;
                string why = canUse ? string.Empty : $"  ({reason})";
                int area = combatManagerRef.Resolver.GetArea(unit, ability);
                string reach = $"  R{combatManagerRef.Resolver.GetBaseRange(unit, ability)}" + (area > 0 ? $" A{area}" : string.Empty);

                MenuBuilder.AddButton(abilityMenu, $"{ability.AbilityName}{cost}{reach}{why}", () =>
                {
                    CloseAbilityMenu();
                    actionController.BeginAttack(ability);
                }, -1f, 40f, usable);

                if (!string.IsNullOrEmpty(ability.Description))
                {
                    MenuBuilder.AddText(abilityMenu, ability.Description, 14f, TextAlignmentOptions.Left, 20f, MenuBuilder.DimTextColor);
                }
            }

            MenuBuilder.AddText(abilityMenu, "Esc / right click to close", 13f, TextAlignmentOptions.Right, 18f, MenuBuilder.DimTextColor);

            abilityMenu.gameObject.SetActive(true);
        }

        private void CloseAbilityMenu()
        {
            if (abilityMenu != null)
            {
                abilityMenu.gameObject.SetActive(false);
            }
        }

        // Above the action buttons, bottom-right. Anchored to the canvas rather than the Act
        // button, so it works however the authored button panel is laid out.
        private void BuildAbilityMenu()
        {
            abilityMenu = MenuBuilder.CreateRect(transform, "AbilityMenu");
            abilityMenu.anchorMin = abilityMenu.anchorMax = new Vector2(1f, 0f);
            abilityMenu.pivot = new Vector2(1f, 0f);
            abilityMenu.anchoredPosition = new Vector2(-16f, 140f);
            abilityMenu.sizeDelta = new Vector2(380f, 0f);

            abilityMenu.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.02f, 0.06f, 0.9f);
            MenuBuilder.AddVerticalLayout(abilityMenu.gameObject, 4f, 8);
            abilityMenu.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            abilityMenu.gameObject.SetActive(false);
        }

        // Top-centre: what the pending ability would do to the hovered target.
        private void BuildPreviewLabel()
        {
            RectTransform box = MenuBuilder.CreateRect(transform, "TargetPreview");
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 1f);
            box.pivot = new Vector2(0.5f, 1f);
            box.anchoredPosition = new Vector2(0f, -16f);
            box.sizeDelta = new Vector2(640f, 44f);

            Image background = box.gameObject.AddComponent<Image>();
            background.color = new Color(0.02f, 0.02f, 0.06f, 0.85f);
            background.raycastTarget = false;

            RectTransform labelRect = MenuBuilder.CreateRect(box, "Label");
            MenuBuilder.Stretch(labelRect);

            previewLabel = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            previewLabel.fontSize = 22f;
            previewLabel.alignment = TextAlignmentOptions.Center;
            previewLabel.color = new Color(0.3f, 0.95f, 0.95f);
            previewLabel.raycastTarget = false;

            box.gameObject.SetActive(false);
        }

        private static void SetButtonLabel(Button button, string label)
        {
            if (button == null)
            {
                return;
            }

            TMP_Text text = button.GetComponentInChildren<TMP_Text>();

            if (text != null)
            {
                text.text = label;
            }
        }

        private void OnMovePressed()
        {
            if (actionController != null) actionController.BeginMove();
            else Debug.LogWarning($"{nameof(CombatHUD)}: no {nameof(actionController)} assigned.", this);
        }

        // Whether this also grants a defensive stance is PlayerActionController's call, not the
        // HUD's - the button reports the press and nothing more.
        private void OnWaitPressed()
        {
            Debug.Log("HUD: Wait.", this);

            if (actionController != null) actionController.Wait();
            else Debug.LogWarning($"{nameof(CombatHUD)}: no {nameof(actionController)} assigned.", this);
        }
    }
}
