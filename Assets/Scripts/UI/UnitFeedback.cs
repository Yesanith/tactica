using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Tactica.Combat;
using Tactica.Grid;

namespace Tactica.UI
{
    // Everything a unit shows about what just happened to it, beyond the bars: a faction ring on
    // the ground, numbers that float up when HP changes, MISS / RESISTED / HACKED / KO callouts,
    // a hop between tiles instead of a snap, a shake when hit, and a fall when knocked out.
    // Plus a short synthesised sound for each, so the board is readable with eyes elsewhere.
    //
    // Purely cosmetic and self-attaching (like UnitStatusBar, through GridUnit.Spawned). Rules never
    // wait on it: the logical position changes at once and this only eases the transform after it.
    public class UnitFeedback : MonoBehaviour
    {
        // Faction ring colours. Guests get their own so "ours but not ours to command" reads on
        // the board; a hacked unit keeps a magenta ring for the rest of the fight.
        private static readonly Color PlayerRing = new Color(0.25f, 0.95f, 1f);
        private static readonly Color GuestRing = new Color(0.55f, 1f, 0.55f);
        private static readonly Color EnemyRing = new Color(1f, 0.25f, 0.4f);
        private static readonly Color NeutralRing = new Color(1f, 0.85f, 0.25f);
        private static readonly Color HackedRing = new Color(1f, 0.3f, 1f);

        private const float RingRadius = 0.42f;
        private const float RingWidth = 0.06f;
        private const int RingSegments = 40;

        private const float SecondsPerTile = 0.1f;
        private const float MinMoveTime = 0.15f;
        private const float MaxMoveTime = 0.7f;
        private const float HopHeight = 0.3f;

        // A jump longer than this is a teleport (spawn, rebuild), not a move - no tween.
        private const float MaxTweenDistance = 15f;

        private const float ShakeTime = 0.25f;
        private const float ShakeAmount = 0.08f;
        private const float FallAngle = 80f;
        private const float FallSpeed = 240f;

        private GridUnit unit;
        private LineRenderer ring;
        private Faction startingFaction;
        private bool hacked;

        private int lastHP;
        private bool wasKnockedOut;
        private string lastStatuses;

        // The tween. target is where game logic last put the transform; shown is where this
        // component put it last frame, so a difference means the unit was moved in between.
        private bool positioned;
        private Vector3 target;
        private Vector3 shown;
        private Vector3 moveFrom;
        private float moveTime;
        private float moveElapsed;
        private float shakeElapsed = ShakeTime;
        private float fall;
        private bool started;
        private bool posed;

        private static Material ringMaterial;
        private static ActionResolver subscribedResolver;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AttachToSpawnedUnits()
        {
            GridUnit.Spawned -= AttachTo;
            GridUnit.Spawned += AttachTo;
            subscribedResolver = null;
        }

        private static void AttachTo(GridUnit spawned)
        {
            if (spawned.GetComponent<UnitFeedback>() == null)
            {
                spawned.gameObject.AddComponent<UnitFeedback>();
            }

            SubscribeToResolver();
        }

        // Misses and failed hacks change no stat, so only the resolver knows about them.
        private static void SubscribeToResolver()
        {
            CombatManager combat = FindAnyObjectByType<CombatManager>();

            if (combat == null || combat.Resolver == subscribedResolver)
            {
                return;
            }

            if (subscribedResolver != null)
            {
                subscribedResolver.OnActionResolved -= HandleActionResolved;
            }

            subscribedResolver = combat.Resolver;
            subscribedResolver.OnActionResolved += HandleActionResolved;
        }

        private static void HandleActionResolved(ActionOutcome outcome)
        {
            if (outcome.Effective || outcome.Target == null || outcome.Ability == null)
            {
                return;
            }

            // Only harmful actions can "miss"; a heal on a full-HP ally just did nothing.
            if (outcome.Ability.EffectType == Tactica.Stats.AbilityEffectType.Override)
            {
                Spawn(outcome.Target, "RESISTED", new Color(0.8f, 0.6f, 1f));
                FeedbackAudio.Play(FeedbackAudio.Cue.Miss);
            }
            else if (outcome.Ability.IsHarmful)
            {
                Spawn(outcome.Target, "MISS", Color.white);
                FeedbackAudio.Play(FeedbackAudio.Cue.Miss);
            }
        }

