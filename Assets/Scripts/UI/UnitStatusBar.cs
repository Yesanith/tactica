using UnityEngine;
using UnityEngine.UI;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.UI
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup.
    using Camera = UnityEngine.Camera;

    // World-space HP/MP bars hovering above a unit, built entirely in code.
    //
    // Lives ON the GridUnit GameObject (GridUnit adds it in Awake) and creates its own child
    // Canvas. Nothing is authored per unit, which is the point: units get spawned at runtime for
    // recruitment and mission rosters, and a hand-built hierarchy cannot be pre-made for a unit
    // that does not exist yet.
    //
    // NOTE ON transform: this component's transform is the UNIT's, not the bar's. The billboard
    // must move barRoot instead - rotating "transform" here would spin the unit itself.
    [DisallowMultipleComponent]
    public class UnitStatusBar : MonoBehaviour
    {
        [Tooltip("World-space offset from the unit's pivot. Y should clear the unit's head.")]
        [SerializeField] private Vector3 offset = new Vector3(0f, 2.2f, 0f);

        [Header("Colours")]
        [Tooltip("HP fill colour for player-controlled units.")]
        [SerializeField] private Color allyColor = new Color(0.25f, 0.85f, 0.3f);

        [Tooltip("HP fill colour for everyone else.")]
        [SerializeField] private Color enemyColor = new Color(0.9f, 0.25f, 0.25f);

        [Tooltip("MP fill colour. Not faction-dependent.")]
        [SerializeField] private Color mpColor = new Color(0.3f, 0.5f, 0.95f);

        [Tooltip("Colour behind both fills.")]
        [SerializeField] private Color backgroundColor = new Color(0.08f, 0.08f, 0.1f, 0.85f);

        [Tooltip("Unit this bar belongs to. Defaults to the GridUnit on this GameObject.")]
        [SerializeField] private GridUnit owningUnit;

        // Canvas local units. Paired with CanvasLocalScale below, these reproduce the hand-built
        // sizing exactly: 100 * 0.01 = 1 world unit wide, one tile.
        private const float CanvasWidth = 100f;
        private const float CanvasHeight = 20f;
        private const float CanvasLocalScale = 0.01f;
        private const float BarHeight = 8f;
        private const float BarSpacing = 1f;

        private Transform barRoot;
        private Image hpFill;
        private Image mpFill;
        private Camera billboardCamera;
        private bool built;

        // One sprite shared by every bar in the scene. Building a texture per unit would allocate
        // a texture and a sprite per spawn for what is, in every case, the same white pixel.
        private static Sprite solidSprite;

        private void Awake()
        {
            EnsureBuilt();
            billboardCamera = Camera.main;
        }

        // Idempotent: Awake calls it, and so does UpdateBars, because GridUnit can drive the bars
        // from its own Awake and component Awake order is not guaranteed.
        public void EnsureBuilt()
        {
            if (built)
            {
                return;
            }

            built = true;
            BuildHierarchy();
        }

        // LateUpdate, not Update: TacticsCameraController moves the camera in Update, so a
        // billboard computed in Update would use the camera's pre-move orientation - a one-frame
        // lag that reads as the bars swimming while panning or snap-rotating.
        private void LateUpdate()
        {
            if (barRoot == null)
            {
                return;
            }

            // Positioning happens BEFORE the camera lookup. These are independent jobs, and
            // bailing out early on a missing camera used to skip placement too - so one missing
            // MainCamera tag would leave every bar buried inside its unit rather than merely
            // un-billboarded.
            //
            // barRoot, never transform. transform is the unit.
            barRoot.position = transform.position + offset;

            if (billboardCamera == null)
            {
                billboardCamera = Camera.main;

                if (billboardCamera == null)
                {
                    return;
                }
            }

            // Copying the camera's rotation aligns the bar with the screen plane. LookAt would aim
            // each bar at the camera's position instead, tilting bars near the screen edges.
            barRoot.rotation = billboardCamera.transform.rotation;
        }

        // Drives both fills. MP keeps its own colour; only HP carries faction.
        public void UpdateBars(CurrentStats current, CharacterStats max)
        {
            // GridUnit can call this from its own Awake, and component Awake order is not
            // guaranteed, so the hierarchy may not exist yet. Building on demand means the first
            // update draws correctly instead of being silently dropped.
            EnsureBuilt();

            SetFill(hpFill, current.HP, max.MaxHP);
            SetFill(mpFill, current.MP, max.MaxMP);

            if (hpFill != null && ResolveOwner() != null)
            {
                hpFill.color = owningUnit.IsPlayerControlled ? allyColor : enemyColor;
            }
        }

        private GridUnit ResolveOwner()
        {
            if (owningUnit == null)
            {
                owningUnit = GetComponentInParent<GridUnit>();
            }

            return owningUnit;
        }

        // Guards the divide: an unconfigured job has MaxHP 0, and 0/0 is NaN, which Unity renders
        // as a bar stuck at full rather than empty - a misleading failure instead of an obvious one.
        private static void SetFill(Image fill, int current, int max)
        {
            if (fill == null)
            {
                return;
            }

            fill.fillAmount = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;
        }

        private void BuildHierarchy()
        {
            GameObject canvasObject = new GameObject("StatusBarCanvas");

            // Canvas FIRST, before caching any transform reference. Canvas requires a
            // RectTransform, so adding it replaces the GameObject's plain Transform - and a
            // reference captured beforehand can be left pointing at the replaced component. That
            // stale reference then trips the null guard in LateUpdate every frame, so the bar
            // never gets positioned while still looking correctly built in the Hierarchy.
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;

            // No GraphicRaycaster on purpose. These bars are output only, and a raycaster would
            // put an invisible UI surface in front of the board for anything using the EventSystem.
            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);

            barRoot = canvasRect;
            barRoot.SetParent(transform, false);
            barRoot.localScale = Vector3.one * CanvasLocalScale;

            // Place it correctly immediately rather than waiting for the first LateUpdate, so the
            // bar is never visibly sitting inside the unit on the frame it spawns.
            barRoot.localPosition = offset;

            float halfSpacing = BarSpacing * 0.5f;
            hpFill = CreateBar(canvasRect, "HP", new Vector2(0f, halfSpacing + BarHeight * 0.5f), allyColor);
            mpFill = CreateBar(canvasRect, "MP", new Vector2(0f, -halfSpacing - BarHeight * 0.5f), mpColor);
        }

        // One background plus one fill. The fill is a child stretched over the background, so
        // fillAmount sweeps across exactly the background's extent regardless of size changes.
        private Image CreateBar(RectTransform parent, string label, Vector2 anchoredPosition, Color fillColor)
        {
            GameObject backgroundObject = new GameObject(label + "_Background");
            RectTransform backgroundRect = backgroundObject.AddComponent<RectTransform>();
            backgroundRect.SetParent(parent, false);
            backgroundRect.sizeDelta = new Vector2(CanvasWidth, BarHeight);
            backgroundRect.anchoredPosition = anchoredPosition;

            Image backgroundImage = backgroundObject.AddComponent<Image>();
            backgroundImage.sprite = GetSolidSprite();
            backgroundImage.color = backgroundColor;
            backgroundImage.raycastTarget = false;

            GameObject fillObject = new GameObject(label + "_Fill");
            RectTransform fillRect = fillObject.AddComponent<RectTransform>();
            fillRect.SetParent(backgroundRect, false);

            // Stretch to the parent on both axes, zero inset.
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            Image fillImage = fillObject.AddComponent<Image>();
            fillImage.sprite = GetSolidSprite();
            fillImage.color = fillColor;
            fillImage.raycastTarget = false;

            // The editor equivalent of Image Type = Filled, Fill Method = Horizontal,
            // Fill Origin = Left. fillOrigin is an int, indexed into OriginHorizontal.
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImage.fillAmount = 1f;

            return fillImage;
        }

        // A 1x1 white pixel, tinted per-Image via Image.color. Created once and cached statically.
        //
        // HideAndDontSave keeps the generated texture and sprite out of the scene file and stops
        // Unity trying to serialise runtime-created assets. The static resets on domain reload in
        // the editor, which just rebuilds one pixel.
        private static Sprite GetSolidSprite()
        {
            if (solidSprite != null)
            {
                return solidSprite;
            }

            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            texture.SetPixel(0, 0, Color.white);
            texture.Apply();

            solidSprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            solidSprite.hideFlags = HideFlags.HideAndDontSave;

            return solidSprite;
        }
    }
}
