using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Stats;

namespace Tactica.UI
{
    // The crew member screen. One window, no scrolling, laid out like a game's status screen
    // rather than a document: a fixed character card on the left (who they are, stats, weapon,
    // passives) and a tabbed pane on the right for the three things you do here - pick jobs,
    // equip, learn. FFT splits these into separate sub-menus; tabs keep the card in view while
    // you change things, so the stat changes show up beside the button that caused them.
    //
    // Every rule still lives in CampaignState; this file only asks it and shows the reason when
    // the answer is no.
    public partial class CampaignMenu
    {
        private enum UnitTab
        {
            Jobs,
            Equipment,
            Learn,
        }

        private const float UnitScreenWidth = 1560f;
        private const float UnitBodyHeight = 640f;
        private const float UnitCardWidth = 440f;
        private const int MaxSpareWeaponsShown = 3;
        private const float JobCellHeight = 38f;
        private const int PassivesPerRow = 4;
        private const int LearnJobsPerRow = 6;

        // Kept across units and redraws, so flipping through the crew stays on the same tab.
        private UnitTab unitTab = UnitTab.Jobs;

        // The passive slot the Equipment tab is editing.
        private AbilitySlot equipSlot = AbilitySlot.Reaction;

        // viewJob is the job the Learn tab lists - any unlocked job, not only the current one,
        // since FFT lets you spend a job's JP while wearing another.
        private void ShowUnit(RosterUnit unit, JobDefinition viewJob)
        {
            System.Action redraw = () => ShowUnit(unit, viewJob);
            BeginScreen(unit.Name, ShowRoster, width: UnitScreenWidth);

            AddCrewNavigation(unit);

            RectTransform body = MenuBuilder.AddRow(column, UnitBodyHeight, 16f);
            AddUnitCard(body, unit);

            RectTransform pane = MenuBuilder.AddPanel(body, -1f);

            if (unit.Locked)
            {
                MenuBuilder.AddText(pane, "LOCKED", 22f, TextAlignmentOptions.Left, 30f, MenuBuilder.AccentTextColor);
                MenuBuilder.AddText(pane,
                    "A hacked recruit keeps its job and fixed kit, and earns no JP.\n\n" +
                    "Kit: " + string.Join(", ", unit.ActionLoadout().Select(a => a.AbilityName)),
                    20f, TextAlignmentOptions.Left);
                MenuBuilder.AddFiller(pane);
                EndScreen();
                return;
            }

            AddUnitTabs(pane, unit, viewJob);

            switch (unitTab)
            {
                case UnitTab.Jobs: AddJobsTab(pane, unit, viewJob, redraw); break;
                case UnitTab.Equipment: AddEquipmentTab(pane, unit, redraw); break;
                default: AddLearnTab(pane, unit, viewJob, redraw); break;
            }

            MenuBuilder.AddFiller(pane);

            EndScreen();
        }

        // Flip through the crew without going back to the list.
        private void AddCrewNavigation(RosterUnit unit)
        {
            int index = campaign.Roster.ToList().IndexOf(unit);
            int count = campaign.Roster.Count;

            if (count < 2 || index < 0)
            {
                return;
            }

            RectTransform nav = MenuBuilder.AddRow(column, SmallButtonHeight);
            RosterUnit previous = campaign.Roster[(index - 1 + count) % count];
            RosterUnit next = campaign.Roster[(index + 1) % count];
            MenuBuilder.AddButton(nav, "< " + previous.Name, () => ShowUnit(previous, previous.Job), 240f, SmallButtonHeight);
            MenuBuilder.AddText(nav, $"{index + 1} / {count}", 18f, color: MenuBuilder.DimTextColor);
            MenuBuilder.AddButton(nav, next.Name + " >", () => ShowUnit(next, next.Job), 240f, SmallButtonHeight);
        }

