using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Acceptance 4: генерация на сидах 1–100.</summary>
    public class MapGenTests
    {
        [Test]
        public void Seeds1To100_LandShare_OneContinent_Capitals_Flags_Time()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            int retryFail = 0, unfair = 0;
            double worstMs = 0, totalMs = 0;
            using (new Simulation(config, rules, 0, 0, 0)) { } // прогрев JIT: в замер не входит
            for (ulong seed = 1; seed <= 100; seed++)
            {
                var sw = Stopwatch.StartNew();
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    totalMs += ms;
                    if (ms > worstMs) worstMs = ms;
                    var w = sim.World;
                    if (w.MapGen.MapRetryFail) retryFail++;
                    if (w.MapGen.UnfairStart) unfair++;
                    CheckWorld(w, seed);
                }
            }
            TestContext.WriteLine($"mapgen: avg {totalMs / 100:0.0} ms, worst {worstMs:0.0} ms, retryFail {retryFail}, unfair {unfair}");
            Assert.LessOrEqual(retryFail, 5, "mapRetryFail in more than 5% of seeds");
            Assert.LessOrEqual(unfair, 5, "unfairStart in more than 5% of seeds");
            Assert.LessOrEqual(worstMs, 200, $"generation too slow: worst {worstMs:0.0} ms");
        }

        static void CheckWorld(World w, ulong seed)
        {
            int n = w.PixelCount, land = 0, hills = 0, mountains = 0;
            for (int i = 0; i < n; i++)
            {
                if (w.IsLand(i)) land++;
                if (w.Terrain[i] == (byte)Terrain.Hills) hills++;
                if (w.Terrain[i] == (byte)Terrain.Mountains) mountains++;
                Assert.LessOrEqual(w.Terrain[i], (byte)Terrain.Mountains, $"seed {seed}: unknown terrain at {i}");
            }
            // Спека 01b, acceptance 3: горы 9–11%, холмы 24–26% суши.
            Assert.That((long)mountains * 10000 / land, Is.InRange(900, 1100), $"seed {seed}: mountains share");
            Assert.That((long)hills * 10000 / land, Is.InRange(2400, 2600), $"seed {seed}: hills share");
            Assert.AreEqual(land, w.TotalLand, $"seed {seed}: TotalLand");
            long share = (long)land * 10000 / n;
            Assert.That(share, Is.InRange(4500, 4900), $"seed {seed}: land share {share} bp");
            Assert.AreEqual(land, ComponentSize(w, FirstLand(w)), $"seed {seed}: land is not one continent");

            var caps = new HashSet<int>();
            var pixels = new int[w.NationCount + 1];
            for (int i = 0; i < n; i++)
            {
                if (!w.IsLand(i)) Assert.AreEqual(0, w.Owner[i], $"seed {seed}: water pixel {i} has an owner");
                pixels[w.Owner[i]]++;
            }
            Assert.AreEqual(8, w.NationCount);
            for (int k = 0; k < w.NationCount; k++)
            {
                var nat = w.Nations[k];
                Assert.AreEqual(1, nat.Alive);
                Assert.IsTrue(caps.Add(nat.Capital), $"seed {seed}: duplicate capital");
                Assert.IsTrue(w.IsLand(nat.Capital), $"seed {seed}: capital on water");
                Assert.AreNotEqual((byte)Terrain.Mountains, w.Terrain[nat.Capital], $"seed {seed}: capital {nat.Id} in mountains");
                Assert.AreEqual(nat.Id, w.Owner[nat.Capital], $"seed {seed}: capital not owned");
                Assert.GreaterOrEqual(WaterDistanceSq(w, nat.Capital), 36, $"seed {seed}: capital {nat.Id} closer than 6 px to water");
                Assert.AreEqual(pixels[nat.Id], nat.Pixels, $"seed {seed}: Pixels of {nat.Id}");
                Assert.Greater(nat.Pixels, 100, $"seed {seed}: start spot of {nat.Id} too small");
                Assert.AreEqual(30000, nat.Gold);
                Assert.AreEqual(1, nat.Divisions);
                Assert.AreEqual(-1, nat.DiedAtTick);
            }
        }

        static int FirstLand(World w)
        {
            for (int i = 0; i < w.PixelCount; i++) if (w.IsLand(i)) return i;
            return -1;
        }

        static int ComponentSize(World w, int start)
        {
            var seen = new bool[w.PixelCount];
            var q = new Queue<int>();
            q.Enqueue(start);
            seen[start] = true;
            int count = 0;
            while (q.Count > 0)
            {
                int p = q.Dequeue();
                count++;
                int x = p % w.Width, y = p / w.Width;
                void Visit(int qx, int qy)
                {
                    if (!w.InMap(qx, qy)) return;
                    int i = w.Index(qx, qy);
                    if (seen[i] || !w.IsLand(i)) return;
                    seen[i] = true;
                    q.Enqueue(i);
                }
                Visit(x, y - 1);
                Visit(x + 1, y);
                Visit(x, y + 1);
                Visit(x - 1, y);
            }
            return count;
        }

        /// <summary>Квадрат евклидова расстояния до ближайшей воды (край карты — вода), до 10 px.</summary>
        static int WaterDistanceSq(World w, int p)
        {
            int cx = p % w.Width, cy = p / w.Width, best = int.MaxValue;
            for (int dy = -10; dy <= 10; dy++)
            for (int dx = -10; dx <= 10; dx++)
            {
                int x = cx + dx, y = cy + dy, d2 = dx * dx + dy * dy;
                bool water = !w.InMap(x, y) || !w.IsLand(w.Index(x, y));
                if (water && d2 < best) best = d2;
            }
            return best;
        }

        [Test]
        public void Generation_IsDeterministic_AndSeedsDiffer()
        {
            using (var a = TestData.Match(7))
            using (var b = TestData.Match(7))
            using (var c = TestData.Match(8))
            {
                Assert.AreEqual(a.ComputeHash(), b.ComputeHash());
                Assert.AreNotEqual(a.ComputeHash(), c.ComputeHash());
            }
        }

        [Test]
        public void OddMapSize_And2To12Nations_Work()
        {
            var config = TestData.Config();
            config.Width = 333;
            config.Height = 177;
            foreach (int nations in new[] { 2, 5, 12 })
            {
                var rules = Rules.Default(nations);
                using (var sim = new Simulation(config, rules, 3, 0, 0))
                {
                    Assert.AreEqual(nations, sim.World.NationCount);
                    var caps = new HashSet<int>();
                    for (int k = 0; k < nations; k++) Assert.IsTrue(caps.Add(sim.World.Nations[k].Capital));
                    TestUtil.Run(sim, 50);
                }
            }
        }

        /// <summary>Рельеф не трогает воду и сушу: меняются только значения суши, по порогам корзин.</summary>
        [Test]
        public void Relief_KeepsLandMask_AndOrdersBandsByHeight()
        {
            var mg = new MapGenRules { HillsShare_bp = 2500, MountainsShare_bp = 1000 };
            int n = 4000;
            var bin = new int[n];
            var land = new byte[n];
            var rng = new Pcg32(5, 1);
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                bin[i] = rng.NextInt(1024);
                land[i] = bin[i] >= 300 ? (byte)Terrain.Land : (byte)Terrain.Water;
                if (land[i] != 0) count++;
            }
            var before = (byte[])land.Clone();
            MapGenerator.Relief(mg, bin, land, count);
            int maxPlain = -1, minHill = int.MaxValue, maxHill = -1, minMountain = int.MaxValue, m = 0, h = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(before[i] == 0, land[i] == 0, $"pixel {i}: land mask changed");
                if (land[i] == (byte)Terrain.Land) maxPlain = System.Math.Max(maxPlain, bin[i]);
                if (land[i] == (byte)Terrain.Hills) { h++; minHill = System.Math.Min(minHill, bin[i]); maxHill = System.Math.Max(maxHill, bin[i]); }
                if (land[i] == (byte)Terrain.Mountains) { m++; minMountain = System.Math.Min(minMountain, bin[i]); }
            }
            Assert.Less(maxPlain, minHill);
            Assert.Less(maxHill, minMountain);
            Assert.That((long)m * 10000 / count, Is.InRange(990, 1100));
            Assert.That((long)(m + h) * 10000 / count, Is.InRange(3490, 3600));
        }

        [Test]
        public void Smooth_IsFixedPointSmoothstep()
        {
            Assert.AreEqual(0, MapGenerator.Smooth(0));
            Assert.AreEqual(512, MapGenerator.Smooth(512));
            Assert.AreEqual(1024, MapGenerator.Smooth(1024));
        }
    }
}
