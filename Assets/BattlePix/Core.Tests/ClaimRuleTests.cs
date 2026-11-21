using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Правка 02b: запрещён только район, в который попадает пиксель столицы; зона столицы не запрещает.</summary>
    public class ClaimRuleTests
    {
        [Test]
        public void CapitalZone_Allowed_CapitalPixel_Forbidden()
        {
            // 100×80: A (1) x < 40, D (2) x ≥ 40, столица D (45, 40) — у самой границы. capitalRadius 28, claimRadius 18.
            using (var w = WarWorlds.Make(100, 80, 2, (x, y) => x < 40 ? (byte)1 : (byte)2, new[] { (5, 40), (45, 40) }))
            {
                int inZone = 40 * 100 + 70; // 25 px от столицы: в зоне столицы, но столица не в круге
                Assert.AreNotEqual(0, w.CapitalCover[inZone] & 2, "the center is inside the capital zone");
                Assert.AreEqual(ClaimCheck.Ok, WarSystem.CheckClaim(w, 1, 2, inZone, out int total));
                Assert.GreaterOrEqual(total, 300);
                int overCapital = 40 * 100 + 55; // 10 px от столицы: столица в круге
                Assert.AreEqual(ClaimCheck.TouchesCapital, WarSystem.CheckClaim(w, 1, 2, overCapital, out _));
            }
        }

        [Test]
        public void AiCenter_IsValid_AndFarthestFromCapital()
        {
            using (var w = WarWorlds.Make(100, 80, 2, (x, y) => x < 40 ? (byte)1 : (byte)2, new[] { (5, 40), (45, 40) }))
            {
                int c = WarSystem.SuggestClaimCenter(w, 1, 2);
                Assert.GreaterOrEqual(c, 0);
                Assert.AreEqual(ClaimCheck.Ok, WarSystem.CheckClaim(w, 1, 2, c, out _));
                Assert.AreEqual(40, c % 100, "a border pixel on D's side");
                int dy = c / 100 - 40;
                Assert.GreaterOrEqual(dy * dy, 30 * 30, "far from the capital row");
            }
        }
    }
}