        // The left card: read-only, always visible.
        private void AddUnitCard(RectTransform body, RosterUnit unit)
        {
            RectTransform card = MenuBuilder.AddPanel(body, UnitCardWidth);

            string secondary = unit.SecondaryJob != null ? unit.SecondaryJob.JobName : "-";
            MenuBuilder.AddText(card, $"<b>{unit.JobName}</b>", 26f, TextAlignmentOptions.Left, 34f);
            MenuBuilder.AddText(card, $"<color={MenuBuilder.DimHex}>Secondary</color>  {secondary}", 18f, TextAlignmentOptions.Left, 24f);
            MenuBuilder.AddText(card,
                $"Lv {unit.Level}   <color={MenuBuilder.DimHex}>XP {unit.XP}/{Progression.XPPerLevel}</color>" +
                (unit.Locked ? string.Empty : $"   <color={MenuBuilder.DimHex}>JLv {unit.JobLevel(unit.Job)}</color>"),
                20f, TextAlignmentOptions.Left, 28f);

            MenuBuilder.AddSpacer(card, 6f);
            CharacterStats stats = unit.Stats;
            AddStatPair(card, "HP", stats.MaxHP, "MP", stats.MaxMP);
            AddStatPair(card, "STR", stats.Strength, "TECH", stats.Tech);
            AddStatPair(card, "DEF", stats.Defense, "EV", stats.Evasion);
            AddStatPair(card, "SPD", stats.Speed, "MOV", stats.Move);
            AddStatPair(card, "JMP", stats.Jump, null, 0);

            MenuBuilder.AddSpacer(card, 10f);
            MenuBuilder.AddText(card, "WEAPON", 16f, TextAlignmentOptions.Left, 22f, MenuBuilder.AccentTextColor);
            MenuBuilder.AddText(card, unit.Weapon != null ? DescribeWeapon(unit.Weapon) : $"<color={MenuBuilder.DimHex}>unarmed</color>",
                19f, TextAlignmentOptions.Left, 28f);

            MenuBuilder.AddSpacer(card, 10f);
            MenuBuilder.AddText(card, "PASSIVES", 16f, TextAlignmentOptions.Left, 22f, MenuBuilder.AccentTextColor);
            AddCardPassive(card, "React", unit.Reaction);
            AddCardPassive(card, "Support", unit.Support);
            AddCardPassive(card, "Move", unit.Movement);
            MenuBuilder.AddFiller(card);
        }

        private static void AddStatPair(RectTransform card, string leftName, int leftValue, string rightName, int rightValue)
        {
            RectTransform row = MenuBuilder.AddRow(card, 26f, 8f);
            MenuBuilder.AddText(row, $"<color={MenuBuilder.DimHex}>{leftName}</color>  <b>{leftValue}</b>", 20f, TextAlignmentOptions.Left);
            MenuBuilder.AddText(row, rightName != null ? $"<color={MenuBuilder.DimHex}>{rightName}</color>  <b>{rightValue}</b>" : string.Empty,
                20f, TextAlignmentOptions.Left);
        }

        private static void AddCardPassive(RectTransform card, string slot, AbilityDefinition ability)
        {
            string name = ability != null ? ability.AbilityName : $"<color={MenuBuilder.FaintHex}>empty</color>";
            MenuBuilder.AddText(card, $"<color={MenuBuilder.DimHex}>{slot}</color>  {name}", 19f, TextAlignmentOptions.Left, 26f);
        }

        private void AddUnitTabs(RectTransform pane, RosterUnit unit, JobDefinition viewJob)
        {
            RectTransform tabs = MenuBuilder.AddRow(pane, RowButtonHeight);
            AddTabButton(tabs, "Jobs", UnitTab.Jobs, unit, viewJob);
            AddTabButton(tabs, "Equipment", UnitTab.Equipment, unit, viewJob);
            AddTabButton(tabs, "Learn", UnitTab.Learn, unit, viewJob);
            MenuBuilder.AddSpacer(pane, 6f);
        }

        private void AddTabButton(RectTransform tabs, string label, UnitTab tab, RosterUnit unit, JobDefinition viewJob)
        {
            MenuBuilder.AddButton(tabs, label, () =>
            {
                unitTab = tab;
                ShowUnit(unit, viewJob);
            }, -1f, RowButtonHeight, color: MenuBuilder.On(unitTab == tab));
        }

        // ---------------------------------------------------------------------
        // Jobs: the job tree as a grid - start, mid and top tier in three columns, FFT's job
        // wheel laid flat - then the picked job's details and Primary / Secondary / Learn.
        // ---------------------------------------------------------------------

