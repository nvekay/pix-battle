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


    /// <summary>Сборка искусственных миров с дивизиями для тестов.</summary>
    static class Worlds
    {
        /// <summary>Нация без дивизий: id, жива, столица, пиксели, золото.</summary>
        public static void Nation(World w, byte id, int capital, int pixels, int gold = 0)
        {
            w.NationsRW[id - 1] = new NationState { Id = id, Alive = 1, Capital = capital, Pixels = pixels, Gold = gold, DiedAtTick = -1 };
        }

        /// <summary>Дивизия k нации типа type на фронте front, готова и на месте.</summary>
        public static int Division(World w, byte owner, int k, byte front, byte type, int strength = 10000, int org = 10000)
        {
            DivisionSystem.Create(w, owner, k, type, 0);
            int slot = w.DivisionSlot(owner, k);
            var v = w.DivisionsRW[slot];
            v.Front = front;
            v.Strength_bp = (short)strength;
            v.Org_bp = (short)org;
            w.DivisionsRW[slot] = v;
            return slot;
        }

        /// <summary>Приказ стороны a на фронте с d.</summary>
        public static void Order(World w, byte a, byte d, byte order) => w.FrontOrderRW[(a - 1) * w.NationCount + (d - 1)] = order;

        /// <summary>
        /// Война attacker → defender (02b): статус «война», цель goal, довоенная земля — текущие Pixels.
        /// Тесты 1b/01c, где «контакт = война», ставят её явно.
        /// </summary>
        public static void War(World w, byte attacker, byte defender, byte goal = WarGoals.Reparations, int center = -1, int claimTotal = 0)
        {
            w.WarsRW[w.WarIndex(attacker, defender)] = new War
            {
                Status = WarStatus.War, Attacker = attacker, Goal = goal, ClaimCenter = center, ClaimTotal = claimTotal,
                StartTick = w.Tick, PreWarAtt = w.NationsRW[attacker - 1].Pixels, PreWarDef = w.NationsRW[defender - 1].Pixels,
            };
        }

        /// <summary>Core = Owner на всей карте и OccupiedLen с нуля (синтетические миры).</summary>
        public static void CoreFromOwner(World w)
        {
            for (int p = 0; p < w.PixelCount; p++) w.CoreRW[p] = w.OwnerRW[p];
            WarSystem.RecountOccupied(w);
        }

        /// <summary>
        /// Заменить типы дивизий на свои составы (по батальонам rules.units) и пересчитать таблицы.
        /// Нужно тестам 1b, где соединения были собраны из батальонов поштучно.
        /// </summary>
        public static Rules WithTypes(Rules rules, params int[][] compositions)
        {
            var types = new DivisionRules[compositions.Length];
            for (int i = 0; i < types.Length; i++) types[i] = new DivisionRules { Id = "t" + i, Composition = compositions[i], TrainTicks = 0 };
            rules.Divisions = types;
            rules.Army.StartDivisions = new int[types.Length];
            rules.RebuildDivisionTables();
            return rules;
        }

        public static void Recount(World w)
        {
            FrontSystem.RebuildFrontier(w);
            FrontSystem.RecountFrontLen(w);
        }

        /// <summary>
        /// Пример раздела 5 спеки 01c: карта 2×120, столбец 0 — Красные (1, равнина), столбец 1 — Синие (2), Красные объявили войну (02b):
        /// строки 0–71 равнина, 72–107 холмы, 108–119 горы. FrontLen[1,2] = 120, по рельефу синих 72 / 36 / 12.
        /// Красные: 2 танковые (k 0–1) + 2 пехотные (k 2–3), наступление. Синие: 3 пехотные, оборона. Всё на 100%.
        /// </summary>
        public static World Section5(int gold = 1000000, int redOrg = 10000, int blueOrg = 10000)
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
            Division(w, 1, 0, 2, DivisionTypes.Armor, org: redOrg);
            Division(w, 1, 1, 2, DivisionTypes.Armor, org: redOrg);
            Division(w, 1, 2, 2, DivisionTypes.Infantry, org: redOrg);
            Division(w, 1, 3, 2, DivisionTypes.Infantry, org: redOrg);
            Order(w, 1, 2, Orders.Attack);
            for (int k = 0; k < 3; k++) Division(w, 2, k, 1, DivisionTypes.Infantry, org: blueOrg);
            CoreFromOwner(w);
            War(w, 1, 2);
            Recount(w);
            return w;
        }
    }

    /// <summary>Спека 01c, acceptance 3: таблица типов и первая таблица раздела 5 через FillPow и FillTables, включая округления.</summary>
    public class DivisionPowTests
    {
        [Test]
        public void TypeTable_Atk100_Def100_Cost_Upkeep()
        {
            var r = Rules.Default(2);
            CollectionAssert.AreEqual(new[] { 3000, 3000, 2700 }, new[] { r.DivisionAtk100(0, 0), r.DivisionAtk100(0, 1), r.DivisionAtk100(0, 2) });
            CollectionAssert.AreEqual(new[] { 4200, 4200, 4200 }, new[] { r.DivisionDef100(0, 0), r.DivisionDef100(0, 1), r.DivisionDef100(0, 2) });
            CollectionAssert.AreEqual(new[] { 7760, 5160, 2980 }, new[] { r.DivisionAtk100(1, 0), r.DivisionAtk100(1, 1), r.DivisionAtk100(1, 2) });
            CollectionAssert.AreEqual(new[] { 4200, 3640, 2800 }, new[] { r.DivisionDef100(1, 0), r.DivisionDef100(1, 1), r.DivisionDef100(1, 2) });
            Assert.AreEqual(45000, r.DivisionCost(DivisionTypes.Infantry));
            Assert.AreEqual(95000, r.DivisionCost(DivisionTypes.Armor));
            Assert.AreEqual(30, r.DivisionUpkeep(DivisionTypes.Infantry));
            Assert.AreEqual(70, r.DivisionUpkeep(DivisionTypes.Armor));
        }

        [Test]
        public void Section5_Table()
        {
            var w = Worlds.Section5();
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                Assert.AreEqual(120, w.FrontLength(1, 2));
                CollectionAssert.AreEqual(new[] { 72, 36, 12 }, new[] { w.FrontTerrainLength(1, 2, 0), w.FrontTerrainLength(1, 2, 1), w.FrontTerrainLength(1, 2, 2) });
                new DivisionSystem().FillPow(w);
                int[] att = { 179, 136, 94 }, def = { 145, 181, 217 }, chance = { 78, 0, 0 };
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

        [Test]
        public void ExhaustedAttackers_DoNotAttack_AndDefendWithoutPenalty()
        {
            var w = Worlds.Section5();
            try
            {
                for (int k = 0; k < 4; k++)
                {
                    var v = w.DivisionsRW[w.DivisionSlot(1, k)];
                    v.Exhausted = 1;
                    w.DivisionsRW[w.DivisionSlot(1, k)] = v;
                }
                new DivisionSystem().FillPow(w);
                for (int tr = 0; tr < 3; tr++) Assert.AreEqual(0, w.AttPow(1, 2, tr), "exhausted: attack 0");
                // Оборона Красных без штрафа наступления: (2 × 4200 + 2 × 4200) / 120 + 40 = 180 на равнине.
                Assert.AreEqual(180, w.DefPow(1, 2, 0));
            }
            finally
            {
                w.Dispose();
            }
        }

        [Test]
        public void CapitalFlag_MultipliesDefense_CappedByDefenseMultCap()
        {
            var w = Worlds.Section5();
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                new DivisionSystem().FillPow(w);
                // Столица ×3: def 145 → 435, ratio 179×10000/435 = 4114 < stall → 0.
                Assert.AreEqual(0, front.ChanceForTest(w, 1, 2, 1, 0));
                w.Rules.Front.CapitalMult_bp = 11000; // ×1,1: def 159, ratio 11257, chance 180×3257/10000 = 58
                Assert.AreEqual(58, front.ChanceForTest(w, 1, 2, 1, 0));
                w.Rules.Front.DefenseMultCap_bp = 10000; // кап ×1: как без столицы
                Assert.AreEqual(78, front.ChanceForTest(w, 1, 2, 1, 0));
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
    /// Красные: дивизия [2 пехоты, 1 броня], наступление; Синие: дивизия [3 пехоты], оборона (01c: соединения 1b — типами дивизий).
    /// FrontLen = 2, гарнизон 40, окружение выключено.
    /// att100: равнина 2000 + 3380 = 5380, холмы 2000 + 2080 = 4080 → attPow 2690 / 2040.
    /// def100 Синих 4200 → (2100 + 40) × рельеф: 2140 / 2675. ratio: 12570 / 7626 → chance 82 / 0.
    /// Frontier = [1, 2, 5, 6], красный сосед у 2 и 6 — W (k = 3). Эталонный pcg_hash: соль 6947 → roll(2,3) = 28,
    /// roll(6,3) = 37 (на холмах бросок прошёл бы, но шанс 0); соль 27 → 129 и 104.
    /// </summary>
    public class FrontExampleTests
    {
        static byte[] Decide(uint salt)
        {
            var rules = Worlds.WithTypes(Rules.Default(2), new[] { 2, 1 }, new[] { 3, 0 });
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
                Worlds.Division(w, 1, 0, 2, 0);
                Worlds.Order(w, 1, 2, Orders.Attack);
                Worlds.Division(w, 2, 0, 1, 1);
                Worlds.CoreFromOwner(w);
                Worlds.War(w, 1, 2);
                Worlds.Recount(w);
                new DivisionSystem().FillPow(w);
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
        // Красные: дивизия из 1 пехоты, наступление → attPow 1000 / 4 = 250. Синие без дивизий, гарнизон 250 → defPow 250.
        // ratio 1,0 → chance 36. У центра четыре красных соседа: ×4 → 144. Соль 115: roll(4, N) = 97.
        static byte[] Decide(int[] surround)
        {
            var rules = Worlds.WithTypes(Rules.Default(2), new[] { 1, 0 });
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
                Worlds.Division(world, 1, 0, 2, 0);
                Worlds.Order(world, 1, 2, Orders.Attack);
                Worlds.CoreFromOwner(world);
                Worlds.War(world, 1, 2);
                Worlds.Recount(world);
                new DivisionSystem().FillPow(world);
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
        /// <summary>01c acceptance 6: один поток и все потоки дают одинаковый хеш на сидах 1–10 через 3 000 тиков.</summary>
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

        /// <summary>01c acceptance 6, 02b acceptance 4: FrontLen, FrontTerrainLen, Frontier, Pixels, Divisions, OccupiedLen, ClaimHeldLen совпадают с пересчётом с нуля каждые 600 тиков, с войнами.</summary>
        [Test]
        public void DerivedStateMatches()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            int declared = 0, peace = 0;
            for (ulong seed = 1; seed <= 10; seed++)
            {
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                {
                    for (int block = 0; block < 10 && !sim.World.IsOver; block++)
                    {
                        TestUtil.RunWithBot(sim, 600, seed * 100 + (ulong)block);
                        Derived.AssertMatches(sim.World, $"seed {seed}, tick {sim.World.Tick}");
                    }
                    declared += sim.World.WarEvents.FindAll(e => e.Kind == WarEventKind.Declared).Count;
                    peace += sim.World.WarEvents.FindAll(e => e.Kind == WarEventKind.Peace).Count;
                }
            }
            TestContext.WriteLine($"wars declared {declared}, peace {peace}");
            Assert.Greater(declared, 0, "02b acceptance 4: with wars");
            Assert.Greater(peace, 0, "02b acceptance 4: with peace");
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

    /// <summary>Гибель нации (spec-01 acceptance 15, 01c §4.5): земля ничья, дивизии сняты, FrontOrder обнулён, у врагов фронт закрыт.</summary>
    public class NationDeathTests
    {
        // Ряд 7×1: [1 1 2* 2 2 3 3], * — столица Синих (2). Шанс везде 100%: каждая атака проходит.
        // Все наступают: Красные и Зелёные на Синих, у Синих по дивизии на каждый фронт. Типы: 0 — [1 пехота], 1 — [1 броня].
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
            Worlds.Division(world, 1, 0, 2, 0);
            Worlds.Division(world, 2, 0, 1, 0);
            Worlds.Division(world, 2, 1, 3, 1);
            Worlds.Division(world, 3, 0, 2, 0);
            Worlds.Order(world, 1, 2, Orders.Attack);
            Worlds.Order(world, 2, 1, Orders.Attack);
            Worlds.Order(world, 2, 3, Orders.Attack);
            Worlds.Order(world, 3, 2, Orders.Attack);
            Worlds.CoreFromOwner(world);
            Worlds.War(world, 1, 2);
            Worlds.War(world, 3, 2);
            FrontSystem.StampCapitals(world);
            Worlds.Recount(world);
            return world;
        }

        static Rules AlwaysWin()
        {
            var rules = Worlds.WithTypes(Rules.Default(3), new[] { 1, 0 }, new[] { 0, 1 });
            rules.Front.WarChance_bp = 1000000;
            rules.Front.StallRatio_bp = 0;
            rules.Front.MaxChance_bp = 10000;
            rules.Front.CapitalRadius = 0;
            rules.Front.SurroundMult_bp = new[] { 10000, 10000, 10000, 10000 };
            return rules;
        }

        [Test]
        public void CapitalFalls_LandBecomesNoMans_DivisionsRemoved_EnemiesGoToRear()
        {
            var rules = AlwaysWin();
            var world = Make(rules);
            var front = new FrontSystem { SingleThreaded = true };
            try
            {
                var ctx = new CommandContext(TestData.Config(), rules, 0);
                new DivisionSystem().FillPow(world);
                front.Tick(world, ctx);
                DivisionSystem.CloseFronts(world);

                // Красные взяли столицу (2), Зелёные — пиксель 4. Синие взяли 1 и 5, но пали: те пиксели ничьи.
                CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0, 3, 0, 3 }, world.Owner.ToArray());
                var dead = world.Nations[1];
                Assert.AreEqual(0, dead.Alive);
                Assert.AreEqual(-1, dead.Capital);
                Assert.AreEqual(0, dead.Pixels);
                Assert.AreEqual(0, dead.Gold);
                Assert.AreEqual(0, dead.Divisions);
                Assert.AreEqual(0, dead.DiedAtTick);
                for (int k = 0; k < world.MaxDivisionsPerNation; k++)
                    Assert.AreEqual(0, world.DivisionAt(world.DivisionSlot(2, k)).Alive, $"division {k} of the dead nation");
                foreach (var v in world.Divisions.ToArray()) Assert.IsTrue(v.Alive == 0 || v.Type == 0, "no armor left (only Blue had armor)");
                foreach (var c in world.CapitalCover.ToArray()) Assert.AreEqual(0, c & 2, "CapitalCover bit of the dead nation");
                for (int i = 1; i <= 3; i++)
                {
                    Assert.AreEqual(Orders.Defend, world.FrontOrderOf(2, i), $"order row of the dead nation, {i}");
                    Assert.AreEqual(Orders.Defend, world.FrontOrderOf(i, 2), $"order column of the dead nation, {i}");
                }
                // Дивизии Красных и Зелёных стояли на фронте с Синими: фронт закрыт, они в тылу.
                Assert.AreEqual(0, world.DivisionAt(world.DivisionSlot(1, 0)).Front);
                Assert.AreEqual(0, world.DivisionAt(world.DivisionSlot(3, 0)).Front);
                Assert.AreEqual(1, world.Nations[0].Divisions);
                Assert.AreEqual(2, world.Nations[0].Pixels);
                Assert.AreEqual(2, world.Nations[2].Pixels);
                // 02b §4.5: занятое у Синих закреплено за держателями, земля Синих ничья, войны с Синими закрыты.
                CollectionAssert.AreEqual(new byte[] { 1, 0, 1, 0, 3, 0, 3 }, world.Core.ToArray());
                Assert.AreEqual(WarStatus.Peace, world.WarOf(1, 2).Status);
                Assert.AreEqual(WarStatus.Peace, world.WarOf(2, 3).Status);
                Assert.AreEqual(0, world.WarOf(1, 2).Attacker);
                Assert.AreEqual(world.Tick, world.Nation(1).LastWarEndTick, "Red's war ended");
                Assert.AreEqual(world.Tick, world.Nation(3).LastWarEndTick, "Green's war ended");
                Assert.AreEqual(2, world.WarEvents.FindAll(e => e.Kind == WarEventKind.EndedByDeath).Count);
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

            // 02b: OccupiedLen с нуля; Core = 0 ровно там, где Owner = 0.
            var occupied = new int[stride * stride];
            for (int p = 0; p < n; p++)
            {
                int o = w.Owner[p], c = w.Core[p];
                if ((o == 0) != (c == 0)) Assert.Fail($"{where}: pixel {p} owner {o} core {c}");
                if (c != 0 && o != c) occupied[o * stride + c]++;
            }
            CollectionAssert.AreEqual(occupied, w.OccupiedLen.ToArray(), $"{where}: OccupiedLen");
            // ClaimHeldLen считается раз в секунду: сверяем на границе секунды (после Step с WarSystem.Second).
            if (w.Tick % 10 == 0)
                for (int a = 1; a <= w.NationCount; a++)
                for (int b = a + 1; b <= w.NationCount; b++)
                {
                    var war = w.WarOf(a, b);
                    int expected = war.Status == WarStatus.War && war.Goal == WarGoals.Claims
                        ? WarSystem.CountCircle(w, war.ClaimCenter, war.Defender(a, b), war.Attacker) : 0;
                    Assert.AreEqual(expected, w.ClaimHeld(a, b), $"{where}: ClaimHeldLen {a}-{b}");
                }
            CollectionAssert.AreEqual(terrainLen, w.FrontTerrainLen.ToArray(), $"{where}: FrontTerrainLen");

            // Divisions у нации — по живым слотам.
            for (int k = 0; k < w.NationCount; k++)
            {
                byte id = (byte)(k + 1);
                int divisions = 0;
                for (int j = 0; j < w.MaxDivisionsPerNation; j++)
                {
                    int slot = w.DivisionSlot(id, j);
                    var v = w.DivisionAt(slot);
                    if (v.Alive == 0) continue;
                    Assert.AreEqual(id, v.Owner, $"{where}: owner of division slot {slot}");
                    divisions++;
                }
                Assert.AreEqual(divisions, w.Nations[k].Divisions, $"{where}: Divisions of {id}");
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
