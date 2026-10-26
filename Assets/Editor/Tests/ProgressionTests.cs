using NUnit.Framework;
using Tactica.Stats;

namespace Tactica.Tests
{
    // JP, job levels and XP - GAME_DESIGN.md "JP ve sınıf seviyesi".
    public class ProgressionTests
    {
        [TestCase(0, 0)]
        [TestCase(99, 0)]
        [TestCase(100, 1)]
        [TestCase(349, 2)]
        [TestCase(350, 3)]
        [TestCase(2100, 8)]
        [TestCase(99999, 8)]
        public void JobLevelFollowsTheThresholdTable(int lifetimeJP, int expectedLevel)
        {
            Assert.AreEqual(expectedLevel, Progression.JobLevelFor(lifetimeJP));
        }

        [Test]
        public void JPToNextLevelCountsDownAndStopsAtTheCap()
        {
            Assert.AreEqual(100, Progression.JPToNextJobLevel(0));
            Assert.AreEqual(1, Progression.JPToNextJobLevel(349));
            Assert.AreEqual(0, Progression.JPToNextJobLevel(Progression.JobLevelThresholds[Progression.MaxJobLevel]));
        }

        // [(8 + 2 x job level + [character level / 4]) x M]
        [TestCase(0, 1)]
        [TestCase(0, 4)]
        [TestCase(3, 7)]
        [TestCase(8, 20)]
        public void JPPerActionUsesTheDesignFormula(int jobLevel, int characterLevel)
        {
            int expected = (8 + 2 * jobLevel + characterLevel / 4) * Progression.JPMultiplier;
            Assert.AreEqual(expected, Progression.JPForAction(jobLevel, characterLevel));
        }

        [Test]
        public void XPRewardsHigherTargetsAndKnockouts()
        {
            Assert.AreEqual(10, Progression.XPForAction(5, 5, false));
            Assert.AreEqual(13, Progression.XPForAction(5, 8, false));
            Assert.AreEqual(10 + Progression.KnockoutXPBonus, Progression.XPForAction(5, 5, true));
        }

        [Test]
        public void XPNeverDropsBelowOne()
        {
            Assert.AreEqual(1, Progression.XPForAction(50, 1, false));
        }
    }
}
