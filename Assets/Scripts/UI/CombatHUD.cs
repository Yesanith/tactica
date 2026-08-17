using UnityEngine;
using UnityEngine.UI;
using Tactica.Combat;
using Tactica.Grid;

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

        private void Awake()
        {
            // Hidden by default. Done in Awake rather than relying on the prefab being saved
            // deactivated, so the panel state is guaranteed regardless of how the scene was left.
            HideTargetPanel();
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

            // A new turn means the previous turn's target is no longer meaningful.
            HideTargetPanel();
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
        private void OnAttackPressed()
        {
            if (actionController != null) actionController.BeginAttack();
            else Debug.LogWarning($"{nameof(CombatHUD)}: no {nameof(actionController)} assigned.", this);
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
