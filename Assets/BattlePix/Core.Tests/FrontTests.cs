using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Acceptance 3: векторы Pcg32.Hash посчитаны отдельной эталонной реализацией pcg_hash (Python, 32-битная арифметика).</summary>
    public class PcgHashTests
    {
        [Test]
        public void Hash_MatchesReferenceVectors()
        {
            Assert.AreEqual(129708002u, Pcg32.Hash(0));
            Assert.AreEqual(2831084092u, Pcg32.Hash(1));
            Assert.AreEqual(1223963391u, Pcg32.Hash(42));
            Assert.AreEqual(3861530882u, Pcg32.Hash(0xFFFFFFFFu));
        }

        [Test]
        public void FrontRoll_MatchesReferenceVectors()
        {
            // roll = Hash(salt ^ Hash(t * 4 + k)) % 10000
            Assert.AreEqual(2453, Pcg32.FrontRoll(0xDEADBEEFu, 1000, 0));
            Assert.AreEqual(4989, Pcg32.FrontRoll(0xDEADBEEFu, 1000, 1));
            Assert.AreEqual(4561, Pcg32.FrontRoll(0xDEADBEEFu, 1000, 2));
            Assert.AreEqual(1810, Pcg32.FrontRoll(0xDEADBEEFu, 1000, 3));
            Assert.AreEqual(9070, Pcg32.FrontRoll(0u, 0, 0));
        }
    }

    /// <summary>Acceptance 5: таблица из раздела 6 спеки.</summary>
    public class FrontExampleTests
    {
        // Пример раздела 6 посчитан на стартовых числах спеки (гарнизон 10, столица ×2, кап ×4), а не на текущих данных.
        static readonly FrontRules Fr = new FrontRules { GarrisonPerEdge = 10, CapitalMult_bp = 20000, DefenseMultCap_bp = 40000 };

        static void Row(int att, int def, int expectRatio, int expectChance)
        {
            int chance = FrontMath.Chance(att, def, Fr.WarChance_bp, Fr.StallRatio_bp, Fr.MaxChance_bp, out int ratio);
            Assert.AreEqual(expectRatio, ratio, $"ratio for {att}/{def}");
            Assert.AreEqual(expectChance, chance, $"chance for {att}/{def}");
        }

        [Test]
        public void Section6_Table()
        {
            var r = Rules.Default(8);
            r.Front.GarrisonPerEdge = Fr.GarrisonPerEdge;
            Assert.AreEqual(4800, FrontMath.ArmyOnFront(12, 120, 300));
            Assert.AreEqual(6000, FrontMath.ArmyOnFront(8, 120, 160));
            int pow12 = FrontMath.Pow(12, 120, 300, Fr.GarrisonPerEdge);
            int pow21 = FrontMath.Pow(8, 120, 160, Fr.GarrisonPerEdge);
            Assert.AreEqual(50, pow12);
            Assert.AreEqual(60, pow21);

            int strike = FrontMath.MulBp(pow12, r.Strikes.StrikeMult_bp);
            int fortDef = FrontMath.MulBp(pow21, r.Forts.FortMult_bp);
            int both = FrontMath.MulBp(FrontMath.MulBp(10000, r.Forts.FortMult_bp), Fr.CapitalMult_bp);
            Assert.AreEqual(60000, both);
            both = both > Fr.DefenseMultCap_bp ? Fr.DefenseMultCap_bp : both;
            int capDef = FrontMath.MulBp(pow21, both);

            Row(50, 60, 8333, 5);           // без всего
            Row(strike, 60, 25000, 306);    // удар Красных
            Row(50, fortDef, 2777, 0);      // синяя крепость
            Row(strike, fortDef, 8333, 5);  // удар против крепости
            Row(strike, capDef, 6250, 0);   // крепость + столица, кап ×4
            Row(60, 50, 12000, 72);         // Синие по красному пикселю
            Assert.AreEqual(150, strike);
            Assert.AreEqual(180, fortDef);
            Assert.AreEqual(240, capDef);
        }
    }

    /// <summary>Acceptance 6: фаза A на искусственной паре с фиксированной солью.</summary>
    public class FrontPhaseATests
    {
        // Карта 4×2, вся суша: x < 2 — Красные (1), x ≥ 2 — Синие (2). 12 и 8 батальонов, столиц нет.
        // FrontLen[1,2] = 2, warLen у обоих 2 → pow(1,2) = 12×1000×2/2/2 + 10 = 6010, pow(2,1) = 4010.
        // Красные по синему: ratio = 6010×10000/4010 = 14987, chance = 180×6987/10000 = 125; с ударом ×3 — 665.
        // Синие по красному: ratio 6672 < stall → 0.
        // Frontier = [1, 2, 5, 6]. У пикселей 2 и 6 красный сосед — W (k = 3).
        // Броски (эталонный pcg_hash): salt 326 → roll(2,3) = 21, roll(6,3) = 542; salt 27 → 129 и 104.
        static World Make(out FrontSystem front, bool strike)
        {
            var rules = Rules.Default(2);
            rules.Front.SurroundMult_bp = new[] { 10000, 10000, 10000, 10000 }; // формула 5.4 как в спеке
            rules.Front.GarrisonPerEdge = 10;                                   // и стартовый гарнизон спеки
            var world = new World(4, 2, 2, 1, rules);
            var terrain = world.TerrainRW;
            var owner = world.OwnerRW;
            for (int i = 0; i < 8; i++)
            {
                terrain[i] = (byte)Terrain.Land;
                owner[i] = (byte)(i % 4 < 2 ? 1 : 2);
            }
            var nations = world.NationsRW;
            nations[0] = new NationState { Id = 1, Alive = 1, Capital = -1, Pixels = 4, Battalions = 12, DiedAtTick = -1 };
            nations[1] = new NationState { Id = 2, Alive = 1, Capital = -1, Pixels = 4, Battalions = 8, DiedAtTick = -1 };
            if (strike) world.StrikeUntilRW[0 * 2 + 1] = 200;
            FrontSystem.RebuildFrontier(world);
            FrontSystem.RecountFrontLen(world);
            front = new FrontSystem { SingleThreaded = true };
            return world;
        }

        static void Check(uint salt, bool strike, byte[] expected)
        {
            var world = Make(out var front, strike);
            try
            {
                CollectionAssert.AreEqual(new[] { 1, 2, 5, 6 }, world.Frontier.ToArray());
                Assert.AreEqual(2, world.FrontLength(1, 2));
                Assert.AreEqual(6010, world.Pow(1, 2));
                Assert.AreEqual(4010, world.Pow(2, 1));
                Assert.AreEqual(21, Pcg32.FrontRoll(326, 2, 3));
                Assert.AreEqual(542, Pcg32.FrontRoll(326, 6, 3));
                CollectionAssert.AreEqual(expected, front.PhaseAForTest(world, salt), $"salt {salt}, strike {strike}");
            }
            finally
            {
                front.Dispose();
                world.Dispose();
            }
        }

        [Test] public void Salt326_NoStrike_OnlyPixel2Flips() => Check(326, false, new byte[] { 0, 1, 0, 0 });
        [Test] public void Salt326_Strike_BothFlip() => Check(326, true, new byte[] { 0, 1, 0, 1 });
        [Test] public void Salt27_NoStrike_OnlyPixel6Flips() => Check(27, false, new byte[] { 0, 0, 0, 1 });
    }

    /// <summary>Множитель окружения: анклав в кольце врага съедается быстрее.</summary>
    public class SurroundTests
    {
        // 3×3, вся суша, центр (4) — Синие, остальное — Красные. По 3 батальона, столиц нет.
        // FrontLen[1,2] = 4, warLen = 4 у обоих → pow = 3000/4 + 10 = 760 у обоих, ratio 1,0 → chance 36.
        // У центра четыре красных соседа: ×4 → 144. Соль 115: roll(4, N) = 97.
        static byte[] Decide(int[] surround)
        {
            var rules = Rules.Default(2);
            rules.Front.SurroundMult_bp = surround;
            rules.Front.GarrisonPerEdge = 10;
            var world = new World(3, 3, 2, 1, rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                for (int i = 0; i < 9; i++)
                {
                    world.TerrainRW[i] = (byte)Terrain.Land;
                    world.OwnerRW[i] = (byte)(i == 4 ? 2 : 1);
                }
                world.NationsRW[0] = new NationState { Id = 1, Alive = 1, Capital = -1, Pixels = 8, Battalions = 3, DiedAtTick = -1 };
                world.NationsRW[1] = new NationState { Id = 2, Alive = 1, Capital = -1, Pixels = 1, Battalions = 3, DiedAtTick = -1 };
                FrontSystem.RebuildFrontier(world);
                FrontSystem.RecountFrontLen(world);
                Assert.AreEqual(760, world.Pow(1, 2));
                Assert.AreEqual(97, Pcg32.FrontRoll(115, 4, 0));
                var frontier = world.Frontier.ToArray();
                var dec = front.PhaseAForTest(world, 115);
                return new[] { dec[System.Array.IndexOf(frontier, 4)] };
            }
            finally
            {
                front.Dispose();
                world.Dispose();
            }
        }

        [Test] public void Enclave_EatenWithSurroundMultiplier() => CollectionAssert.AreEqual(new byte[] { 1 }, Decide(new[] { 5000, 10000, 20000, 40000 }));
        [Test] public void Enclave_SurvivesWithoutMultiplier() => CollectionAssert.AreEqual(new byte[] { 0 }, Decide(new[] { 10000, 10000, 10000, 10000 }));
    }

    public class FrontSimulationTests
    {
        /// <summary>Acceptance 7: один поток и все потоки дают одинаковый хеш на сидах 1–10 через 3 000 тиков.</summary>
        [Test]
        public void FrontJob_SingleVsMultiThread_SameHash()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            for (ulong seed = 1; seed <= 10; seed++)
            {
                ulong single, multi;
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    sim.Pipeline.Get<FrontSystem>().SingleThreaded = true;
                    TestUtil.Run(sim, 3000);
                    single = sim.ComputeHash();
                }
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    TestUtil.Run(sim, 3000);
                    multi = sim.ComputeHash();
                }
                Assert.AreEqual(single, multi, $"seed {seed}");
            }
        }

        /// <summary>Acceptance 8: инкрементальные FortCover, FrontLen, Frontier и Pixels совпадают с пересчётом с нуля каждые 600 тиков.</summary>
        [Test]
        public void DerivedStateMatches()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            for (ulong seed = 1; seed <= 10; seed++)
            {
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    for (int block = 0; block < 10 && !sim.World.IsOver; block++)
                    {
                        TestUtil.Run(sim, 600);
                        Derived.AssertMatches(sim.World, $"seed {seed}, tick {sim.World.Tick}");
                    }
                }
            }
        }

        [Test]
        public void Expansion_GrowsNations_AndTakesNoWater()
        {
            using (var sim = TestData.Match(5))
            {
                var start = new int[sim.World.NationCount];
                for (int i = 0; i < start.Length; i++) start[i] = sim.World.Nations[i].Pixels;
                TestUtil.Run(sim, 1200);
                for (int i = 0; i < start.Length; i++)
                    Assert.Greater(sim.World.Nations[i].Pixels, start[i], $"nation {i + 1} did not expand");
                for (int p = 0; p < sim.World.PixelCount; p++)
                    if (!sim.World.IsLand(p)) Assert.AreEqual(0, sim.World.Owner[p]);
            }
        }
    }

    /// <summary>Acceptance 15 и падение крепости.</summary>
    public class NationDeathTests
    {
        // Ряд 7×1: [1 1 2* 2 2 3 3], * — столица Синих (2). Шанс везде 100%: каждая атака проходит.
        static World Make(Rules rules)
        {
            var world = new World(7, 1, 3, 1, rules);
            byte[] owners = { 1, 1, 2, 2, 2, 3, 3 };
            var terrain = world.TerrainRW;
            var owner = world.OwnerRW;
            for (int i = 0; i < 7; i++)
            {
                terrain[i] = (byte)Terrain.Land;
                owner[i] = owners[i];
            }
            var nations = world.NationsRW;
            nations[0] = new NationState { Id = 1, Alive = 1, Capital = 0, Pixels = 2, Battalions = 3, DiedAtTick = -1 };
            nations[1] = new NationState { Id = 2, Alive = 1, Capital = 2, Pixels = 3, Battalions = 3, Gold = 500, DiedAtTick = -1 };
            nations[2] = new NationState { Id = 3, Alive = 1, Capital = 6, Pixels = 2, Battalions = 3, DiedAtTick = -1 };
            Fortifications.StampCapitals(world);
            FrontSystem.RebuildFrontier(world);
            FrontSystem.RecountFrontLen(world);
            return world;
        }

        static Rules AlwaysWin()
        {
            var rules = Rules.Default(3);
            rules.Front.WarChance_bp = 1000000;
            rules.Front.StallRatio_bp = 0;
            rules.Front.MaxChance_bp = 10000;
            rules.Front.CapitalRadius = 0;
            rules.Forts.FortRadius = 1;
            rules.Front.SurroundMult_bp = new[] { 10000, 10000, 10000, 10000 };
            return rules;
        }

        [Test]
        public void CapitalFalls_LandBecomesNoMans_FortsRemoved_SameTickCapturesStay()
        {
            var rules = AlwaysWin();
            var world = Make(rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                // Крепость Синих на пикселе 3.
                world.FortsRW[1 * rules.Forts.MaxFortsPerNation] = new Fort { Pixel = 3, Owner = 2, Alive = 1 };
                var n2 = world.NationsRW[1];
                n2.FortsAlive = 1;
                world.NationsRW[1] = n2;
                Fortifications.Stamp(world, 3, 2, true);
                world.StrikeUntilRW[1 * 3 + 0] = 500; // удар Синих по Красным
                world.StrikeUntilRW[0 * 3 + 1] = 500; // удар Красных по Синим

                front.Tick(world, new CommandContext(TestData.Config(), rules, 0));

                // Красные взяли столицу (2), Зелёные — пиксель 4. Синие взяли 1 и 5, но пали: те пиксели ничьи.
                CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0, 3, 0, 3 }, world.Owner.ToArray());
                var dead = world.Nations[1];
                Assert.AreEqual(0, dead.Alive);
                Assert.AreEqual(-1, dead.Capital);
                Assert.AreEqual(0, dead.Pixels);
                Assert.AreEqual(0, dead.Gold);
                Assert.AreEqual(0, dead.Battalions);
                Assert.AreEqual(0, dead.FortsAlive);
                Assert.AreEqual(0, dead.DiedAtTick);
                Assert.AreEqual(0, world.Forts[1 * rules.Forts.MaxFortsPerNation].Alive);
                foreach (var c in world.FortCover.ToArray()) Assert.AreEqual(0, c & 2, "FortCover bit of the dead nation");
                Assert.AreEqual(0, world.StrikeUntil[1 * 3 + 0]);
                Assert.AreEqual(0, world.StrikeUntil[0 * 3 + 1]);
                Assert.AreEqual(2, world.Nations[0].Pixels);
                Assert.AreEqual(2, world.Nations[2].Pixels);
                Derived.AssertMatches(world, "after death");
            }
            finally
            {
                front.Dispose();
                world.Dispose();
            }
        }

        [Test]
        public void FortPixelCaptured_FortFalls_CoverRecomputed()
        {
            var rules = AlwaysWin();
            var world = Make(rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                // Без столицы под ударом: столица Синих — пиксель 3, крепость — на 2 (рядом с Красными).
                var n2 = world.NationsRW[1];
                n2.Capital = 3;
                n2.FortsAlive = 2;
                world.NationsRW[1] = n2;
                for (int i = 0; i < 7; i++) world.CapitalCoverRW[i] = 0;
                Fortifications.StampCapitals(world);
                int m = rules.Forts.MaxFortsPerNation;
                world.FortsRW[m + 0] = new Fort { Pixel = 2, Owner = 2, Alive = 1 };
                world.FortsRW[m + 1] = new Fort { Pixel = 4, Owner = 2, Alive = 1 };
                Fortifications.Stamp(world, 2, 2, true);
                Fortifications.Stamp(world, 4, 2, true);
                // Зелёных убираем с карты: их пиксели — вода.
                world.TerrainRW[5] = (byte)Terrain.Water;
                world.TerrainRW[6] = (byte)Terrain.Water;
                world.OwnerRW[5] = 0;
                world.OwnerRW[6] = 0;
                var n3 = world.NationsRW[2];
                n3.Pixels = 0;
                n3.Capital = -1;
                world.NationsRW[2] = n3;
                FrontSystem.RebuildFrontier(world);
                FrontSystem.RecountFrontLen(world);

                front.Tick(world, new CommandContext(TestData.Config(), rules, 0));

                CollectionAssert.AreEqual(new byte[] { 1, 2, 1, 2, 2, 0, 0 }, world.Owner.ToArray());
                Assert.AreEqual(0, world.Forts[m + 0].Alive);
                Assert.AreEqual(1, world.Forts[m + 1].Alive);
                Assert.AreEqual(1, world.Nations[1].FortsAlive);
                Assert.AreEqual(1, world.Nations[1].Alive);
                Derived.AssertMatches(world, "after fort fall");
            }
            finally
            {
                front.Dispose();
                world.Dispose();
            }
        }
    }

    /// <summary>Пересчёт производного состояния с нуля.</summary>
    static class Derived
    {
        public static void AssertMatches(World w, string where)
        {
            int n = w.PixelCount, width = w.Width, height = w.Height, stride = w.NationCount + 1;

            var pixels = new int[stride];
            for (int p = 0; p < n; p++) pixels[w.Owner[p]]++;
            for (int k = 0; k < w.NationCount; k++)
                Assert.AreEqual(pixels[k + 1], w.Nations[k].Pixels, $"{where}: Pixels of {k + 1}");

            var frontLen = new int[stride * stride];
            for (int p = 0; p < n; p++)
            {
                if (!w.IsLand(p)) continue;
                int x = p % width, y = p / width, a = w.Owner[p];
                if (x + 1 < width) Edge(p + 1);
                if (y > 0) Edge(p - width);
                void Edge(int q)
                {
                    if (!w.IsLand(q) || w.Owner[q] == a) return;
                    frontLen[a * stride + w.Owner[q]]++;
                    frontLen[w.Owner[q] * stride + a]++;
                }
            }
            CollectionAssert.AreEqual(frontLen, w.FrontLen.ToArray(), $"{where}: FrontLen");

            // Frontier: удаление ленивое, поэтому в списке могут остаться устаревшие записи.
            // Сверяем: без дублей, у каждой записи InFrontier = 1, и каждый настоящий пограничный пиксель в списке.
            var inList = new HashSet<int>();
            foreach (var p in w.Frontier.ToArray())
            {
                Assert.IsTrue(inList.Add(p), $"{where}: duplicate frontier entry {p}");
                Assert.AreEqual(1, w.InFrontier[p], $"{where}: InFrontier of listed {p}");
            }
            var owner = w.Owner.ToArray();
            var terrain = w.Terrain.ToArray();
            for (int p = 0; p < n; p++)
            {
                bool f = IsFrontier(p, owner, terrain, width, height);
                if (f) Assert.IsTrue(inList.Contains(p), $"{where}: frontier pixel {p} missing");
                if (!inList.Contains(p)) Assert.AreEqual(0, w.InFrontier[p], $"{where}: InFrontier of unlisted {p}");
            }

            CollectionAssert.AreEqual(Fortifications.Recompute(w), w.FortCover.ToArray(), $"{where}: FortCover");
        }

        static bool IsFrontier(int p, byte[] owner, byte[] terrain, int w, int h)
        {
            if (terrain[p] != 1) return false;
            int x = p % w, y = p / w;
            bool Diff(int q) => terrain[q] == 1 && owner[q] != owner[p];
            return (y + 1 < h && Diff(p + w)) || (x + 1 < w && Diff(p + 1)) || (y > 0 && Diff(p - w)) || (x > 0 && Diff(p - 1));
        }
    }
}
