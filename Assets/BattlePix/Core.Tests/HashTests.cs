using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class HashTests
    {
        [Test]
        public void HashJob_MatchesHashManaged_On10RandomWorlds()
        {
            var rng = new Pcg32(2026, 11);
            for (int w = 0; w < 10; w++)
            {
                int width = 1 + rng.NextInt(600);
                int height = 1 + rng.NextInt(300);
                int nations = 1 + rng.NextInt(255);
                var cfg = TestUtil.Config(pixelsPerTick: rng.NextInt(200), nations: nations, width: width, height: height);
                ulong seed = ((ulong)rng.Next() << 32) | rng.Next();
                using (var sim = new Simulation(cfg, seed, 1 + rng.NextInt(300)))
                {
                    var terrain = sim.World.TerrainRW;
                    for (int i = 0; i < terrain.Length; i++) terrain[i] = (byte)rng.NextInt(256);
                    var nationsRw = sim.World.NationsRW;
                    for (int i = 0; i < nationsRw.Length; i++)
                    {
                        var n = nationsRw[i];
                        n.Alive = (byte)rng.NextInt(2);
                        nationsRw[i] = n;
                    }
                    TestUtil.Run(sim, rng.NextInt(400));
                    Assert.AreEqual(HashManaged.Compute(sim.World), StateHash.Compute(sim.World),
                        $"world {w} {width}x{height}");
                }
            }
        }

        [Test]
        public void Hash_ChangesWhenAnyPartChanges()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                ulong h0 = sim.ComputeHash();
                var terrain = sim.World.TerrainRW;
                terrain[5] = 1;
                ulong h1 = sim.ComputeHash();
                Assert.AreNotEqual(h0, h1);
                sim.World.Rng.Get(RngStream.Events).Next();
                Assert.AreNotEqual(h1, sim.ComputeHash());
            }
        }

        [Test]
        public void Fnv1a64_KnownVector()
        {
            // FNV-1a 64 от строки "a" = 0xaf63dc4c8601ec8c (эталон FNV).
            Assert.AreEqual(0xaf63dc4c8601ec8cUL, Fnv1a64.Add(Fnv1a64.Offset, (byte)97));
        }
    }
}
