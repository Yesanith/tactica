using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tactica.Campaign;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.UI
{
    // The one-line descriptions the screens share: the status strip, roster rows, weapons,
    // mission opposition. Text only - no layout.
    public partial class CampaignMenu
    {
        // Under every title: where the crew is and what it has.
        private string DescribeStatusStrip(bool withDistrict = true)
        {
            string district = withDistrict && campaign.CurrentDistrict != null
                ? campaign.CurrentDistrict.districtName.ToUpperInvariant() + "   |   "
                : string.Empty;
            return $"{district}<color={MenuBuilder.CreditsHex}>{campaign.Credits} cr</color>   |   " +
                   $"Crew {campaign.Roster.Count}/{campaign.MaxRosterSize}   |   " +
                   $"Deployed {campaign.DeployedCount}/{campaign.MaxDeployed}";
        }

        private static string DescribeRosterUnit(RosterUnit unit)
        {
            string jobs = unit.SecondaryJob != null ? $"{unit.JobName} / {unit.SecondaryJob.JobName}" : unit.JobName;
            string progress = unit.Locked
                ? "locked"
                : $"JLv{unit.JobLevel(unit.Job)}  JP {unit.ProgressFor(unit.Job).AvailableJP}  XP {unit.XP}/{Progression.XPPerLevel}";
            string weapon = unit.Weapon != null ? unit.Weapon.DisplayName : "unarmed";
            CharacterStats stats = unit.Stats;
            return $"<b>{unit.Name}</b>  {jobs}  Lv{unit.Level}   <size=17><color={MenuBuilder.DimHex}>{progress}   |   {weapon}</color></size>\n" +
                   $"<size=17><color={MenuBuilder.DimHex}>HP {stats.MaxHP}  MP {stats.MaxMP}  STR {stats.Strength}  TECH {stats.Tech}  " +
                   $"DEF {stats.Defense}  SPD {stats.Speed}  MOV {stats.Move}</color></size>";
        }

        // "Monoblade Mk I  PWR 4  RNG 1  +1 SPD" - the numbers that matter for picking one.
        private static string DescribeWeapon(EquipmentDefinition weapon)
        {
            List<string> parts = new List<string> { $"PWR {weapon.Power}", $"RNG {weapon.Range}" };

            if (weapon.AreaOfEffect > 0) parts.Add($"AREA {weapon.AreaOfEffect}");

            CharacterStats bonus = weapon.StatBonus;
            AddBonus(parts, bonus.MaxHP, "HP");
            AddBonus(parts, bonus.MaxMP, "MP");
            AddBonus(parts, bonus.Strength, "STR");
            AddBonus(parts, bonus.Tech, "TECH");
            AddBonus(parts, bonus.Defense, "DEF");
            AddBonus(parts, bonus.Speed, "SPD");
            AddBonus(parts, bonus.Move, "MOV");
            AddBonus(parts, bonus.Jump, "JMP");
            AddBonus(parts, bonus.Evasion, "EV");

            return $"{weapon.DisplayName}  <size=17><color={MenuBuilder.DimHex}>{string.Join("  ", parts)}</color></size>";
        }

        private static void AddBonus(List<string> parts, int value, string stat)
        {
            if (value != 0)
            {
                parts.Add($"{(value > 0 ? "+" : string.Empty)}{value} {stat}");
            }
        }

        private static bool IsEquippedPassive(RosterUnit unit, AbilityDefinition ability)
        {
            return ability != null && (unit.Reaction == ability || unit.Support == ability || unit.Movement == ability);
        }

        private static string DescribeOpponents(MissionDefinition mission)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(mission.DescribeObjective().ToUpperInvariant()).Append("  -  ");

            if (!string.IsNullOrEmpty(mission.description))
            {
                builder.Append(mission.description).Append("  -  ");
            }

            builder.Append(string.Join(", ", mission.opponents
                .Where(entry => entry.faction != Faction.Player)
                .Select(entry => $"{(string.IsNullOrEmpty(entry.unitName) ? JobNameOf(entry.job) : entry.unitName)} Lv{entry.level}")));

            return builder.ToString();
        }

        private static string DescribeEnd(MissionUnitReport report)
        {
            string state = report.Flatlined ? ", FLATLINED" : report.KnockedOut ? ", KO" : string.Empty;
            return $"<color={MenuBuilder.DimHex}>[{report.FinalFaction}{state}]</color>";
        }

        private static string JobNameOf(JobDefinition job)
        {
            return job != null ? job.JobName : "(no job)";
        }
    }
}