        private void Awake()
        {
            unit = GetComponent<GridUnit>();
            unit.StatsChanged += HandleStatsChanged;
            BuildRing();
        }

        private void Start()
        {
            // Stats exist from Awake on; read them once everything is set up.
            startingFaction = unit.CurrentFaction;
            lastHP = unit.CurrentStats.HP;
            wasKnockedOut = unit.IsKnockedOut;
            lastStatuses = unit.DescribeStatuses();
            started = true;
            RefreshRing();
        }

        private void OnEnable()
        {
            if (ring != null)
            {
                ring.gameObject.SetActive(true);
            }
        }

        private void OnDisable()
        {
            // Flatlined units are deactivated; their ring goes with them.
            if (ring != null)
            {
                ring.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            if (unit != null)
            {
                unit.StatsChanged -= HandleStatsChanged;
            }

            if (ring != null)
            {
                Destroy(ring.gameObject);
            }
        }

        private void HandleStatsChanged()
        {
            // Spawning fills the pools before Start; that is not "healed for 16".
            if (unit == null || !started)
            {
                return;
            }

            int hp = unit.CurrentStats.HP;
            int delta = hp - lastHP;
            lastHP = hp;

            if (unit.IsKnockedOut && !wasKnockedOut)
            {
                Spawn(unit, delta < 0 ? $"{delta}  KO" : "KO", new Color(1f, 0.3f, 0.3f), 1.3f);
                FeedbackAudio.Play(FeedbackAudio.Cue.KnockOut);
                shakeElapsed = 0f;
            }
            else if (!unit.IsKnockedOut && wasKnockedOut)
            {
                Spawn(unit, "REBOOT", new Color(0.4f, 1f, 0.6f), 1.2f);
                FeedbackAudio.Play(FeedbackAudio.Cue.Heal);
            }
            else if (delta < 0)
            {
                Spawn(unit, delta.ToString(), new Color(1f, 0.45f, 0.35f));
                FeedbackAudio.Play(FeedbackAudio.Cue.Hit);
                shakeElapsed = 0f;
            }
            else if (delta > 0)
            {
                Spawn(unit, "+" + delta, new Color(0.45f, 1f, 0.55f));
                FeedbackAudio.Play(FeedbackAudio.Cue.Heal);
            }

            wasKnockedOut = unit.IsKnockedOut;

            // A new status or stance tag: call out what changed, not the whole list.
            string statuses = unit.DescribeStatuses();

            if (statuses != lastStatuses)
            {
                string added = NewTag(lastStatuses, statuses);

                if (added != null)
                {
                    Spawn(unit, added, new Color(0.4f, 1f, 1f), 0.9f, 0.5f);
                    FeedbackAudio.Play(FeedbackAudio.Cue.Status);
                }

                lastStatuses = statuses;
            }

            if (!hacked && !unit.IsGuest && unit.CurrentFaction != startingFaction)
            {
                hacked = true;
                Spawn(unit, "HACKED", HackedRing, 1.3f);
                FeedbackAudio.Play(FeedbackAudio.Cue.Hack);
            }

            RefreshRing();
        }

        // The first tag in the new description that the old one lacked, minus its turn count.
        private static string NewTag(string before, string after)
        {
            HashSet<string> old = new HashSet<string>(Names(before));

            foreach (string name in Names(after))
            {
                if (!old.Contains(name))
                {
                    return name;
                }
            }

            return null;
        }

        private static IEnumerable<string> Names(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                yield break;
            }

            foreach (string tag in description.Split(new[] { "  " }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                int space = tag.LastIndexOf(' ');
                yield return space > 0 && int.TryParse(tag.Substring(space + 1), out _) ? tag.Substring(0, space) : tag;
            }
        }

        private void LateUpdate()
        {
            if (unit == null)
            {
                return;
            }

            Vector3 current = transform.position;

            if (!positioned)
            {
                positioned = true;
                target = shown = current;
            }
            else if ((current - shown).sqrMagnitude > 0.0001f)
            {
                // Something moved the unit since last frame: that is the new destination.
                target = current;
                float distance = Vector3.Distance(shown, target);

                if (distance > MaxTweenDistance)
                {
                    shown = target;
                    moveElapsed = moveTime = 0f;
                }
                else
                {
                    moveFrom = shown;
                    Vector3 flat = target - moveFrom;
                    flat.y = 0f;
                    moveTime = Mathf.Clamp(flat.magnitude * SecondsPerTile, MinMoveTime, MaxMoveTime);
                    moveElapsed = 0f;
                }
            }

            Vector3 position = target;

            if (moveElapsed < moveTime)
            {
                moveElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(moveElapsed / moveTime);
                position = Vector3.Lerp(moveFrom, target, Mathf.SmoothStep(0f, 1f, t));
                float climb = Mathf.Max(0f, target.y - moveFrom.y);
                position.y += Mathf.Sin(t * Mathf.PI) * (HopHeight + climb * 0.5f);
            }

            if (shakeElapsed < ShakeTime)
            {
                shakeElapsed += Time.deltaTime;
                float k = 1f - Mathf.Clamp01(shakeElapsed / ShakeTime);
                position += transform.right * Mathf.Sin(shakeElapsed * 70f) * ShakeAmount * k;
            }

            // The KO fall: tip over sideways about the feet, and get back up on a reboot.
            float wantFall = unit.IsKnockedOut ? FallAngle : 0f;
            fall = Mathf.MoveTowards(fall, wantFall, FallSpeed * Time.deltaTime);
            Quaternion upright = Quaternion.Euler(0f, FacingUtility.ToYaw(unit.CurrentFacing), 0f);

            if (fall > 0.01f)
            {
                Quaternion tipped = upright * Quaternion.Euler(0f, 0f, fall);
                Vector3 feet = position - Vector3.up * unit.PivotHeight;
                position = feet + tipped * (Vector3.up * unit.PivotHeight);
                transform.rotation = tipped;
                posed = true;
            }
            else if (posed)
            {
                // Back on its feet: hand the rotation back to GridUnit's facing.
                transform.rotation = upright;
                posed = false;
            }

            transform.position = position;
            shown = position;

            if (ring != null)
            {
                Vector3 feetOnTile = target - Vector3.up * (unit.PivotHeight - 0.02f);
                Vector3 ringAt = new Vector3(position.x, feetOnTile.y, position.z);
                ring.transform.position = ringAt;
            }
        }

        private void RefreshRing()
        {
            if (ring == null || unit == null)
            {
                return;
            }

            Color color = hacked ? HackedRing
                : unit.IsGuest ? GuestRing
                : unit.CurrentFaction switch
                {
                    Faction.Player => PlayerRing,
                    Faction.Enemy => EnemyRing,
                    _ => NeutralRing,
                };

            if (unit.IsKnockedOut)
            {
                color.a = 0.35f;
            }

            ring.startColor = ring.endColor = color;
        }

        private void BuildRing()
        {
            if (ringMaterial == null)
            {
                ringMaterial = new Material(Shader.Find("Sprites/Default"));
            }

            GameObject ringObject = new GameObject($"{name} Ring");
            ring = ringObject.AddComponent<LineRenderer>();
            ring.sharedMaterial = ringMaterial;
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.widthMultiplier = RingWidth;
            ring.positionCount = RingSegments;
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.receiveShadows = false;

            for (int i = 0; i < RingSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / RingSegments;
                ring.SetPosition(i, new Vector3(Mathf.Cos(angle) * RingRadius, 0f, Mathf.Sin(angle) * RingRadius));
            }
        }

        // A callout above a unit's head that rises and fades.
        private static void Spawn(GridUnit over, string text, Color color, float scale = 1f, float extraHeight = 0f)
        {
            if (over == null || !over.isActiveAndEnabled)
            {
                return;
            }

            Bounds bounds = new Bounds(over.transform.position, Vector3.zero);

            foreach (Renderer renderer in over.GetComponentsInChildren<Renderer>())
            {
                if (!(renderer is LineRenderer))
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            Vector3 at = new Vector3(over.transform.position.x, bounds.max.y + 0.6f + extraHeight, over.transform.position.z);
            FloatingText.Create(at, text, color, scale);
        }
    }
}
