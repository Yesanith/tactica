using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Tactica.Campaign;
using Tactica.Combat;
using Tactica.Grid;
using Tactica.Stats;

namespace Tactica.EditorTools
{
    // Auto-battles for balance work: the real MissionRunner, CombatManager and AI play both sides
    // of a fight, many times over, and the results are written to Temp/BalanceSim.txt. The standard
    // set is in BalanceScenarios (Tactica > Balance menu).
    //
    // Play mode only. The crew are spawned as roster units and then handed to the AI (made guests),
    // so the numbers say how the kits fare under the AI's judgement, not a player's. The campaign
    // menu is switched off while it runs, so nothing is applied to the save; Time.timeScale is
    // raised to get through the AI's think delays.
    public static class BalanceSim
    {
        public class CrewSpec
        {
            public string Job;
            public int Level = 1;
            public int WeaponTier = 1;
            public string Secondary;

            public CrewSpec(string job, int level, int weaponTier = 1, string secondary = null)
            {
                Job = job;
                Level = level;
                WeaponTier = weaponTier;
                Secondary = secondary;
            }
        }

        public class Scenario
        {
            public string Name;
            public List<CrewSpec> Crew = new List<CrewSpec>();

            // Builds the fight for run i - a new map seed each time, so terrain does not decide.
            public Func<int, MissionDefinition> Mission;
            public int Runs = 6;
        }

        private class Tally
        {
            public int Wins, Losses, Draws, Turns, CrewDown, FoesDown, CrewCount, FoeCount;
            public readonly Dictionary<string, int> Uses = new Dictionary<string, int>();
            public readonly Dictionary<string, int> Knockouts = new Dictionary<string, int>();
            public readonly List<string> Stalls = new List<string>();
        }

        public const string ResultPath = "Temp/BalanceSim.txt";
        private const float SimTimeScale = 40f;
        private const int MaxTurnsPerFight = 120;

        private static readonly Queue<(Scenario scenario, int run)> queue = new Queue<(Scenario, int)>();
        private static readonly Dictionary<Scenario, Tally> tallies = new Dictionary<Scenario, Tally>();
        private static readonly List<Scenario> order = new List<Scenario>();
        private static MissionRunner runner;
        private static Behaviour menu;
        private static Scenario current;
        private static bool fighting;
        private static bool wasRunningInBackground;