        private void AddJobsTab(RectTransform pane, RosterUnit unit, JobDefinition viewJob, System.Action redraw)
        {
            JobDefinition picked = viewJob != null ? viewJob : unit.Job;
            List<JobDefinition>[] tiers =
            {
                JobsOfTier(JobTier.Start), JobsOfTier(JobTier.Mid), JobsOfTier(JobTier.Top),
            };

            RectTransform header = MenuBuilder.AddRow(pane, 24f, 6f);

            foreach (string tierName in new[] { "START", "MID", "TOP" })
            {
                MenuBuilder.AddText(header, tierName, 16f, TextAlignmentOptions.Center, color: MenuBuilder.AccentTextColor);
            }

            int rows = tiers.Max(tier => tier.Count);

            for (int r = 0; r < rows; r++)
            {
                RectTransform row = MenuBuilder.AddRow(pane, JobCellHeight, 6f);

                foreach (List<JobDefinition> tier in tiers)
                {
                    if (r < tier.Count)
                    {
                        AddJobCell(row, unit, tier[r], picked);
                    }
                    else
                    {
                        // An empty cell, so the columns stay aligned.
                        MenuBuilder.AddText(row, string.Empty, 16f);
                    }
                }
            }

            MenuBuilder.AddSpacer(pane, 6f);
            AddJobDetail(pane, unit, picked, redraw);
        }

        private List<JobDefinition> JobsOfTier(JobTier tier)
        {
            return campaign.PlayerJobs.Where(job => job.Tier == tier).ToList();
        }

        // One job in the tree. Lit when picked; [P] / [S] mark the unit's primary and secondary;
        // a locked job is dimmed but still clickable, so its requirement can be read.
        private void AddJobCell(RectTransform row, RosterUnit unit, JobDefinition job, JobDefinition picked)
        {
            bool unlocked = CampaignState.IsJobUnlocked(unit, job);
            string role = unit.Job == job ? "  [P]" : unit.SecondaryJob == job ? "  [S]" : string.Empty;
            string level = unlocked ? $"JLv{unit.JobLevel(job)}" : "locked";
            string name = unlocked ? job.JobName : $"<color={MenuBuilder.DimHex}>{job.JobName}</color>";

            MenuBuilder.AddButton(row, $"{name}  <size=14><color={MenuBuilder.DimHex}>{level}</color>{role}</size>",
                () => ShowUnit(unit, job), -1f, JobCellHeight, true, MenuBuilder.On(job == picked));
        }

        private void AddJobDetail(RectTransform pane, RosterUnit unit, JobDefinition job, System.Action redraw)
        {
            bool unlocked = CampaignState.IsJobUnlocked(unit, job);
            bool isPrimary = unit.Job == job;
            bool isSecondary = unit.SecondaryJob == job;
            bool canPrimary = CampaignState.CanSetPrimary(unit, job, out string primaryReason);
            bool canSecondary = CampaignState.CanSetSecondary(unit, job, out _);

            string weapons = job.AllowedWeapons.Count > 0 ? string.Join(", ", job.AllowedWeapons) : "no weapon";
            MenuBuilder.AddText(pane,
                $"<b>{job.JobName}</b>  <size=16><color={MenuBuilder.DimHex}>{job.Tier} tier   |   {weapons}</color></size>",
                22f, TextAlignmentOptions.Left, 30f);

            string status;

            if (!unlocked)
            {
                status = $"Needs {CampaignState.DescribeRequirements(job)}";
            }
            else
            {
                RosterUnit.JobProgress progress = unit.ProgressFor(job);
                int toNext = Progression.JPToNextJobLevel(progress.TotalJP);
                string next = toNext > 0 ? $"{toNext} to next" : "max";
                string refusal = !canPrimary && !isPrimary ? $"   <color={MenuBuilder.WarningHex}>{primaryReason}</color>" : string.Empty;
                status = $"JLv{unit.JobLevel(job)} ({next})   JP {progress.AvailableJP}{refusal}";
            }

            MenuBuilder.AddText(pane, status, 17f, TextAlignmentOptions.Left, 26f, MenuBuilder.DimTextColor);

            RectTransform buttons = MenuBuilder.AddRow(pane, RowButtonHeight);

            // The current primary stays lit and clickable-looking; clicking it does nothing.
            MenuBuilder.AddButton(buttons, "Primary", () =>
            {
                if (isPrimary)
                {
                    return;
                }

                CampaignState.SetPrimaryJob(unit, job);
                campaign.ValidateWeapon(unit);
                SaveAndShow(redraw);
            }, -1f, RowButtonHeight, canPrimary || isPrimary, MenuBuilder.On(isPrimary));

            // Clicking the current secondary clears it - the toggle reads as on/off.
            MenuBuilder.AddButton(buttons, "Secondary", () =>
            {
                CampaignState.SetSecondaryJob(unit, isSecondary ? null : job);
                SaveAndShow(redraw);
            }, -1f, RowButtonHeight, isSecondary || canSecondary, MenuBuilder.On(isSecondary));

            MenuBuilder.AddButton(buttons, "Learn", () =>
            {
                unitTab = UnitTab.Learn;
                ShowUnit(unit, job);
            }, -1f, RowButtonHeight, unlocked);
        }

