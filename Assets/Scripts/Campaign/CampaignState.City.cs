using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Tactica.Stats;

namespace Tactica.Campaign
{
    // The city map: which district the crew is in, which are open, how far the story has got,
    // and travelling between districts with the chance of a road encounter.
    // GAME_DESIGN.md "Dünya haritası".
    public partial class CampaignState
    {
        // Road encounter chance per point of the destination's danger, and its cap. A danger-5
        // district is ambushed three times in four; a quiet one rarely.
        public const float AmbushChancePerDanger = 0.15f;
        public const float MaxAmbushChance = 0.75f;

        // A district whose story is finished is quieter: half the ambushes on the way in.
        public const float ClearedAmbushFactor = 0.5f;

        // Trips that are safe after any fight - GAME_DESIGN.md's "savaştan sonra kısa bir koruma
        // süresi".
        public const int SafeTripsAfterAFight = 1;

        // Districts the crew has been to. The first trip into a district is never ambushed - its
        // first visit belongs to the story, not to a random fight.
        private readonly HashSet<string> visitedDistricts = new HashSet<string>();

        // Bumped after every mission; the job boards are rebuilt from it, so they change over time
        // without the generated contracts having to be saved.
        public int BoardVersion { get; private set; }

        public int SafeTrips { get; private set; }

        // The city map. The district is where the crew is; services and contracts come from it.
        private readonly HashSet<string> openDistricts = new HashSet<string>();
        private readonly HashSet<string> finishedStory = new HashSet<string>();

        public string CurrentDistrictName { get; private set; }

        public DistrictDefinition CurrentDistrict => config.FindDistrict(CurrentDistrictName);

        // Set while a road encounter is being fought: where the crew was heading. Winning gets
        // them there; losing or jacking out leaves them where they started. Not saved - the save
        // is only written between fights.
        public string TravelDestination { get; private set; }

        // A new game's map, also used to bring a pre-map save onto it.
        private void StartCity()
        {
            Credits = config.StartingCredits;
            openDistricts.Clear();

            foreach (DistrictDefinition district in config.Districts)
            {
                if (district != null && district.openFromStart)
                {
                    openDistricts.Add(district.districtName);
                }
            }

            CurrentDistrictName = config.StartingDistrict;

            if (config.FindDistrict(CurrentDistrictName) == null && config.Districts.Count > 0)
            {
                CurrentDistrictName = config.Districts[0].districtName;
            }

            if (!string.IsNullOrEmpty(CurrentDistrictName))
            {
                openDistricts.Add(CurrentDistrictName);
                visitedDistricts.Add(CurrentDistrictName);
            }
        }

        public bool HasVisited(string districtName)
        {
            return visitedDistricts.Contains(districtName);
        }

        // Every story mission here played. A district with no story is never "cleared".
        public bool IsCleared(DistrictDefinition district)
        {
            return district != null && district.story.Count > 0 && district.story.All(finishedStory.Contains);
        }

        // What the shop sells today: its stock, plus the cleared-district stock once the story is done.
        public List<EquipmentDefinition> ShopStock(DistrictDefinition district)
        {
            if (district == null)
            {
                return new List<EquipmentDefinition>();
            }

            IEnumerable<EquipmentDefinition> stock = district.shopStock;

            if (IsCleared(district))
            {
                stock = stock.Concat(district.clearedStock);
            }

            return stock.Where(item => item != null).Distinct().ToList();
        }

        public bool IsDistrictOpen(string name)
        {
            return openDistricts.Contains(name);
        }

        // The next unplayed story mission here, or null when the district's story is told.
        public MissionDefinition NextStoryMission(DistrictDefinition district)
        {
            return district?.story
                .Where(name => !finishedStory.Contains(name))
                .Select(config.FindMission)
                .FirstOrDefault(mission => mission != null);
        }

        // The district's authored contracts, then the generated ones for the current board version.
        // What generated fights scale to: the average level of the strongest crew that could deploy.
        public int CrewLevel
        {
            get
            {
                List<int> levels = Roster.Select(unit => unit.Level).OrderByDescending(level => level).Take(config.MaxDeployed).ToList();
                return levels.Count == 0 ? 1 : Mathf.RoundToInt((float)levels.Average());
            }
        }