        private static readonly FieldInfo GuestField = typeof(GridUnit).GetField("isGuest", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool Busy { get; private set; }
        public static string Progress => Busy ? $"{order.Sum(s => s.Runs) - queue.Count} / {order.Sum(s => s.Runs)}" : "idle";

        public static string Run(IEnumerable<Scenario> scenarios)
        {
            if (!EditorApplication.isPlaying)
            {
                return "Enter Play mode first.";
            }

            if (Busy)
            {
                return "Already running: " + Progress;
            }

            runner = UnityEngine.Object.FindAnyObjectByType<MissionRunner>();

            if (runner == null)
            {
                return "No MissionRunner in the scene.";
            }

            queue.Clear();
            tallies.Clear();
            order.Clear();

            foreach (Scenario scenario in scenarios)
            {
                order.Add(scenario);
                tallies[scenario] = new Tally();

                for (int i = 0; i < scenario.Runs; i++)
                {
                    queue.Enqueue((scenario, i));
                }
            }

            menu = UnityEngine.Object.FindAnyObjectByType<Tactica.UI.CampaignMenu>();

            if (menu != null)
            {
                menu.enabled = false;
            }

            runner.OnMissionFinished += HandleFinished;
            runner.Combat.Resolver.OnActionResolved += HandleAction;
            Time.timeScale = SimTimeScale;

            // An unfocused editor stops the player loop otherwise, and the fights never move.
            wasRunningInBackground = Application.runInBackground;
            Application.runInBackground = true;
            Busy = true;
            fighting = false;
            EditorApplication.update += Step;
            return $"Queued {queue.Count} fights.";
        }

        private static void Step()
        {
            if (!EditorApplication.isPlaying || runner == null)
            {
                Stop("Play mode ended.");
                return;
            }

            if (fighting)
            {
                // The crew are guests here, and guests do not count for the end condition - so a
                // wiped crew never loses on its own. Call it.
                bool crewStanding = runner.SpawnedUnits.Any(u => runner.GetRosterSource(u) != null && !u.IsKnockedOut && !u.IsFlatlined);

                if (runner.IsRunning && !crewStanding && runner.Combat.CurrentState != CombatState.CombatEnd)
                {
                    tallies[current].Losses++;
                    tallies[current].Turns += runner.Combat.TurnNumber;
                    runner.Retreat();
                    return;
                }

                // A stalemate (two units that cannot reach each other) would never end on its own.
                if (runner.IsRunning && runner.Combat.TurnNumber > MaxTurnsPerFight && runner.Combat.CurrentState != CombatState.CombatEnd)
                {
                    tallies[current].Draws++;
                    tallies[current].Turns += runner.Combat.TurnNumber;

                    // Who was still up, so a stall can be told apart from a slow win.
                    tallies[current].Stalls.Add(string.Join(", ", runner.SpawnedUnits.Where(u => !u.IsKnockedOut && !u.IsFlatlined)
                        .Select(u => $"{u.DisplayName} {u.CurrentStats.HP}/{u.EffectiveStats.MaxHP} @{u.GridCoords}")));
                    runner.Retreat();
                }

                return;
            }

            if (runner.IsRunning)
            {
                return;
            }

            if (queue.Count == 0)
            {
                Stop(null);
                return;
            }

            (Scenario scenario, int run) = queue.Dequeue();
            current = scenario;
            MissionDefinition mission = scenario.Mission(run);
            List<RosterUnit> crew = scenario.Crew.Select((spec, i) => BuildCrew(spec, i)).ToList();

            if (!runner.StartMission(mission, crew, out string error))
            {
                Debug.LogWarning($"BalanceSim: {scenario.Name} run {run}: {error}");
                return;
            }

            foreach (GridUnit unit in runner.SpawnedUnits.Where(u => runner.GetRosterSource(u) != null))
            {
                GuestField.SetValue(unit, true);
            }

            fighting = true;
            runner.BeginBattle();
        }

        private static RosterUnit BuildCrew(CrewSpec spec, int index)
        {
            JobDefinition job = FindAsset<JobDefinition>(spec.Job);
            RosterUnit unit = new RosterUnit($"{spec.Job} {index + 1}", job, spec.Level);
            LearnEverything(unit, job);

            if (!string.IsNullOrEmpty(spec.Secondary))
            {
                unit.SecondaryJob = FindAsset<JobDefinition>(spec.Secondary);
                LearnEverything(unit, unit.SecondaryJob);
            }

            unit.Weapon = WeaponFor(job.DefaultWeapon != null ? job.DefaultWeapon.Type : WeaponType.None, spec.WeaponTier);

            // Passives from the primary job: the first of each kind, the cheap default a player
            // would reach first.
            unit.Reaction = job.AllAbilities().FirstOrDefault(a => a.Slot == AbilitySlot.Reaction);
            unit.Support = job.AllAbilities().FirstOrDefault(a => a.Slot == AbilitySlot.Support && a.EquipWeapon == WeaponType.None);
            unit.Movement = job.AllAbilities().FirstOrDefault(a => a.Slot == AbilitySlot.Movement);
            return unit;
        }

        private static void LearnEverything(RosterUnit unit, JobDefinition job)
        {
            foreach (AbilityDefinition ability in job.AllAbilities())
            {
                unit.MarkLearned(ability);
            }
        }

        public static EquipmentDefinition WeaponFor(WeaponType type, int tier)
        {
            if (type == WeaponType.None)
            {
                return null;
            }

            List<EquipmentDefinition> all = FindAll<EquipmentDefinition>().Where(e => e.Type == type).ToList();
            return all.FirstOrDefault(e => e.Tier == tier) ?? all.OrderBy(e => e.Tier).FirstOrDefault();
        }

        public static T FindAsset<T>(string name) where T : UnityEngine.Object
        {
            T found = FindAll<T>().FirstOrDefault(a => a.name == name);

            if (found == null)
            {
                throw new ArgumentException($"No {typeof(T).Name} named '{name}'.");
            }

            return found;
        }

        private static IEnumerable<T> FindAll<T>() where T : UnityEngine.Object
        {
            return AssetDatabase.FindAssets("t:" + typeof(T).Name)
                .Select(guid => AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(asset => asset != null);
        }

        private static void HandleAction(ActionOutcome outcome)
        {
            if (current == null || outcome.User == null || outcome.Ability == null)
            {
                return;
            }

            // A hacked unit joins the Player side as player-controlled; hand it to the AI too, or
            // the fight waits forever for orders on its turn.
            if (outcome.Effective && outcome.Ability.EffectType == AbilityEffectType.Override && outcome.Target != null
                && outcome.Target.CurrentFaction == Faction.Player)
            {
                GuestField.SetValue(outcome.Target, true);
            }

            Tally tally = tallies[current];
            string key = $"{(runner.GetRosterSource(outcome.User) != null ? "crew" : "foe")} {outcome.Ability.AbilityName}";
            tally.Uses[key] = tally.Uses.TryGetValue(key, out int uses) ? uses + 1 : 1;

            if (outcome.KnockedOut)
            {
                tally.Knockouts[key] = tally.Knockouts.TryGetValue(key, out int kos) ? kos + 1 : 1;
            }
        }

        private static void HandleFinished(MissionResult result)
        {
            Tally tally = tallies[current];
            List<MissionUnitReport> mine = result.Units.Where(u => current.Crew.Any(c => u.Name.StartsWith(c.Job + " "))).ToList();
            List<MissionUnitReport> foes = result.Units.Except(mine).Where(u => u.FinalFaction != Faction.Player || u.Hacked).ToList();

            switch (result.Outcome)
            {
                case MissionOutcome.Victory: tally.Wins++; break;
                case MissionOutcome.Defeat: tally.Losses++; break;
            }

            if (result.Outcome != MissionOutcome.Retreat)
            {
                tally.Turns += runner.Combat.TurnNumber;
            }

            tally.CrewCount += mine.Count;
            tally.CrewDown += mine.Count(u => u.KnockedOut || u.Flatlined);
            tally.FoeCount += foes.Count;
            tally.FoesDown += foes.Count(u => u.KnockedOut || u.Flatlined || u.Hacked);
            fighting = false;
        }

        private static void Stop(string reason)
        {
            EditorApplication.update -= Step;

            if (runner != null)
            {
                runner.OnMissionFinished -= HandleFinished;
                runner.Combat.Resolver.OnActionResolved -= HandleAction;
            }

            if (menu != null)
            {
                menu.enabled = true;
            }

            Time.timeScale = 1f;
            Application.runInBackground = wasRunningInBackground;
            Busy = false;
            current = null;
            System.IO.File.WriteAllText(ResultPath, Report(reason));
        }

        private static string Report(string reason)
        {
            StringBuilder text = new StringBuilder();

            if (reason != null)
            {
                text.AppendLine("STOPPED: " + reason);
            }

            foreach (Scenario scenario in order)
            {
                Tally t = tallies[scenario];
                int fights = t.Wins + t.Losses + t.Draws;
                text.AppendLine($"## {scenario.Name}");
                text.AppendLine($"crew: {string.Join(", ", scenario.Crew.Select(c => $"{c.Job} L{c.Level} Mk{c.WeaponTier}" + (c.Secondary != null ? "/" + c.Secondary : "")))}");
                text.AppendLine($"won {t.Wins}/{fights}, lost {t.Losses}, stalled {t.Draws}; avg turns {(fights > 0 ? t.Turns / (float)fights : 0):0.0}; " +
                                $"crew down {t.CrewDown}/{t.CrewCount}, foes down {t.FoesDown}/{t.FoeCount}");
                text.AppendLine("uses (KOs): " + string.Join(", ", t.Uses.OrderByDescending(p => p.Value)
                    .Select(p => $"{p.Key} {p.Value}" + (t.Knockouts.TryGetValue(p.Key, out int k) ? $" ({k})" : ""))));

                foreach (string stall in t.Stalls)
                {
                    text.AppendLine("stalled with: " + stall);
                }

                text.AppendLine();
            }

            return text.ToString();
        }
    }
}