        // ---------------------------------------------------------------------
        // Equipment: the weapon (pick from the spares directly) and the three passive slots
        // (every learned option as a button - no cycling through them blind).
        // ---------------------------------------------------------------------

        private void AddEquipmentTab(RectTransform pane, RosterUnit unit, System.Action redraw)
        {
            MenuBuilder.AddText(pane, "WEAPON", 18f, TextAlignmentOptions.Left, 26f, MenuBuilder.AccentTextColor);

            RectTransform current = MenuBuilder.AddRow(pane, SmallRowHeight);
            MenuBuilder.AddText(current, unit.Weapon != null ? DescribeWeapon(unit.Weapon) : $"<color={MenuBuilder.DimHex}>unarmed</color>",
                20f, TextAlignmentOptions.Left);
            MenuBuilder.AddButton(current, "Unequip", () =>
            {
                campaign.EquipWeapon(unit, null);
                SaveAndShow(redraw);
            }, 130f, SmallButtonHeight, unit.Weapon != null);

            List<EquipmentDefinition> spares = campaign.EquippableFor(unit);

            if (spares.Count == 0)
            {
                MenuBuilder.AddText(pane, "No spare weapon this job can hold. The shop sells more.", 17f,
                    TextAlignmentOptions.Left, 26f, MenuBuilder.DimTextColor);
            }

            foreach (EquipmentDefinition spare in spares.Take(MaxSpareWeaponsShown))
            {
                EquipmentDefinition item = spare;
                RectTransform row = MenuBuilder.AddRow(pane, 38f);
                MenuBuilder.AddText(row, $"<color={MenuBuilder.DimHex}>spare</color>  " + DescribeWeapon(item), 18f, TextAlignmentOptions.Left);
                MenuBuilder.AddButton(row, "Equip", () =>
                {
                    campaign.EquipWeapon(unit, item);
                    SaveAndShow(redraw);
                }, 130f, 34f);
            }

            if (spares.Count > MaxSpareWeaponsShown)
            {
                MenuBuilder.AddText(pane, $"+{spares.Count - MaxSpareWeaponsShown} more in the inventory", 16f,
                    TextAlignmentOptions.Left, 22f, MenuBuilder.DimTextColor);
            }

            MenuBuilder.AddSpacer(pane, 10f);
            MenuBuilder.AddText(pane, "PASSIVES", 18f, TextAlignmentOptions.Left, 26f, MenuBuilder.AccentTextColor);

            // One slot at a time: with every job learnable, a slot can have a dozen options.
            RectTransform slots = MenuBuilder.AddRow(pane, SmallButtonHeight, 6f);

            foreach (AbilitySlot slot in new[] { AbilitySlot.Reaction, AbilitySlot.Support, AbilitySlot.Movement })
            {
                AbilitySlot chosen = slot;
                AbilityDefinition equipped = EquippedIn(unit, slot);
                MenuBuilder.AddButton(slots, $"{slot}: {(equipped != null ? equipped.AbilityName : "empty")}", () =>
                {
                    equipSlot = chosen;
                    redraw();
                }, -1f, SmallButtonHeight, true, MenuBuilder.On(equipSlot == slot));
            }

            AddPassiveOptions(pane, unit, equipSlot, redraw);
        }

        private static AbilityDefinition EquippedIn(RosterUnit unit, AbilitySlot slot)
        {
            switch (slot)
            {
                case AbilitySlot.Reaction: return unit.Reaction;
                case AbilitySlot.Support: return unit.Support;
                case AbilitySlot.Movement: return unit.Movement;
                default: return null;
            }
        }

