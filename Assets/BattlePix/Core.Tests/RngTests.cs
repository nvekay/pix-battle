using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class RngTests
    {
        [Test]
        public void Pcg32_MatchesPcgCReferenceVector()
        {
            var rng = new Pcg32(42, 54);
            uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };
            foreach (var e in expected) Assert.AreEqual(e, rng.Next());
        }

        [Test]
        public void NextInt_StaysInBounds()
        {
            var rng = new Pcg32(7, 3);
            foreach (int bound in new[] { 1, 2, 3, 7, 255, 256, 511, 512, 1000003, int.MaxValue })
                for (int i = 0; i < 1000; i++)
                {
                    int v = rng.NextInt(bound);
                    Assert.That(v, Is.InRange(0, bound - 1));
                }
        }

        [Test]
        public void NextInt_RejectsNonPositiveBound()
        {
            var rng = new Pcg32(1, 1);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => rng.NextInt(-5));
        }

        [TestCase(0UL)]
        [TestCase(ulong.MaxValue)]
        [TestCase(42UL)]
        public void Streams_AreIndependentAndSeedable(ulong seed)
        {
            var a = new RngStreams(seed);
            var b = new RngStreams(seed);
            // Тянем из Debug в одном наборе: остальные потоки не должны сдвинуться.
            for (int i = 0; i < 100; i++) a.Get(RngStream.Debug).Next();
            Assert.AreEqual(b.Get(RngStream.Front).Next(), a.Get(RngStream.Front).Next());
            Assert.AreEqual(b.Get(RngStream.MapGen).Next(), a.Get(RngStream.MapGen).Next());
            Assert.AreNotEqual(new Pcg32(seed, 1).Next(), new Pcg32(seed, 2).Next());
        }

        [Test]
        public void StreamIds_AreFrozen()
        {
            Assert.AreEqual(1UL, (ulong)RngStream.MapGen);
            Assert.AreEqual(2UL, (ulong)RngStream.Front);
            Assert.AreEqual(3UL, (ulong)RngStream.Debug);
            Assert.AreEqual(4UL, (ulong)RngStream.Ai);
            Assert.AreEqual(5UL, (ulong)RngStream.Events);
        }
    }
}
