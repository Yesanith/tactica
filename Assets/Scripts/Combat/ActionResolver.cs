using System;
using System.Collections.Generic;
using UnityEngine;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.Combat
{
    // Rules for using an ability: who can be hit, from how far, for how much, and what follows.
    //
    // Plain C# class, not a MonoBehaviour - it holds no scene references. What it needs to know
    // about the board (who stands on a tile, how high a tile is) arrives as two delegates that
    // CombatManager fills in, so a test can construct one with a fake board and no scene.
    public partial class ActionResolver
    {
        // Raised after an action has finished mutating stats. An event rather than a direct call
        // into CombatHUD on purpose: this class is a plain C# object constructible in a test
        // without a scene, and holding a reference to a MonoBehaviour would end that. Listeners
        // decide what "something changed" means to them - HUD refresh today, floating combat text
        // and audio later - without this class learning about any of them.
        //
        // Coarse by design: it says stats changed, not which. Panels re-read whatever they display,
        // which is cheap and cannot go out of sync with a more precise payload.
        public event Action OnStatsChanged;

        // Raised once per target of a carried-out action (and once for a counter), after
        // OnStatsChanged, with whether it was effective. Progression listens here (MissionRunner
        // turns it into JP and XP); CombatManager resets a freshly hacked unit's CT.
        public event Action<ActionOutcome> OnActionResolved;

        // A charged ability was chosen: MP spent, the aim point fixed, nothing applied yet.
        // CombatManager keeps the clock and calls ResolveCharge when it goes off.
        public event Action<GridUnit, Vector2Int, AbilityDefinition> OnChargeStarted;

        // Override can never be a sure thing nor a lost cause: a 100% hack would make every enemy a
        // future recruit, and a 0% one is a button that only wastes MP.
        public const int MinOverrideChance = 5;
        public const int MaxOverrideChance = 95;

        // GAME_DESIGN.md's hack brakes: Tech counts three times, every level the target has over
        // the hacker costs 4%, and the target's own resistance comes straight off.
        public const int OverrideTechWeight = 3;
        public const int OverrideLevelPenalty = 4;

        // Ranged reach from high ground: +1 per this many levels above the target, -1 per this many
        // below. FFT's longbow rule (5 + height difference / 2), applied to every ranged ability.
        public const int HeightPerRangeStep = 2;

        // Seedable so a test can pin the rolls. Owned here rather than using UnityEngine.Random,
        // which would tie the resolver to engine state it otherwise never needs.
        private readonly System.Random random;

        public ActionResolver(int? seed = null)
        {
            random = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
        }

        // For changes that happen outside an action - a status ticking at turn start - so the HUD
        // refreshes the same way it does after an action.
        public void NotifyStatsChanged()
        {
            OnStatsChanged?.Invoke();
        }

        // Board lookups, provided by CombatManager. Without them area abilities can only hit the
        // unit at their centre and height has no effect - still correct, just flatter.
        public Func<Vector2Int, GridUnit> UnitAt { get; set; }
        public Func<Vector2Int, int?> HeightAt { get; set; }

        // ---------------------------------------------------------------------
        // Reach
        // ---------------------------------------------------------------------

        // An ability's base reach: its own Range, or the weapon's if it uses the weapon and that is
        // longer - a rifle's basic attack reaches further than a blade's.
        public int GetBaseRange(GridUnit user, AbilityDefinition ability)
        {
            if (ability.UsesWeapon && user != null && user.Weapon != null)
            {
                return Mathf.Max(ability.Range, user.Weapon.Range);
            }

            return ability.Range;
        }

        public int GetArea(GridUnit user, AbilityDefinition ability)
        {
            if (ability.UsesWeapon && user != null && user.Weapon != null)
            {
                return Mathf.Max(ability.AreaOfEffect, user.Weapon.AreaOfEffect);
            }

            return ability.AreaOfEffect;
        }

        // Reach toward one tile, height included. Only ranged abilities (base reach 2+) gain or
        // lose from height, and never below 1 - melee and self abilities are unaffected.
        public int GetRange(GridUnit user, AbilityDefinition ability, Vector2Int center)
        {
            int range = GetBaseRange(user, ability);

            if (range < 2 || HeightAt == null)
            {
                return range;
            }

            int? from = HeightAt(user.GridCoords);
            int? to = HeightAt(center);

            if (!from.HasValue || !to.HasValue)
            {
                return range;
            }

            int difference = from.Value - to.Value;
            int step = difference >= 0 ? difference / HeightPerRangeStep : -((-difference) / HeightPerRangeStep);
            return Mathf.Max(1, range + step);
        }

        // Distance is Manhattan (orthogonal) steps, ignoring obstacles - the same metric movement
        // uses. See GridManager.GetGridDistance for why the move-range flood fill is the wrong tool.
        // Range 0 means self-only, since only the user is at distance 0 from itself.
        public bool IsInRange(GridUnit user, Vector2Int center, AbilityDefinition ability)
        {
            if (user == null || ability == null)
            {
                return false;
            }

            return GridManager.GetGridDistance(user.GridCoords, center) <= GetRange(user, ability, center);
        }

        // Reach as if the user stood on `from` - for the AI, weighing tiles it could move to first.
        // Height is read from `from`, so the AI values high ground the same way the rules do.
        public bool IsInRangeFrom(GridUnit user, Vector2Int from, Vector2Int center, AbilityDefinition ability)
        {
            if (user == null || ability == null)
            {
                return false;
            }

            int range = GetBaseRange(user, ability);

            if (range >= 2 && HeightAt != null)
            {
                int? fromHeight = HeightAt(from);
                int? toHeight = HeightAt(center);

                if (fromHeight.HasValue && toHeight.HasValue)
                {
                    int difference = fromHeight.Value - toHeight.Value;
                    int step = difference >= 0 ? difference / HeightPerRangeStep : -((-difference) / HeightPerRangeStep);
                    range = Mathf.Max(1, range + step);
                }
            }

            return GridManager.GetGridDistance(from, center) <= range;
        }

        public bool IsTargetInRange(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            return target != null && IsInRange(user, target.GridCoords, ability);
        }

        // ---------------------------------------------------------------------
        // Who
        // ---------------------------------------------------------------------

        // Checks the ability's targeting flags against the user/target relationship.
        //
        // Three cases, in order: the unit itself, someone on its own side, someone on another.
        // Self is tested first because a unit shares its own faction, so without that check
        // TargetsAllies alone would make every ability self-targetable.
        //
        // NEUTRAL IS HOSTILE TO EVERYONE, on purpose. "Different faction" is the only rule, so a
        // Neutral is a legal target for both sides and targets both. Neutral is for genuine third
        // parties - rogue drones, a feral AI - which is exactly that behaviour.
        //
        // Civilians and escorts are NOT Neutral: they are guests (GridUnit.IsGuest), on the Player
        // side and driven by the AI, as in FFT. That split is why no faction relationship table is
        // needed - "who may hit the civilian" is answered by the civilian being on your side.
        public bool IsValidTarget(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return false;
            }

            // Flatlined units are off the board. Otherwise the knocked out are valid ONLY for
            // Revive, and Revive is valid only for them - this is the single place that rule lives,
            // so the player's highlight, the AI's search and execution all agree.
            if (target.IsFlatlined)
            {
                return false;
            }

            bool wantsDowned = ability.EffectType == AbilityEffectType.Revive;

            if (target.IsKnockedOut != wantsDowned)
            {
                return false;
            }

            if (user == target)
            {
                return ability.TargetsSelf;
            }

            if (user.CurrentFaction == target.CurrentFaction)
            {
                return ability.TargetsAllies;
            }

            return ability.TargetsEnemies;
        }

        // Everyone the ability would touch if aimed at `center`.
        //
        // Single-target: whoever stands on the centre, if it is a valid target. Area: every unit
        // within the blast radius (Manhattan), with FRIENDLY FIRE for harmful abilities - a grenade
        // does not check uniforms, GAME_DESIGN.md's "AoE dostlara vurur". Helpful area abilities
        // (heals, buffs) only touch units they may target. The caster is spared its own harmful
        // blast unless the ability targets self.
        public List<GridUnit> GetAffectedUnits(GridUnit user, Vector2Int center, AbilityDefinition ability)
        {
            List<GridUnit> affected = new List<GridUnit>();

            if (user == null || ability == null)
            {
                return affected;
            }

            int area = GetArea(user, ability);

            if (area <= 0 || UnitAt == null)
            {
                GridUnit single = UnitAt != null ? UnitAt(center) : null;

                if (single != null && IsValidTarget(user, single, ability))
                {
                    affected.Add(single);
                }

                return affected;
            }

            foreach (Vector2Int tile in GetAreaTiles(center, area))
            {
                GridUnit unit = UnitAt(tile);

                if (unit == null || unit.IsFlatlined)
                {
                    continue;
                }

                bool hit = ability.IsHarmful
                    ? !unit.IsKnockedOut && (unit != user || ability.TargetsSelf) && !Spares(user, ability, unit)
                    : IsValidTarget(user, unit, ability);

                if (hit)
                {
                    affected.Add(unit);
                }
            }

            return affected;
        }

        // Every tile within `area` of the centre (Manhattan) - the blast's footprint, units or not.
        public static List<Vector2Int> GetAreaTiles(Vector2Int center, int area)
        {
            List<Vector2Int> tiles = new List<Vector2Int>();

            for (int dx = -area; dx <= area; dx++)
            {
                for (int dy = -area; dy <= area; dy++)
                {
                    if (Mathf.Abs(dx) + Mathf.Abs(dy) <= area)
                    {
                        tiles.Add(center + new Vector2Int(dx, dy));
                    }
                }
            }

            return tiles;
        }

        // Whether a harmful blast passes over this unit: an area hack never touches the user's own
        // side (there is nothing to take over), and Friendly Fuse in the Support slot keeps the
        // user's blasts off its allies.
        private static bool Spares(GridUnit user, AbilityDefinition ability, GridUnit unit)
        {
            if (unit == user || unit.CurrentFaction != user.CurrentFaction)
            {
                return false;
            }

            return ability.EffectType == AbilityEffectType.Override || (user.Support != null && user.Support.SparesAllies);
        }

        // ---------------------------------------------------------------------
        // How much
        // ---------------------------------------------------------------------

        // Power + the scaling stat (+ weapon Power for weapon abilities). Shared by damage, healing,
        // reboots and damage/heal over time, so Tech-scaled medicine and hacks grow like blades do.
        public int GetPower(GridUnit user, AbilityDefinition ability)
        {
            int stat = ability.Scaling == StatScaling.Tech ? user.EffectiveStats.Tech : user.EffectiveStats.Strength;
            int weapon = ability.UsesWeapon && user.Weapon != null ? user.Weapon.Power : 0;
            return ability.Power + stat + weapon;
        }

        // Power + scaling stat (+ weapon) - defender's Defense, floored at 1.
        //
        // The floor matters for more than tuning. Without it a well-armoured defender produces a
        // negative result, and a negative damage value passed to ApplyDamage would heal the
        // target - an attack that repairs its victim. Flooring at 1 also keeps a chip of progress
        // in every hit, so no matchup can stall forever.
        //
        // Returns 0 for non-damage abilities rather than a nonsense number.
        public int CalculateDamage(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null || ability.EffectType != AbilityEffectType.Damage)
            {
                return 0;
            }

            return Mathf.Max(1, GetPower(user, ability) - target.EffectiveStats.Defense);
        }

        // Where the user would strike the target from, relative to the target's facing.
        public AttackSide GetAttackSide(GridUnit user, GridUnit target)
        {
            if (user == null || target == null || user == target)
            {
                return AttackSide.Front;
            }

            return FacingUtility.SideOf(user.GridCoords, target.GridCoords, target.CurrentFacing);
        }

        // Chance, in percent, that a physical harmful ability connects. FFT's facing rule,
        // simplified: Evasion in full from the front, halved from the side, nothing from behind,
        // doubled while defending. Pure Tech abilities (hacks, viruses) and helpful ones always
        // land; Override has its own roll.
        public int GetHitChance(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || target == null || ability == null)
            {
                return 0;
            }

            // A Tech ability that needs a weapon (a poisoned blade) is still a physical blow and
            // rolls against evasion; a pure hack does not.
            bool pureTech = ability.Scaling == StatScaling.Tech && ability.RequiredWeapon == WeaponType.None;

            if (!ability.IsHarmful || ability.EffectType == AbilityEffectType.Override || pureTech || user == target)
            {
                return 100;
            }

            return PhysicalHitChance(user, target);
        }

        // Evasion by facing and stance - what a blow or a shot has to beat.
        private int PhysicalHitChance(GridUnit user, GridUnit target)
        {
            int evasion = target.EffectiveStats.Evasion;

            if (target.IsDefending)
            {
                evasion *= 2;
            }

            switch (GetAttackSide(user, target))
            {
                case AttackSide.Side: evasion /= 2; break;
                case AttackSide.Back: evasion = 0; break;
            }

            return Mathf.Clamp(100 - evasion, 0, 100);
        }

        // Override's chance against one target: Power + 3 x Tech, minus 4 per level the target has
        // over the hacker, minus the target's resistance, clamped to 5-95.
        public int GetOverrideChance(GridUnit user, GridUnit target, AbilityDefinition ability)
        {
            if (user == null || ability == null)
            {
                return 0;
            }

            int chance = ability.Power + OverrideTechWeight * user.EffectiveStats.Tech;

            if (target != null)
            {
                chance -= OverrideLevelPenalty * Mathf.Max(0, target.Level - user.Level);
                chance -= target.HackResistance;

                if (ability.WoundedBonus > 0 && target.CurrentStats.HP * 2 < target.EffectiveStats.MaxHP)
                {
                    chance += ability.WoundedBonus;
                }

                if (target.IsBoss)
                {
                    chance = chance * ability.BossChancePercent / 100;
                }
            }

            return Mathf.Clamp(chance, MinOverrideChance, MaxOverrideChance);
        }

        // Whether the unit could use this ability at all right now, ignoring targets.
        public bool CanUse(GridUnit user, AbilityDefinition ability, out string reason)
        {
            reason = null;

            if (user == null || ability == null) reason = "No ability.";
            else if (!user.HasWeaponType(ability.RequiredWeapon)) reason = $"Needs a {ability.RequiredWeapon}.";
            else if (user.CurrentStats.MP < ability.MPCost) reason = $"{ability.MPCost} MP needed.";

            return reason == null;
        }

        // ---------------------------------------------------------------------
        // Doing it
        // ---------------------------------------------------------------------

        // Single-target convenience: aims at the target's tile.
        public bool TryExecuteAbility(GridUnit user, GridUnit target, AbilityDefinition ability, out string resultMessage)
        {
            if (target == null)
            {
                resultMessage = "Invalid action: no target.";
                return false;
            }

            return TryExecuteAt(user, target.GridCoords, ability, out resultMessage);
        }

        // Validates and applies an ability aimed at a tile. resultMessage always explains the
        // outcome, whether it succeeded or why it did not.
        //
        // Validation runs before any mutation: MP is only spent once the action is known to be
        // legal, so a rejected ability costs nothing. Once carried out, every affected unit is
        // resolved in turn - each with its own hit roll and its own reaction.
        public bool TryExecuteAt(GridUnit user, Vector2Int center, AbilityDefinition ability, out string resultMessage)
        {
            if (user == null || ability == null)
            {
                resultMessage = "Invalid action: user or ability is missing.";
                return false;
            }

            if (!CanUse(user, ability, out string reason))
            {
                resultMessage = $"{user.DisplayName} cannot use {ability.AbilityName}: {reason}";
                return false;
            }

            if (!IsInRange(user, center, ability))
            {
                int distance = GridManager.GetGridDistance(user.GridCoords, center);
                resultMessage = $"{center} is out of range for {ability.AbilityName} " +
                                $"(distance {distance}, range {GetRange(user, ability, center)}).";
                return false;
            }

            List<GridUnit> targets = GetAffectedUnits(user, center, ability);

            if (targets.Count == 0)
            {
                resultMessage = $"{ability.AbilityName} has no valid target at {center}.";
                return false;
            }

            user.SpendMP(ability.MPCost);

            // The attacker turns to its aim point as it acts. After validation so a rejected
            // ability does not swing the unit around.
            if (center != user.GridCoords)
            {
                user.FaceTowards(center);
            }

            // A charged ability only starts here: the clock fires it later, on whoever is there.
            if (ability.ChargeTime > 0)
            {
                resultMessage = $"{user.DisplayName} begins charging {ability.AbilityName} at {center} ({ability.ChargeTime} ticks).";
                OnChargeStarted?.Invoke(user, center, ability);
                return true;
            }

            Resolve(user, ability, targets, out resultMessage);
            return true;
        }

        // A charged ability going off: no MP (paid when chosen), no range check (the user may
        // have been pushed or the target may have walked into it - that is the point). Returns
        // false when nobody is left in the blast.
        public bool ResolveCharge(GridUnit user, Vector2Int center, AbilityDefinition ability, out string resultMessage)
        {
            List<GridUnit> targets = GetAffectedUnits(user, center, ability);

            if (targets.Count == 0)
            {
                resultMessage = $"{user.DisplayName}'s {ability.AbilityName} goes off at {center} and hits nothing.";
                return false;
            }

            Resolve(user, ability, targets, out string message);
            resultMessage = $"{ability.AbilityName} fires! {message}";
            return true;
        }

        // Applies an ability to everyone it touches, then reports. Shared by instant and charged
        // abilities.
        private void Resolve(GridUnit user, AbilityDefinition ability, List<GridUnit> targets, out string resultMessage)
        {
            List<string> lines = new List<string>();
            List<ActionOutcome> outcomes = new List<ActionOutcome>();

            foreach (GridUnit target in targets)
            {
                ApplyTo(user, target, ability, lines, outcomes, allowReaction: true);
            }

            resultMessage = string.Join(" ", lines);

            // After everything, so listeners observe the finished result rather than a half-applied
            // one. Not raised on any rejection path above - nothing changed there.
            OnStatsChanged?.Invoke();

            foreach (ActionOutcome outcome in outcomes)
            {
                OnActionResolved?.Invoke(outcome);
            }
        }

        // Overwatch: `mover` just finished a move. The first unit on watch that is hostile to it,
        // standing, not stunned, and has it in reach takes one shot (evasion applies), and its watch
        // ends. Null when nobody fired.
        public string ResolveOverwatch(IEnumerable<GridUnit> units, GridUnit mover)
        {
            if (mover == null || mover.IsKnockedOut)
            {
                return null;
            }

            foreach (GridUnit watcher in units)
            {
                if (watcher == null || watcher == mover || watcher.IsKnockedOut || watcher.IsFlatlined
                    || watcher.IsStunned || watcher.CurrentFaction == mover.CurrentFaction)
                {
                    continue;
                }

                GridUnit.ActiveStatus watch = watcher.FindStatus(StatusKind.Overwatch);

                if (watch == null || GridManager.GetGridDistance(watcher.GridCoords, mover.GridCoords)
                    > GetRange(watcher, watch.Ability, mover.GridCoords))
                {
                    continue;
                }

                watcher.RemoveStatus(watch);
                watcher.FaceTowards(mover.GridCoords);

                int hitChance = PhysicalHitChance(watcher, mover);
                bool hits = hitChance >= 100 || random.Next(100) < hitChance;
                string message;
                bool wasStanding = !mover.IsKnockedOut;

                if (hits)
                {
                    int damage = Mathf.Max(1, GetPower(watcher, watch.Ability) - mover.EffectiveStats.Defense);
                    mover.ApplyDamage(damage);
                    string ko = mover.IsKnockedOut ? " KNOCKED OUT!" : string.Empty;
                    message = $"OVERWATCH: {watcher.DisplayName} fires on {mover.DisplayName} for {damage} " +
                              $"({mover.CurrentStats.HP}/{mover.EffectiveStats.MaxHP}).{ko}";
                }
                else
                {
                    message = $"OVERWATCH: {watcher.DisplayName} fires on {mover.DisplayName} and misses ({hitChance}%).";
                }

                OnStatsChanged?.Invoke();
                OnActionResolved?.Invoke(new ActionOutcome(watcher, mover, watch.Ability, hits, wasStanding && mover.IsKnockedOut));
                return message;
            }

            return null;
        }

        private void ApplyTo(
            GridUnit user, GridUnit target, AbilityDefinition ability,
            List<string> lines, List<ActionOutcome> outcomes, bool allowReaction)
        {
            bool effective = false;
            bool wasStanding = !target.IsKnockedOut;
            int hitChance = GetHitChance(user, target, ability);
            bool hits = hitChance >= 100 || random.Next(100) < hitChance;

            switch (ability.EffectType)
            {
                case AbilityEffectType.Damage:
                {
                    if (!hits)
                    {
                        lines.Add($"{user.DisplayName}'s {ability.AbilityName} misses {target.DisplayName} ({hitChance}%).");
                        break;
                    }

                    int damage = CalculateDamage(user, target, ability);
                    target.ApplyDamage(damage);
                    effective = true;

                    string ko = target.IsKnockedOut ? " KNOCKED OUT!" : string.Empty;
                    lines.Add($"{user.DisplayName} hits {target.DisplayName} with {ability.AbilityName} for {damage} " +
                              $"({target.CurrentStats.HP}/{target.EffectiveStats.MaxHP}).{ko}");

                    // A rider status rides on the hit: a marking shot, a disarming cut.
                    if (ability.InflictsStatus && !target.IsKnockedOut)
                    {
                        target.AddStatus(ability, user, Mathf.Max(1, GetPower(user, ability)));
                        lines.Add($"{target.DisplayName} gets {ability.AbilityName} ({DescribeStatus(ability)}, {ability.Duration} turns).");
                    }

                    break;
                }

                case AbilityEffectType.Heal:
                {
                    // Measured either side of the call rather than trusting Power, so the message
                    // reports what actually landed after the MaxHP cap.
                    int before = target.CurrentStats.HP;
                    target.Heal(GetPower(user, ability));
                    int restored = target.CurrentStats.HP - before;
                    effective = restored > 0;
                    lines.Add($"{user.DisplayName} heals {target.DisplayName} for {restored}.");
                    break;
                }

                case AbilityEffectType.Revive:
                {
                    effective = target.Revive(GetPower(user, ability));
                    lines.Add($"{user.DisplayName} reboots {target.DisplayName} ({target.CurrentStats.HP} HP).");
                    break;
                }

                case AbilityEffectType.Override:
                {
                    // A failed hack still spends the action and the MP - the attempt is the action,
                    // as with FFT's Invite.
                    int chance = GetOverrideChance(user, target, ability);

                    if (random.Next(100) < chance)
                    {
                        effective = true;
                        target.SetFaction(user.CurrentFaction);
                        lines.Add($"{user.DisplayName} overrides {target.DisplayName} ({chance}%) - it now fights for {user.CurrentFaction}.");
                    }
                    else
                    {
                        lines.Add($"{user.DisplayName}'s {ability.AbilityName} fails against {target.DisplayName} ({chance}%).");
                    }

                    break;
                }

                default:
                {
                    // Buff, Debuff, StatusEffect: a status with the ability's modifier and kind.
                    // Damage and healing over time carry the user's power at the moment of casting.
                    if (!hits)
                    {
                        lines.Add($"{user.DisplayName}'s {ability.AbilityName} misses {target.DisplayName} ({hitChance}%).");
                        break;
                    }

                    target.AddStatus(ability, user, Mathf.Max(1, GetPower(user, ability)));
                    effective = true;
                    lines.Add($"{target.DisplayName} gets {ability.AbilityName} ({DescribeStatus(ability)}, {ability.Duration} turns).");
                    break;
                }
            }

            outcomes.Add(new ActionOutcome(user, target, ability, effective, wasStanding && target.IsKnockedOut));

            if (allowReaction && effective && ability.IsHarmful && target != user)
            {
                TryReact(target, user, lines, outcomes);
            }
        }

        // The defender's Reaction ability, if it rolls. Counters never trigger another reaction -
        // ApplyTo is called with allowReaction off - so two counter-fighters cannot loop.
        private void TryReact(GridUnit defender, GridUnit attacker, List<string> lines, List<ActionOutcome> outcomes)
        {
            AbilityDefinition reaction = defender.Reaction;

            if (reaction == null || defender.IsKnockedOut || defender.IsStunned)
            {
                return;
            }

            if (random.Next(100) >= reaction.TriggerChance)
            {
                return;
            }

            switch (reaction.ReactionKind)
            {
                case ReactionKind.Counter:
                {
                    if (attacker.IsKnockedOut
                        || GridManager.GetGridDistance(defender.GridCoords, attacker.GridCoords) > Mathf.Max(1, reaction.Range))
                    {
                        return;
                    }

                    lines.Add($"{defender.DisplayName} counters!");
                    defender.FaceTowards(attacker.GridCoords);

                    int damage = Mathf.Max(1, GetPower(defender, reaction) - attacker.EffectiveStats.Defense);
                    bool wasStanding = !attacker.IsKnockedOut;
                    attacker.ApplyDamage(damage);
                    lines.Add($"{attacker.DisplayName} takes {damage} ({attacker.CurrentStats.HP}/{attacker.EffectiveStats.MaxHP}).");
                    outcomes.Add(new ActionOutcome(defender, attacker, reaction, true, wasStanding && attacker.IsKnockedOut));
                    break;
                }

                case ReactionKind.Brace:
                {
                    defender.AddStatus(reaction, defender, 0);
                    lines.Add($"{defender.DisplayName} braces ({reaction.AbilityName}).");
                    break;
                }
            }
        }
    }
}
