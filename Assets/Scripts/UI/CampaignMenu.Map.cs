using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.UI
{
    // The city map and what hangs off it: district screen, travel, shop, job board and the
    // pre-fight briefing. Split from CampaignMenu.cs only to keep files readable; same class,
    // same prototype rules (rebuilt from scratch on every change, no rules of its own).
    public partial class CampaignMenu
    {
        // The district the crew is in. This is the hub: everything between fights starts here.
        private void ShowHub()
        {
            DistrictDefinition district = campaign.CurrentDistrict;

            if (district == null)
            {
                // A config without districts still plays: one flat contract list, as before the map.
                ShowFlatHub();
                return;
            }

            BeginScreen(district.districtName, null, showStatus: false);
            MenuBuilder.AddText(column, DescribeStatusStrip(withDistrict: false), 20f, height: 30f, color: MenuBuilder.DimTextColor);
            string cleared = campaign.IsCleared(district) ? $"   |   <color={MenuBuilder.AccentHex}>CLEARED</color>" : string.Empty;
            MenuBuilder.AddText(column,
                $"Danger {DangerPips(district.danger)}   |   {district.dominantFaction}{cleared}",
                20f, height: 30f, color: MenuBuilder.DimTextColor);

            if (!string.IsNullOrEmpty(district.description))
            {
                MenuBuilder.AddText(column, district.description, 20f);
            }

            MenuBuilder.AddSpacer(column, 16f);

            // The story, when there is one here, is the button the screen is about.
            MissionDefinition story = campaign.NextStoryMission(district);

            if (story != null)
            {
                MenuBuilder.AddButton(column, $"STORY:  {story.missionName}", () => ShowBriefing(story, false),
                    color: MenuBuilder.PrimaryColor);
            }
            else
            {
                MenuBuilder.AddText(column, "No story left here. Travel on, or take contracts.", 18f,
                    color: MenuBuilder.DimTextColor);
            }

            AddServiceRow(("Job Board", ShowMissions, campaign.Listings(district).Count > 0), ("Crew", ShowRoster, true));
            AddServiceRow(("Shop", ShowShop, campaign.ShopStock(district).Count > 0 || campaign.Inventory.Count > 0),
                ("Hire Runners", ShowRecruit, true));
            AddServiceRow(("Travel", ShowTravel, district.connections.Count > 0), ("Save & Jack Out", () =>
            {
                campaign.Save();
                ShowMainMenu();
            }, true));

            EndScreen();
        }

        // The hub's buttons two to a row, so six services do not make a tall stack.
        private void AddServiceRow((string Label, System.Action Show, bool Enabled) left, (string Label, System.Action Show, bool Enabled) right)
        {
            RectTransform row = MenuBuilder.AddRow(column, 52f);
            MenuBuilder.AddButton(row, left.Label, () => left.Show(), -1f, 52f, left.Enabled);
            MenuBuilder.AddButton(row, right.Label, () => right.Show(), -1f, 52f, right.Enabled);
        }

        private void ShowFlatHub()
        {
            BeginScreen("Safehouse", null);
            MenuBuilder.AddButton(column, "Contracts", ShowMissions);
            AddServiceRow(("Crew", ShowRoster, true), ("Hire Runners", ShowRecruit, true));
            MenuBuilder.AddButton(column, "Save & Jack Out", () =>
            {
                campaign.Save();
                ShowMainMenu();
            });
            EndScreen();
        }

        private void ShowMissions()
        {
            DistrictDefinition district = campaign.CurrentDistrict;
            BeginScreen(district != null ? "Job Board" : "Contracts", ShowHub);

            IEnumerable<MissionDefinition> missions = district != null ? campaign.Listings(district) : config.Missions;

            foreach (MissionDefinition mission in missions)
            {
                RectTransform row = MenuBuilder.AddRow(column, 72f);
                MenuBuilder.AddText(row,
                    $"<b>{mission.missionName}</b>   <color={MenuBuilder.CreditsHex}><size=18>{mission.credits} cr</size></color>\n" +
                    $"<size=17><color={MenuBuilder.DimHex}>{DescribeOpponents(mission)}</color></size>",
                    22f, TextAlignmentOptions.Left);

                MenuBuilder.AddButton(row, "Brief", () => ShowBriefing(mission, false), 140f, RowButtonHeight);
            }

            EndScreen();
        }

        // Last stop before the board: what the fight is, who is in it, what it pays, and who goes.
        // FFT shows the formation screen here; deploy toggles do the same job.
        private void ShowBriefing(MissionDefinition mission, bool road)
        {
            System.Action back = road
                ? () =>
                {
                    campaign.CancelTravel();
                    SetNotice("Turned back.");
                    SaveAndShow(ShowHub);
                }
                : () => SaveAndShow(mission.kind == MissionKind.Story ? ShowHub : ShowMissions);

            // Back is laid out by hand here, beside Fight; Escape still goes back.
            BeginScreen(mission.missionName, back);
            System.Action escape = backAction;
            backAction = null;

            string kind = road ? "AMBUSH ON THE ROAD" : mission.kind == MissionKind.Story ? "STORY" : "CONTRACT";
            Color kindColor = road ? new Color(1f, 0.45f, 0.35f)
                : mission.kind == MissionKind.Story ? MenuBuilder.StoryTextColor
                : MenuBuilder.DimTextColor;
            MenuBuilder.AddText(column, kind, 22f, color: kindColor);

            if (road)
            {
                MenuBuilder.AddText(column,
                    $"Something is waiting on the way to {campaign.TravelDestination}. Win to get through, or turn back.",
                    20f);
            }
            else if (!string.IsNullOrEmpty(mission.introText))
            {
                MenuBuilder.AddText(column, mission.introText, 20f);
            }
            else if (!string.IsNullOrEmpty(mission.description))
            {
                MenuBuilder.AddText(column, mission.description, 20f);
            }

            MenuBuilder.AddHeader(column, "Mission");
            MenuBuilder.AddText(column, "Objective: " + mission.DescribeObjective(), 20f, TextAlignmentOptions.Left,
                color: MenuBuilder.AccentTextColor);
            MenuBuilder.AddText(column,
                "Opposition: " + string.Join(", ", mission.opponents.Where(e => e.faction != Faction.Player).Select(e =>
                    $"{EntryName(e)} Lv{e.level}")),
                20f, TextAlignmentOptions.Left);

            List<string> guests = mission.opponents.Where(e => e.faction == Faction.Player).Select(EntryName).ToList();

            if (guests.Count > 0)
            {
                MenuBuilder.AddText(column, "With you: " + string.Join(", ", guests), 20f, TextAlignmentOptions.Left);
            }

            int pay = road ? mission.credits / 2 : mission.credits;
            string loot = !road && mission.loot.Count > 0
                ? "  +  " + string.Join(", ", mission.loot.Where(i => i != null).Select(i => i.DisplayName))
                : string.Empty;
            MenuBuilder.AddText(column, $"Pay: {pay} cr{loot}", 20f, TextAlignmentOptions.Left, color: MenuBuilder.CreditsTextColor);

            MenuBuilder.AddHeader(column, $"Deploy  {campaign.DeployedCount}/{campaign.MaxDeployed}");

            foreach (RosterUnit unit in campaign.Roster)
            {
                RectTransform row = MenuBuilder.AddRow(column, SmallRowHeight, 6f);
                string weapon = unit.Weapon != null ? unit.Weapon.DisplayName : "unarmed";
                MenuBuilder.AddText(row,
                    $"<b>{unit.Name}</b>  {unit.JobName} Lv{unit.Level}  <size=17><color={MenuBuilder.DimHex}>{weapon}</color></size>",
                    20f, TextAlignmentOptions.Left);
                AddDeployToggle(row, unit, () => ShowBriefing(mission, road));
            }

            MenuBuilder.AddSpacer(column, 12f);
            RectTransform buttons = MenuBuilder.AddRow(column, 56f);
            MenuBuilder.AddButton(buttons, campaign.DeployedCount > 0 ? "Fight" : "Deploy someone first",
                () => StartMission(mission, road), -1f, 52f, campaign.DeployedCount > 0, MenuBuilder.PrimaryColor);
            MenuBuilder.AddButton(buttons, (road ? "Turn Back" : "Back") + $"   <size=16><color={MenuBuilder.DimHex}>[Esc]</color></size>",
                () => back(), -1f, 52f);

            EndScreen();
            backAction = escape;
        }

        private void ShowTravel()
        {
            DistrictDefinition here = campaign.CurrentDistrict;
            BeginScreen("Travel", ShowHub);
            MenuBuilder.AddText(column, $"Roads out of {here.districtName}. Dangerous districts get ambushed on the way in.",
                20f, color: MenuBuilder.DimTextColor);

            foreach (DistrictDefinition next in campaign.Connections(here))
            {
                bool open = campaign.IsDistrictOpen(next.districtName);
                float chance = campaign.AmbushChanceFor(next, out string why);
                string reason = why != null ? $" ({why})" : string.Empty;
                string info = open
                    ? $"Danger {DangerPips(next.danger)}   {Mathf.RoundToInt(chance * 100f)}% ambush{reason}   |   {next.dominantFaction}"
                    : "LOCKED - the story has not reached it";

                RectTransform row = MenuBuilder.AddRow(column, ListRowHeight);
                MenuBuilder.AddText(row, $"<b>{next.districtName}</b>\n<size=17><color={MenuBuilder.DimHex}>{info}</color></size>",
                    22f, TextAlignmentOptions.Left);
                MenuBuilder.AddButton(row, open ? "Go" : "Locked", () => Travel(next), 140f, RowButtonHeight, open);
            }

            EndScreen();
        }

        private void Travel(DistrictDefinition destination)
        {
            if (!campaign.TryTravel(destination, Random.value, out MissionDefinition ambush, out string reason))
            {
                SetNotice(reason);
                ShowTravel();
                return;
            }

            if (ambush != null)
            {
                ShowBriefing(ambush, true);
                return;
            }

            SetNotice($"Arrived in {destination.districtName} without trouble.");
            SaveAndShow(ShowHub);
        }

        private void ShowShop()
        {
            BeginScreen("Shop", ShowHub);
            DistrictDefinition district = campaign.CurrentDistrict;

            MenuBuilder.AddHeader(column, "Buy");

            foreach (EquipmentDefinition item in campaign.ShopStock(district))
            {
                bool canBuy = campaign.CanBuy(item, out string reason);
                int owned = campaign.Inventory.Count(i => i == item);
                string ownedText = owned > 0 ? $"  <color={MenuBuilder.AccentHex}>(own {owned})</color>" : string.Empty;

                RectTransform row = MenuBuilder.AddRow(column, ListRowHeight, 6f);
                MenuBuilder.AddText(row, $"{DescribeWeapon(item)}{ownedText}\n<size=16><color={MenuBuilder.DimHex}>{DescribeUsers(item)}</color></size>",
                    20f, TextAlignmentOptions.Left);
                MenuBuilder.AddButton(row, canBuy ? $"Buy {item.Price} cr" : $"{item.Price} cr - short", () =>
                {
                    if (campaign.Buy(item))
                    {
                        SetNotice($"Bought {item.DisplayName}.");
                    }

                    SaveAndShow(ShowShop);
                }, 190f, RowButtonHeight, canBuy);
            }

            MenuBuilder.AddHeader(column, "Sell");
            MenuBuilder.AddText(column, "Spare weapons only - unequip one first to sell it.", 17f,
                TextAlignmentOptions.Left, color: MenuBuilder.DimTextColor);

            if (campaign.Inventory.Count == 0)
            {
                MenuBuilder.AddText(column, "Nothing spare.", 20f, TextAlignmentOptions.Left, color: MenuBuilder.DimTextColor);
            }

            foreach (IGrouping<EquipmentDefinition, EquipmentDefinition> group in campaign.Inventory.GroupBy(i => i))
            {
                EquipmentDefinition item = group.Key;
                RectTransform row = MenuBuilder.AddRow(column, SmallRowHeight, 6f);
                MenuBuilder.AddText(row, $"{DescribeWeapon(item)}  x{group.Count()}", 20f, TextAlignmentOptions.Left);
                MenuBuilder.AddButton(row, $"Sell {CampaignState.SellPrice(item)} cr", () =>
                {
                    if (campaign.Sell(item))
                    {
                        SetNotice($"Sold {item.DisplayName}.");
                    }

                    SaveAndShow(ShowShop);
                }, 190f, SmallButtonHeight);
            }

            EndScreen();
        }

        // Who in the crew could hold it - the question a buyer actually has.
        private string DescribeUsers(EquipmentDefinition item)
        {
            List<string> names = campaign.Roster.Where(unit => unit.CanEquip(item.Type)).Select(unit => unit.Name).ToList();
            return names.Count > 0 ? "Usable by " + string.Join(", ", names) : "Nobody in the crew can use this yet";
        }

        // The campaign half of the result screen: pay, loot, story beats.
        private void AddRewardLines()
        {
            if (lastResult.Outcome != MissionOutcome.Victory)
            {
                if (lastResult.WasRoadEncounter)
                {
                    MenuBuilder.AddText(column, $"Pushed back to {campaign.CurrentDistrictName}.", 22f);
                }

                return;
            }

            string loot = lastResult.LootGained.Count > 0
                ? "   Loot: " + string.Join(", ", lastResult.LootGained.Select(i => i.DisplayName))
                : string.Empty;
            MenuBuilder.AddText(column, $"+{lastResult.CreditsEarned} cr{loot}", 22f, color: MenuBuilder.CreditsTextColor);

            if (lastResult.WasRoadEncounter)
            {
                MenuBuilder.AddText(column, $"Road cleared - arrived in {campaign.CurrentDistrictName}.", 22f);
            }

            if (!string.IsNullOrEmpty(lastResult.Mission.outroText) && lastResult.Mission.kind == MissionKind.Story
                && !lastResult.WasRoadEncounter)
            {
                MenuBuilder.AddText(column, lastResult.Mission.outroText, 20f, color: MenuBuilder.StoryTextColor);
            }

            foreach (string joined in lastResult.JoinedCrew)
            {
                MenuBuilder.AddText(column, $"{joined} joined the crew.", 22f, color: MenuBuilder.StoryTextColor);
            }

            if (!string.IsNullOrEmpty(lastResult.DistrictUnlocked))
            {
                MenuBuilder.AddText(column, $"New district open: {lastResult.DistrictUnlocked}", 22f, color: MenuBuilder.StoryTextColor);
            }
        }

        private static string DangerPips(int danger)
        {
            return new string('#', danger) + new string('-', Mathf.Max(0, 5 - danger));
        }

        private static string EntryName(UnitEntry entry)
        {
            return string.IsNullOrEmpty(entry.unitName) ? JobNameOf(entry.job) : entry.unitName;
        }
    }
}
