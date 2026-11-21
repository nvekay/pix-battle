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


    /// <summary>Сборка искусственных миров с соединениями для тестов.</summary>
    static class Worlds
    {
        /// <summary>Нация без соединений и батальонов: id, жива, столица, пиксели, золото.</summary>
        public static void Nation(World w, byte id, int capital, int pixels, int gold = 0)
        {
            w.NationsRW[id - 1] = new NationState { Id = id, Alive = 1, Capital = capital, Pixels = pixels, Gold = gold, DiedAtTick = -1 };
        }

        /// <summary>Соединение k нации на фронте front с приказом order и батальонами types (на 100%), на месте.</summary>
        public static int Formation(World w, byte owner, int k, byte front, byte order, params byte[] types)
        {
            FormationSystem.CreateFormation(w, owner, k);
            int slot = w.FormationSlot(owner, k);
            var f = w.FormationsRW[slot];
            f.Front = front;
            f.Order = order;
            w.FormationsRW[slot] = f;
            foreach (var t in types) FormationSystem.AddBattalion(w, slot, t);
            return slot;
        }

        public static byte[] Many(byte type, int count)
        {
            var r = new byte[count];
            for (int i = 0; i < count; i++) r[i] = type;
            return r;
        }

        public static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        public static void Recount(World w)
        {
            FrontSystem.RebuildFrontier(w);
            FrontSystem.RecountFrontLen(w);
        }

        /// <summary>
        /// Пример раздела 6 спеки 01b: карта 2×120, столбец 0 — Красные (1, равнина), столбец 1 — Синие (2):
        /// строки 0–71 равнина, 72–107 холмы, 108–119 горы. FrontLen[1,2] = 120, по рельефу синих 72 / 36 / 12.
        /// Красные: 1-е — 8 пехоты + 3 брони, наступление; если second — 2-е, 8 пехоты, наступление. Синие: 10 пехоты, оборона.
        /// </summary>
        public static World Section6(bool second, int gold = 1000000)
        {
            var rules = Rules.Default(2);
            var w = new World(2, 120, 2, 1, rules);
            for (int y = 0; y < 120; y++)
            {
                w.TerrainRW[y * 2] = (byte)Terrain.Land;
                w.TerrainRW[y * 2 + 1] = (byte)(y < 72 ? Terrain.Land : y < 108 ? Terrain.Hills : Terrain.Mountains);
                w.OwnerRW[y * 2] = 1;
                w.OwnerRW[y * 2 + 1] = 2;
            }
            Nation(w, 1, -1, 120, gold);
            Nation(w, 2, -1, 120, gold);
            Formation(w, 1, 0, 2, Orders.Attack, Concat(Many(UnitTypes.Infantry, 8), Many(UnitTypes.Armor, 3)));
            if (second) Formation(w, 1, 1, 2, Orders.Attack, Many(UnitTypes.Infantry, 8));
            Formation(w, 2, 0, 1, Orders.Defend, Many(UnitTypes.Infantry, 10));
            Recount(w);
            return w;
        }
    }

    /// <summary>Спека 01b, acceptance 4: обе таблицы раздела 6 через FillPow и FillTables, включая округления.</summary>
    public class FormationPowTests
    {
        static void Check(bool second, int[] att, int[] def, int[] chance)
        {
            var w = Worlds.Section6(second);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                Assert.AreEqual(120, w.FrontLength(1, 2));
                CollectionAssert.AreEqual(new[] { 72, 36, 12 }, new[] { w.FrontTerrainLength(1, 2, 0), w.FrontTerrainLength(1, 2, 1), w.FrontTerrainLength(1, 2, 2) });
                CollectionAssert.AreEqual(new[] { 120, 0, 0 }, new[] { w.FrontTerrainLength(2, 1, 0), w.FrontTerrainLength(2, 1, 1), w.FrontTerrainLength(2, 1, 2) });
                new FormationSystem().FillPow(w);
                for (int tr = 0; tr < 3; tr++)
                {
                    Assert.AreEqual(att[tr], w.AttPow(1, 2, tr), $"attPow tr {tr}");
                    Assert.AreEqual(def[tr], w.DefPow(2, 1, tr), $"defPow tr {tr}");
                    Assert.AreEqual(0, w.AttPow(2, 1, tr), "Blue defends: no attack");
                    Assert.AreEqual(chance[tr], front.ChanceForTest(w, 1, 2, 0, tr), $"chance tr {tr}");
                    Assert.AreEqual(0, front.ChanceForTest(w, 2, 1, 0, tr), "Blue takes nothing");
                }
            }
            finally
            {
                front.Dispose();
                w.Dispose();
            }
        }

        [Test] public void Table1_OneFormation() => Check(false, new[] { 151, 118, 86 }, new[] { 156, 195, 234 }, new[] { 30, 0, 0 });
        [Test] public void Table2_TwoFormations() => Check(true, new[] { 217, 185, 146 }, new[] { 156, 195, 234 }, new[] { 106, 26, 0 });

        [Test]
        public void CapitalFlag_MultipliesDefense_CappedByDefenseMultCap()
        {
            var w = Worlds.Section6(true);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                new FormationSystem().FillPow(w);
                // Столица ×3: def 156 → 468, ratio 217×10000/468 = 4636 < stall → 0.
                Assert.AreEqual(0, front.ChanceForTest(w, 1, 2, 1, 0));
                w.Rules.Front.CapitalMult_bp = 11000; // ×1,1: def 171, ratio 12690, chance 180×4690/10000 = 84
                Assert.AreEqual(84, front.ChanceForTest(w, 1, 2, 1, 0));
                w.Rules.Front.DefenseMultCap_bp = 10000; // кап ×1: как без столицы
                Assert.AreEqual(106, front.ChanceForTest(w, 1, 2, 1, 0));
            }
            finally
            {
                front.Dispose();
                w.Dispose();
            }
        }
    }

    /// <summary>
    /// Спека 01b, acceptance 5: фаза A на искусственной паре с рельефом и фиксированной солью.
    /// Карта 4×2, x &lt; 2 — Красные (1), x ≥ 2 — Синие (2). Синий пиксель 2 — равнина, 6 — холмы, остальное равнина.
    /// Красные: 2 пехоты + 1 броня, наступление; Синие: 3 пехоты, оборона. FrontLen = 2, гарнизон 40, окружение выключено.
    /// att100: равнина 2000 + 3380 = 5380, холмы 2000 + 2080 = 4080 → attPow 2690 / 2040.
    /// def100 Синих 4200 → (2100 + 40) × рельеф: 2140 / 2675. ratio: 12570 / 7626 → chance 82 / 0.
    /// Frontier = [1, 2, 5, 6], красный сосед у 2 и 6 — W (k = 3). Эталонный pcg_hash: соль 6947 → roll(2,3) = 28,
    /// roll(6,3) = 37 (на холмах бросок прошёл бы, но шанс 0); соль 27 → 129 и 104.
    /// </summary>
    public class FrontExampleTests
    {
        static byte[] Decide(uint salt)
        {
            var rules = Rules.Default(2);
            rules.Front.SurroundMult_bp = new[] { 10000, 10000, 10000, 10000 };
            var w = new World(4, 2, 2, 1, rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    w.TerrainRW[i] = (byte)(i == 6 ? Terrain.Hills : Terrain.Land);
                    w.OwnerRW[i] = (byte)(i % 4 < 2 ? 1 : 2);
                }
                Worlds.Nation(w, 1, -1, 4);
                Worlds.Nation(w, 2, -1, 4);
                Worlds.Formation(w, 1, 0, 2, Orders.Attack, UnitTypes.Infantry, UnitTypes.Infantry, UnitTypes.Armor);
                Worlds.Formation(w, 2, 0, 1, Orders.Defend, Worlds.Many(UnitTypes.Infantry, 3));
                Worlds.Recount(w);
                new FormationSystem().FillPow(w);
                CollectionAssert.AreEqual(new[] { 1, 2, 5, 6 }, w.Frontier.ToArray());
                Assert.AreEqual(2690, w.AttPow(1, 2, 0));
                Assert.AreEqual(2040, w.AttPow(1, 2, 1));
                Assert.AreEqual(2140, w.DefPow(2, 1, 0));
                Assert.AreEqual(2675, w.DefPow(2, 1, 1));
                Assert.AreEqual(82, front.ChanceForTest(w, 1, 2, 0, 0));
                Assert.AreEqual(0, front.ChanceForTest(w, 1, 2, 0, 1));
                Assert.AreEqual(28, Pcg32.FrontRoll(6947, 2, 3));
                Assert.AreEqual(37, Pcg32.FrontRoll(6947, 6, 3));
                Assert.AreEqual(129, Pcg32.FrontRoll(27, 2, 3));
                return front.PhaseAForTest(w, salt);
            }
            finally
            {
                front.Dispose();
                w.Dispose();
            }
        }

        [Test] public void Salt6947_PlainFlips_HillsHolds() => CollectionAssert.AreEqual(new byte[] { 0, 1, 0, 0 }, Decide(6947));
        [Test] public void Salt27_NothingFlips() => CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, Decide(27));
    }

    /// <summary>Множитель окружения: анклав в кольце врага съедается быстрее.</summary>
    public class SurroundTests
    {
        // 3×3, вся суша, центр (4) — Синие, остальное — Красные. Столиц нет. FrontLen[1,2] = 4.
        // Красные: 1 пехота, наступление → attPow 1000 / 4 = 250. Синие без соединений, гарнизон 250 → defPow 250.
        // ratio 1,0 → chance 36. У центра четыре красных соседа: ×4 → 144. Соль 115: roll(4, N) = 97.
        static byte[] Decide(int[] surround)
        {
            var rules = Rules.Default(2);
            rules.Front.SurroundMult_bp = surround;
            rules.Front.GarrisonPerEdge = 250;
            var world = new World(3, 3, 2, 1, rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                for (int i = 0; i < 9; i++)
                {
                    world.TerrainRW[i] = (byte)Terrain.Land;
                    world.OwnerRW[i] = (byte)(i == 4 ? 2 : 1);
                }
                Worlds.Nation(world, 1, -1, 8);
                Worlds.Nation(world, 2, -1, 1);
                Worlds.Formation(world, 1, 0, 2, Orders.Attack, UnitTypes.Infantry);
                Worlds.Recount(world);
                new FormationSystem().FillPow(world);
                Assert.AreEqual(250, world.AttPow(1, 2, 0));
                Assert.AreEqual(250, world.DefPow(2, 1, 0));
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
        /// <summary>01b acceptance 9: один поток и все потоки дают одинаковый хеш на сидах 1–10 через 3 000 тиков.</summary>
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
                    TestUtil.RunWithBot(sim, 3000, seed);
                    single = sim.ComputeHash();
                }
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    TestUtil.RunWithBot(sim, 3000, seed);
                    multi = sim.ComputeHash();
                }
                Assert.AreEqual(single, multi, $"seed {seed}");
            }
        }

        /// <summary>01b acceptance 10: FrontLen, FrontTerrainLen, Frontier, Pixels, Battalions, Formations совпадают с пересчётом с нуля каждые 600 тиков.</summary>
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
                        TestUtil.RunWithBot(sim, 600, seed * 100 + (ulong)block);
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

    /// <summary>Гибель нации (spec-01 acceptance 15, 01b §5.6): земля ничья, соединения сняты, у врагов фронт закрыт.</summary>
    public class NationDeathTests
    {
        // Ряд 7×1: [1 1 2* 2 2 3 3], * — столица Синих (2). Шанс везде 100%: каждая атака проходит.
        // Все наступают: Красные и Зелёные на Синих, у Синих по соединению на каждый фронт.
        static World Make(Rules rules)
        {
            var world = new World(7, 1, 3, 1, rules);
            byte[] owners = { 1, 1, 2, 2, 2, 3, 3 };
            for (int i = 0; i < 7; i++)
            {
                world.TerrainRW[i] = (byte)Terrain.Land;
                world.OwnerRW[i] = owners[i];
            }
            Worlds.Nation(world, 1, 0, 2);
            Worlds.Nation(world, 2, 2, 3, 500);
            Worlds.Nation(world, 3, 6, 2);
            Worlds.Formation(world, 1, 0, 2, Orders.Attack, UnitTypes.Infantry);
            Worlds.Formation(world, 2, 0, 1, Orders.Attack, UnitTypes.Infantry);
            Worlds.Formation(world, 2, 1, 3, Orders.Attack, UnitTypes.Armor);
            Worlds.Formation(world, 3, 0, 2, Orders.Attack, UnitTypes.Infantry);
            FrontSystem.StampCapitals(world);
            Worlds.Recount(world);
            return world;
        }

        static Rules AlwaysWin()
        {
            var rules = Rules.Default(3);
            rules.Front.WarChance_bp = 1000000;
            rules.Front.StallRatio_bp = 0;
            rules.Front.MaxChance_bp = 10000;
            rules.Front.CapitalRadius = 0;
            rules.Front.SurroundMult_bp = new[] { 10000, 10000, 10000, 10000 };
            return rules;
        }

        [Test]
        public void CapitalFalls_LandBecomesNoMans_FormationsRemoved_EnemiesGoToRear()
        {
            var rules = AlwaysWin();
            var world = Make(rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                var ctx = new CommandContext(TestData.Config(), rules, 0);
                new FormationSystem().FillPow(world);
                front.Tick(world, ctx);
                FormationSystem.CloseFronts(world);

                // Красные взяли столицу (2), Зелёные — пиксель 4. Синие взяли 1 и 5, но пали: те пиксели ничьи.
                CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0, 3, 0, 3 }, world.Owner.ToArray());
                var dead = world.Nations[1];
                Assert.AreEqual(0, dead.Alive);
                Assert.AreEqual(-1, dead.Capital);
                Assert.AreEqual(0, dead.Pixels);
                Assert.AreEqual(0, dead.Gold);
                Assert.AreEqual(0, dead.Battalions);
                Assert.AreEqual(0, dead.Formations);
                Assert.AreEqual(0, dead.DiedAtTick);
                for (int k = 0; k < world.MaxFormationsPerNation; k++)
                    Assert.AreEqual(0, world.FormationAt(world.FormationSlot(2, k)).Alive, $"formation {k} of the dead nation");
                foreach (var b in world.Battalions.ToArray()) Assert.IsTrue(b.Alive == 0 || b.Type == UnitTypes.Infantry, "no armor left (only Blue had armor)");
                foreach (var c in world.CapitalCover.ToArray()) Assert.AreEqual(0, c & 2, "CapitalCover bit of the dead nation");
                // Соединения Красных и Зелёных стояли на фронте с Синими: фронт закрыт, они в тылу.
                Assert.AreEqual(0, world.FormationAt(world.FormationSlot(1, 0)).Front);
                Assert.AreEqual(0, world.FormationAt(world.FormationSlot(3, 0)).Front);
                Assert.AreEqual(1, world.Nations[0].Battalions);
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
            var terrainLen = new int[stride * stride * World.TerrainKinds];
            for (int p = 0; p < n; p++)
            {
                if (!w.IsLand(p)) continue;
                int x = p % width, y = p / width, a = w.Owner[p];
                if (x + 1 < width) Edge(p + 1);
                if (y > 0) Edge(p - width);
                void Edge(int q)
                {
                    if (!w.IsLand(q) || w.Owner[q] == a) return;
                    int b = w.Owner[q];
                    frontLen[a * stride + b]++;
                    frontLen[b * stride + a]++;
                    terrainLen[(a * stride + b) * World.TerrainKinds + w.Terrain[q] - 1]++;
                    terrainLen[(b * stride + a) * World.TerrainKinds + w.Terrain[p] - 1]++;
                }
            }
            CollectionAssert.AreEqual(frontLen, w.FrontLen.ToArray(), $"{where}: FrontLen");
            CollectionAssert.AreEqual(terrainLen, w.FrontTerrainLen.ToArray(), $"{where}: FrontTerrainLen");

            // Battalions и Formations у нации — по живым слотам.
            for (int k = 0; k < w.NationCount; k++)
            {
                byte id = (byte)(k + 1);
                int formations = 0, battalions = 0;
                for (int f = 0; f < w.MaxFormationsPerNation; f++)
                {
                    int slot = w.FormationSlot(id, f);
                    var form = w.FormationAt(slot);
                    if (form.Alive == 0) continue;
                    Assert.AreEqual(id, form.Owner, $"{where}: owner of formation slot {slot}");
                    formations++;
                    battalions += FormationSystem.BattalionCount(w, slot);
                }
                Assert.AreEqual(formations, w.Nations[k].Formations, $"{where}: Formations of {id}");
                Assert.AreEqual(battalions, w.Nations[k].Battalions, $"{where}: Battalions of {id}");
            }

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
        }

        static bool IsFrontier(int p, byte[] owner, byte[] terrain, int w, int h)
        {
            if (terrain[p] == 0) return false;
            int x = p % w, y = p / w;
            bool Diff(int q) => terrain[q] != 0 && owner[q] != owner[p];
            return (y + 1 < h && Diff(p + w)) || (x + 1 < w && Diff(p + 1)) || (y > 0 && Diff(p - w)) || (x > 0 && Diff(p - 1));
        }
    }
}