        public List<MissionDefinition> Listings(DistrictDefinition district)
        {
            if (district == null)
            {
                return new List<MissionDefinition>();
            }

            List<MissionDefinition> listings = district.listings.Select(config.FindMission).Where(mission => mission != null).ToList();

            for (int i = 0; i < config.GeneratedListings; i++)
            {
                MissionDefinition generated = ContractGenerator.Generate(
                    district, config.EscortJob, BoardSeed(district, i), ContractGenerator.Purpose.Listing, CrewLevel);

                if (generated != null)
                {
                    listings.Add(generated);
                }
            }

            return listings;
        }

        private int BoardSeed(DistrictDefinition district, int slot)
        {
            unchecked
            {
                return ContractGenerator.StableHash(district.districtName) * 31 + BoardVersion * 7919 + slot * 104729;
            }
        }

        // Whether the road between two districts is a safe one, listed on either end.
        public static bool IsSafeRoad(DistrictDefinition from, DistrictDefinition to)
        {
            return from != null && to != null
                && (from.safeRoads.Contains(to.districtName) || to.safeRoads.Contains(from.districtName));
        }

        // The chance of an ambush on the way to `destination` from where the crew is now, and why it
        // is what it is (for the travel screen).
        public float AmbushChanceFor(DistrictDefinition destination, out string why)
        {
            why = null;

            if (destination == null) return 0f;

            if (IsSafeRoad(CurrentDistrict, destination)) { why = "safe road"; return 0f; }
            if (!HasVisited(destination.districtName)) { why = "first visit"; return 0f; }
            if (SafeTrips > 0) { why = "lying low after a fight"; return 0f; }

            float chance = AmbushChance(destination);

            if (IsCleared(destination))
            {
                why = "cleared";
                chance *= ClearedAmbushFactor;
            }

            return chance;
        }

        public List<DistrictDefinition> Connections(DistrictDefinition district)
        {
            return district == null
                ? new List<DistrictDefinition>()
                : district.connections.Select(config.FindDistrict).Where(next => next != null).ToList();
        }

        public static float AmbushChance(DistrictDefinition destination)
        {
            return Mathf.Min(MaxAmbushChance, destination.danger * AmbushChancePerDanger);
        }

        // Heads for a neighbouring open district. Returns the road encounter to fight first, or
        // null when the crew arrived without one. roll is 0..1 - passed in so a test can force
        // either outcome; the menu passes Random.value.
        public bool TryTravel(DistrictDefinition destination, float roll, out MissionDefinition ambush, out string reason)
        {
            ambush = null;
            reason = null;

            if (destination == null) reason = "No such district.";
            else if (destination.districtName == CurrentDistrictName) reason = "Already here.";
            else if (!Connections(CurrentDistrict).Contains(destination)) reason = "No road from here.";
            else if (!IsDistrictOpen(destination.districtName)) reason = "Locked - the story has not reached it yet.";

            if (reason != null)
            {
                return false;
            }

            float chance = AmbushChanceFor(destination, out _);

            // The safe period is a number of trips, used up whether or not it was needed.
            if (SafeTrips > 0)
            {
                SafeTrips--;
            }

            if (roll < chance)
            {
                ambush = RoadEncounter(destination, roll / chance);
            }

            if (ambush != null)
            {
                TravelDestination = destination.districtName;
                return true;
            }

            ArriveAt(destination.districtName);
            return true;
        }

        // Turning back from a road encounter before it starts.
        public void CancelTravel()
        {
            TravelDestination = null;
        }

        // A generated ambush from the destination's enemy pool, scaled by its danger; without a
        // pool, one of its authored templates. `pick` (0..1) chooses, so one roll decides the trip.
        private MissionDefinition RoadEncounter(DistrictDefinition destination, float pick)
        {
            unchecked
            {
                int seed = ContractGenerator.StableHash(destination.districtName) * 17 + BoardVersion * 31 + Mathf.FloorToInt(pick * 1000f);
                MissionDefinition generated = ContractGenerator.Generate(destination, config.EscortJob, seed, ContractGenerator.Purpose.Road, CrewLevel);

                if (generated != null)
                {
                    return generated;
                }
            }

            List<MissionDefinition> templates = destination.roadEncounters
                .Select(config.FindMission).Where(mission => mission != null).ToList();

            return templates.Count > 0
                ? templates[Mathf.Min(templates.Count - 1, Mathf.FloorToInt(pick * templates.Count))]
                : null;
        }

        private void ArriveAt(string districtName)
        {
            CurrentDistrictName = districtName;
            TravelDestination = null;
            visitedDistricts.Add(districtName);

            // A new district, a new hiring hall.
            RefreshRecruitPool();
        }
    }
}
