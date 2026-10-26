using UnityEngine;
using UnityEngine.UI;
using Tactica.Grid;

namespace Tactica.UI
{
    // Tactica.Camera is a sibling namespace, so an unqualified "Camera" inside Tactica.* resolves
    // to that namespace instead of UnityEngine.Camera (CS0118). The alias must sit inside the
    // namespace block to win that lookup.
    using Camera = UnityEngine.Camera;

    // World-space HP/MP bars hovering above a unit, built entirely in code.
    //
    // Lives ON the GridUnit GameObject and creates its own child Canvas. Attached to every unit
    // automatically through GridUnit.Spawned (see AttachToSpawnedUnits), and redrawn from
    // GridUnit.StatsChanged - the unit never references this type, which keeps Grid free of UI. Nothing is authored per unit, which is the point: units get spawned at runtime for
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
        [Tooltip("HP fill colour for Player-faction units.")]
        [SerializeField] private Color allyColor = new Color(0.25f, 0.85f, 0.3f);

        [Tooltip("HP fill colour for Enemy-faction units.")]
        [SerializeField] private Color enemyColor = new Color(0.9f, 0.25f, 0.25f);

        [Tooltip("HP fill colour for Neutral units. Amber reads as neither side at a glance, " +
                 "which is the point.")]
        [SerializeField] private Color neutralColor = new Color(0.95f, 0.75f, 0.2f);

        [Tooltip("MP fill colour. Not faction-dependent.")]
        [SerializeField] private Color mpColor = new Color(0.3f, 0.5f, 0.95f);

        [Tooltip("Colour behind both fills.")]
        [SerializeField] private Color backgroundColor = new Color(0.08f, 0.08f, 0.1f, 0.85f);

        [Header("Turn Indicator")]
        [Tooltip("Fill of the triangle above the bars on whoever holds the turn.")]
        [SerializeField] private Color turnIndicatorColor = new Color(0.2f, 0.9f, 0.85f);

        [SerializeField] private Color turnIndicatorOutlineColor = Color.black;

        [Tooltip("Triangle size in canvas units (the bars are 100 wide).")]
        [SerializeField] private Vector2 turnIndicatorSize = new Vector2(33f, 27f);

        [Tooltip("Outline thickness in canvas units, measured perpendicular to each edge.")]
        [Min(0f)]
        [SerializeField] private float turnIndicatorOutline = 3.75f;

        [Tooltip("Gap between the HP bar's top and the triangle's tip.")]
        [SerializeField] private float turnIndicatorGap = 3f;

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

        // Active statuses as short tags above the bars ("BRACED  Stim 2"). Empty when none.
        private TMPro.TextMeshProUGUI statusLabel;
        private Camera billboardCamera;
        private bool built;

        // Parent of the outline and fill triangles. Inactive unless this unit holds the turn.
        private GameObject turnIndicator;

        // One sprite shared by every bar in the scene. Building a texture per unit would allocate
        // a texture and a sprite per spawn for what is, in every case, the same white pixel.
        private static Sprite solidSprite;

        // Same idea for the turn indicator: one white, tip-down triangle, tinted per Image.
        private static Sprite triangleSprite;
        private const int TriangleTextureSize = 128;

        // BeforeSceneLoad runs after GridUnit resets its statics (SubsystemRegistration) and before
        // any scene object's Awake, so authored units get a bar exactly like spawned ones. The
        // remove-then-add keeps it to one subscription if this ever runs twice.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AttachToSpawnedUnits()
        {
            GridUnit.Spawned -= AttachTo;
            GridUnit.Spawned += AttachTo;
        }

        // A hand-made bar already on the unit is kept rather than doubled up.
        private static void AttachTo(GridUnit unit)
        {
            if (unit.GetComponent<UnitStatusBar>() == null)
            {
                unit.gameObject.AddComponent<UnitStatusBar>();
            }
        }

        // Clearance above the tallest point of the unit's model, so the bars sit just over a head
        // whatever the model's size - a scaled-up boss included.
        private const float HeadClearance = 0.35f;

        private void Awake()
        {
            FitOffsetToModel();
            EnsureBuilt();
            billboardCamera = Camera.main;
        }