        // None plus every learned ability that fits the slot, PassivesPerRow to a row. The
        // equipped one is lit, and its description is spelled out underneath.
        private void AddPassiveOptions(RectTransform pane, RosterUnit unit, AbilitySlot slot, System.Action redraw)
        {
            AbilityDefinition current = EquippedIn(unit, slot);
            List<AbilityDefinition> options = new List<AbilityDefinition> { null };
            options.AddRange(CampaignState.LearnedFor(unit, slot));

            RectTransform row = null;

            for (int i = 0; i < options.Count; i++)
            {
                if (i % PassivesPerRow == 0)
                {
                    row = MenuBuilder.AddRow(pane, SmallButtonHeight, 6f);
                }

                AbilityDefinition choice = options[i];
                bool on = current == choice;
                MenuBuilder.AddButton(row, choice != null ? choice.AbilityName : "None", () =>
                {
                    if (on)
                    {
                        return;
                    }

                    campaign.EquipPassiveAndValidate(unit, slot, choice);
                    SaveAndShow(redraw);
                }, -1f, SmallButtonHeight, true, MenuBuilder.On(on));
            }

            // Pads the last row, so its buttons keep the same width as the rows above.
            for (int i = options.Count % PassivesPerRow; i > 0 && i < PassivesPerRow; i++)
            {
                MenuBuilder.AddText(row, string.Empty, 16f);
            }

            if (options.Count == 1)
            {
                MenuBuilder.AddText(pane, $"No {slot} ability learned yet - see Learn.", 16f, TextAlignmentOptions.Left, 24f,
                    MenuBuilder.DimTextColor);
            }
            else if (current != null)
            {
                MenuBuilder.AddText(pane, $"{current.AbilityName}: {current.Description}", 16f, TextAlignmentOptions.Left,
                    color: MenuBuilder.DimTextColor);
            }
        }

        // ---------------------------------------------------------------------
        // Learn: pick a job, spend its JP.
        // ---------------------------------------------------------------------

        private void AddLearnTab(RectTransform pane, RosterUnit unit, JobDefinition viewJob, System.Action redraw)
        {
            List<JobDefinition> unlocked = campaign.PlayerJobs.Where(j => CampaignState.IsJobUnlocked(unit, j)).ToList();

            if (viewJob == null || !unlocked.Contains(viewJob))
            {
                viewJob = unit.Job;
            }

            // LearnJobsPerRow to a row, padded so every button keeps the same width.
            RectTransform jobs = null;

            for (int i = 0; i < unlocked.Count; i++)
            {
                if (i % LearnJobsPerRow == 0)
                {
                    jobs = MenuBuilder.AddRow(pane, SmallButtonHeight, 6f);
                }

                JobDefinition tab = unlocked[i];
                MenuBuilder.AddButton(jobs, tab.JobName, () => ShowUnit(unit, tab), -1f, SmallButtonHeight,
                    color: MenuBuilder.On(tab == viewJob));
            }

            for (int i = unlocked.Count % LearnJobsPerRow; i > 0 && i < LearnJobsPerRow; i++)
            {
                MenuBuilder.AddText(jobs, string.Empty, 16f);
            }

            RosterUnit.JobProgress progress = unit.ProgressFor(viewJob);
            MenuBuilder.AddText(pane, $"{viewJob.JobName}:  {progress.AvailableJP} JP to spend", 20f,
                TextAlignmentOptions.Left, 30f, MenuBuilder.CreditsTextColor);

            foreach (AbilityDefinition ability in viewJob.AllAbilities())
            {
                AbilityDefinition item = ability;
                JobDefinition teaching = viewJob;
                RectTransform row = MenuBuilder.AddRow(pane, SmallRowHeight);
                string weapon = item.RequiredWeapon != WeaponType.None ? $"  [{item.RequiredWeapon}]" : string.Empty;
                string cost = item.MPCost > 0 ? $"  {item.MPCost} MP" : string.Empty;
                string equipped = IsEquippedPassive(unit, item) ? $"  <color={MenuBuilder.AccentHex}>(equipped)</color>" : string.Empty;
                MenuBuilder.AddText(row,
                    $"{item.AbilityName}{equipped}  <size=15><color={MenuBuilder.DimHex}>{item.Slot}{cost}{weapon}  {item.Description}</color></size>",
                    19f, TextAlignmentOptions.Left);

                bool learned = unit.HasLearned(item);
                bool canLearn = CampaignState.CanLearn(unit, teaching, item, out _);
                string label = learned ? "Learned" : canLearn ? $"Learn {item.JPCost} JP" : $"{item.JPCost} JP";
                MenuBuilder.AddButton(row, label, () =>
                {
                    if (CampaignState.Learn(unit, teaching, item))
                    {
                        SetNotice($"{unit.Name} learned {item.AbilityName}.");
                    }

                    SaveAndShow(() => ShowUnit(unit, teaching));
                }, 160f, SmallButtonHeight, canLearn, MenuBuilder.On(learned));
            }
        }
    }
}
