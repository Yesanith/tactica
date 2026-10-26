using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Tactica.Campaign;
using Crew = Tactica.EditorTools.BalanceSim.CrewSpec;

namespace Tactica.EditorTools
{
    // The standard balance set: crews of each tier against the contracts the generator would hand
    // them, from Neon Row's easy work to Spire Gate. Tactica > Balance in Play mode; results land in
    // Temp/BalanceSim.txt after a few minutes. PROGRESS.md records the last run.
    public static class BalanceScenarios
    {
        private const int Runs = 6;

        [MenuItem("Tactica/Balance/Run standard auto-battles")]
        private static void RunStandardSet()
        {
            Debug.Log("BalanceSim: " + BalanceSim.Run(StandardSet()));
        }

        [MenuItem("Tactica/Balance/Run standard auto-battles", true)]
        private static bool CanRun()
        {
            return EditorApplication.isPlaying && !BalanceSim.Busy;
        }

        public static List<BalanceSim.Scenario> StandardSet()
        {
            return new List<BalanceSim.Scenario>
            {
                Contract("start L2", Start(2, 1), "Neon Row", 2),
                Contract("start L5 Mk2", Start(5, 2), "Rustyard", 5),
                Contract("start L5 Mk2", Start(5, 2), "Spire Gate", 5),
                Contract("mid L8 Mk3", Mid(8, 3), "Rustyard", 8),
                Contract("mid L8 Mk3", Mid(8, 3), "Spire Gate", 8),
                Contract("hybrid L8 Mk3", new[] { new Crew("Warden", 8, 3, "CyberKnight"), new Crew("Duelist", 8, 3, "Blade"), new Crew("Sniper", 8, 3, "Deadeye"), new Crew("Surgeon", 8, 3, "FieldMedic") }, "Spire Gate", 8),
                Contract("hackers L10 Mk3", new[] { new Crew("Netrunner", 10, 3), new Crew("Daemon", 10, 3), new Crew("Overmind", 10, 3), new Crew("Surgeon", 10, 3) }, "Spire Gate", 10),
                Contract("top L12 Mk3", new[] { new Crew("Juggernaut", 12, 3), new Crew("Reaper", 12, 3), new Crew("Railgunner", 12, 3), new Crew("CyberSaint", 12, 3) }, "Spire Gate", 12),
                Contract("glass L10 Mk3", new[] { new Crew("Phantom", 10, 3, "Blade"), new Crew("Reaper", 10, 3), new Crew("Sniper", 10, 3), new Crew("Deadeye", 10, 3) }, "Spire Gate", 10),
                Contract("tanks L10 Mk3", new[] { new Crew("Juggernaut", 10, 3), new Crew("Warden", 10, 3), new Crew("Warlord", 10, 3), new Crew("CyberSaint", 10, 3) }, "Rustyard", 10, ContractGenerator.Purpose.Road),
            };
        }

        private static Crew[] Start(int level, int tier)
        {
            return new[] { new Crew("CyberKnight", level, tier), new Crew("Blade", level, tier), new Crew("Deadeye", level, tier), new Crew("FieldMedic", level, tier) };
        }

        private static Crew[] Mid(int level, int tier)
        {
            return new[] { new Crew("Warden", level, tier), new Crew("Duelist", level, tier), new Crew("Sniper", level, tier), new Crew("Surgeon", level, tier) };
        }

        // Generated contracts for a crew of this level, a fixed seed per run so a rerun after a
        // tuning change meets the same fights.
        public static BalanceSim.Scenario Contract(string name, Crew[] crew, string districtName, int crewLevel,
            ContractGenerator.Purpose purpose = ContractGenerator.Purpose.Listing)
        {
            CampaignConfig config = Resources.Load<CampaignConfig>(CampaignConfig.ResourcePath);
            DistrictDefinition district = config.Districts.First(d => d.districtName == districtName);

            return new BalanceSim.Scenario
            {
                Name = $"{name} vs {districtName} ({purpose}, crew L{crewLevel})",
                Runs = Runs,
                Crew = crew.ToList(),
                Mission = run => ContractGenerator.Generate(district, config.EscortJob, 5000 + run * 131 + crewLevel, purpose, crewLevel),
            };
        }
    }
}