        // Raises the bar to clear the unit's renderers if they reach higher than the authored
        // offset. Never lowers it, so the capsule keeps the placement it always had. Runs in
        // Awake, when MissionRunner has already parented the model under the unit.
        private void FitOffsetToModel()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);

            if (renderers.Length == 0)
            {
                return;
            }

            float top = float.MinValue;

            foreach (Renderer meshRenderer in renderers)
            {
                top = Mathf.Max(top, meshRenderer.bounds.max.y);
            }

            float height = top - transform.position.y;
            offset.y = Mathf.Max(offset.y, height + HeadClearance);
        }

        // Draws immediately on subscribing, so a bar enabled after the unit's first StatsChanged
        // (a hand-made one whose Awake ran late, or a re-enabled one) is not blank until the next
        // hit.
        private void OnEnable()
        {
            if (ResolveOwner() != null)
            {
                owningUnit.StatsChanged += Redraw;
                Redraw();
            }
        }

        private void OnDisable()
        {
            if (owningUnit != null)
            {
                owningUnit.StatsChanged -= Redraw;
            }
        }

        // Idempotent: Awake calls it, and so does Redraw, because the unit can raise StatsChanged
        // before this component's Awake when the bar was authored rather than attached.
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

        // Re-reads the owning unit. MP keeps its own colour; only HP carries faction.
        private void Redraw()
        {
            if (owningUnit == null)
            {
                return;
            }

            EnsureBuilt();

            SetFill(hpFill, owningUnit.CurrentStats.HP, owningUnit.EffectiveStats.MaxHP);
            SetFill(mpFill, owningUnit.CurrentStats.MP, owningUnit.EffectiveStats.MaxMP);

            if (hpFill != null)
            {
                hpFill.color = ColorFor(owningUnit.CurrentFaction);
            }

            if (statusLabel != null)
            {
                statusLabel.text = owningUnit.DescribeStatuses();
            }
        }

        // Shows or hides the active-turn triangle. Driven by CombatHUD, which already follows
        // OnActiveUnitChanged - the bar itself knows nothing about turns.
        public void SetTurnIndicator(bool visible)
        {
            EnsureBuilt();

            if (turnIndicator != null)
            {
                turnIndicator.SetActive(visible);
            }
        }

        // Switch rather than a ternary now that there are three sides. A default arm would silently
        // paint a future faction ally-green, so every member is listed and a new one is a compile
        // warning waiting to be noticed rather than a colour bug nobody spots.
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

            float hpTop = halfSpacing + BarHeight;
            CreateTurnIndicator(canvasRect, hpTop + turnIndicatorGap);
            CreateStatusLabel(canvasRect, -halfSpacing - BarHeight - 2f);
        }

        // A tip-down triangle whose tip sits tipY above the canvas centre, pointing at the unit.
        //
        // Two stacked Images of the same triangle: a black one at full size, and a turquoise one
        // shrunk about the triangle's INCENTRE. Scaling about the incentre moves every edge inward
        // by the same perpendicular distance, so the outline is equally thick on all three sides;
        // scaling about the rect centre would leave the slanted edges thinner than the top.
        // Outside the canvas rect on purpose - world-space canvases do not clip their children.
        private void CreateTurnIndicator(RectTransform parent, float tipY)
        {
            float width = turnIndicatorSize.x;
            float height = turnIndicatorSize.y;

            GameObject root = new GameObject("TurnIndicator", typeof(RectTransform));
            RectTransform rootRect = (RectTransform)root.transform;
            rootRect.SetParent(parent, false);
            rootRect.sizeDelta = turnIndicatorSize;
            rootRect.anchoredPosition = new Vector2(0f, tipY + height * 0.5f);

            CreateTriangle(rootRect, "Outline", turnIndicatorSize, Vector2.zero, turnIndicatorOutlineColor);

            // Vertices relative to the rect centre: top-left, top-right, tip.
            Vector2 a = new Vector2(-width * 0.5f, height * 0.5f);
            Vector2 b = new Vector2(width * 0.5f, height * 0.5f);
            Vector2 c = new Vector2(0f, -height * 0.5f);

            // Incentre = vertices weighted by the length of the side opposite each.
            float sideA = Vector2.Distance(b, c);
            float sideB = Vector2.Distance(c, a);
            float sideC = Vector2.Distance(a, b);
            float perimeter = sideA + sideB + sideC;
            Vector2 incentre = (sideA * a + sideB * b + sideC * c) / perimeter;
            float inradius = width * height / perimeter;

            float scale = Mathf.Clamp01((inradius - turnIndicatorOutline) / inradius);

            // A point p maps to incentre + scale * (p - incentre); the rect centre is the origin.
            CreateTriangle(rootRect, "Fill", turnIndicatorSize * scale, incentre * (1f - scale), turnIndicatorColor);

            turnIndicator = root;
            turnIndicator.SetActive(false);
        }

        // Under the MP bar, so it never collides with the turn triangle above the HP bar.
        private void CreateStatusLabel(RectTransform parent, float topY)
        {
            GameObject labelObject = new GameObject("Statuses", typeof(RectTransform));
            RectTransform rect = (RectTransform)labelObject.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(CanvasWidth * 2f, 12f);
            rect.anchoredPosition = new Vector2(0f, topY - 6f);

            statusLabel = labelObject.AddComponent<TMPro.TextMeshProUGUI>();
            statusLabel.fontSize = 9f;
            statusLabel.alignment = TMPro.TextAlignmentOptions.Center;
            statusLabel.color = new Color(0.4f, 1f, 1f);
            statusLabel.raycastTarget = false;
            statusLabel.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            statusLabel.text = string.Empty;
        }

        private static void CreateTriangle(RectTransform parent, string name, Vector2 size, Vector2 position, Color color)
        {
            GameObject triangleObject = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)triangleObject.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            Image image = triangleObject.AddComponent<Image>();
            image.sprite = GetTriangleSprite();
            image.color = color;
            image.raycastTarget = false;
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

        // A white triangle filling the texture: top edge along the top, tip at the bottom centre.
        // Edges are antialiased over about one pixel, so it stays smooth when stretched to any
        // rect - the Image is what decides its final proportions.
        private static Sprite GetTriangleSprite()
        {
            if (triangleSprite != null)
            {
                return triangleSprite;
            }

            int size = TriangleTextureSize;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            Color32[] pixels = new Color32[size * size];

            // Side edges are |x| = y / 2 in a unit square centred on x; dividing by the line's
            // normal length turns the test into a distance, in pixels, for the antialiasing.
            float sideNormal = Mathf.Sqrt(1f + 0.25f);

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float u = (px + 0.5f) / size - 0.5f;
                    float v = (py + 0.5f) / size;

                    float toTop = (1f - v) * size;
                    float toSide = (0.5f * v - Mathf.Abs(u)) / sideNormal * size;

                    float alpha = Mathf.Clamp01(Mathf.Min(toTop, toSide) + 0.5f);
                    pixels[py * size + px] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            triangleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
            triangleSprite.hideFlags = HideFlags.HideAndDontSave;

            return triangleSprite;
        }
    }
}
