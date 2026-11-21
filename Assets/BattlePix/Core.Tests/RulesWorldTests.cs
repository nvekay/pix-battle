using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Мир с правилами: хеш, реплей формата 3, отказ отладочных команд.</summary>
    public class RulesWorldTests
    {
        [Test]
        public void HashJob_MatchesHashManaged_On10RandomRulesWorlds()
        {
            var rng = new Pcg32(2027, 5);
            for (int k = 0; k < 10; k++)
            {
                var config = TestData.Config();
                config.Width = 96 + rng.NextInt(500);
                config.Height = 64 + rng.NextInt(250);
                int nations = 2 + rng.NextInt(11);
                var rules = Rules.Default(nations);
                ulong seed = ((ulong)rng.Next() << 32) | rng.Next();
                using (var sim = new Simulation(config, rules, seed, 1 + rng.NextInt(300), 0))
                {
                    var w = sim.World;
                    // Трогаем каждое поле, входящее в хеш.
                    var divisions = w.DivisionsRW;
                    for (int i = 0; i < divisions.Length; i++)
                        divisions[i] = new Division
                        {
                            Owner = (byte)rng.NextInt(nations + 1), Alive = (byte)rng.NextInt(2), Type = (byte)rng.NextInt(2),
                            Front = (byte)rng.NextInt(nations + 1), ReadyTick = rng.NextInt(100000) - 50, ArriveTick = rng.NextInt(100000) - 50,
                            Strength_bp = (short)(rng.NextInt(65536) - 32768), Org_bp = (short)(rng.NextInt(65536) - 32768),
                            Exhausted = (byte)rng.NextInt(2),
                        };
                    var order = w.FrontOrderRW;
                    for (int i = 0; i < order.Length; i++) order[i] = (byte)rng.NextInt(2);
                    var flips = w.FlipHistoryRW;
                    for (int i = 0; i < flips.Length; i++) flips[i] = (short)(rng.NextInt(65536) - 32768);
                    var nat = w.NationsRW;
                    for (int i = 0; i < nat.Length; i++)
                    {
                        var s = nat[i];
                        s.Gold = (int)rng.Next();
                        s.RecruitedInfDiv = rng.NextInt(1000);
                        s.RecruitedArmorDiv = rng.NextInt(1000);
                        s.DivisionsLost = rng.NextInt(1000);
                        s.Divisions = (byte)rng.NextInt(33);
                        s.DiedAtTick = rng.NextInt(3) - 1;
                        nat[i] = s;
                    }
                    w.Winner = (byte)rng.NextInt(nations + 1);
                    // Хеши сравниваются сразу: случайные дивизии не обязаны быть согласованы для симуляции.
                    Assert.AreEqual(HashManaged.Compute(w), StateHash.Compute(w), $"world {k}: {config.Width}x{config.Height}, {nations} nations");
                }
            }
        }

        [Test]
        public void Hash_CoversRulesState()
        {
            using (var sim = TestData.Match(1))
            {
                var w = sim.World;
                ulong h0 = sim.ComputeHash();
                var divisions = w.DivisionsRW;
                var v = divisions[3];
                v.ArriveTick = 1;
                divisions[3] = v;
                ulong h1 = sim.ComputeHash();
                Assert.AreNotEqual(h0, h1);
                foreach (System.Func<Division, Division> change in new System.Func<Division, Division>[]
                {
                    x => { x.Strength_bp -= 1; return x; }, x => { x.Org_bp -= 1; return x; }, x => { x.Exhausted = 1; return x; },
                    x => { x.ReadyTick = 5; return x; }, x => { x.Type = 1; return x; },
                })
                {
                    divisions[0] = change(divisions[0]);
                    ulong hc = sim.ComputeHash();
                    Assert.AreNotEqual(h1, hc);
                    h1 = hc;
                }
                w.FrontOrderRW[5] = 1;
                ulong ho = sim.ComputeHash();
                Assert.AreNotEqual(h1, ho);
                h1 = ho;
                var flips = w.FlipHistoryRW;
                flips[7] = 1;
                ulong h2 = sim.ComputeHash();
                Assert.AreNotEqual(h1, h2);
                w.Winner = 3;
                Assert.AreNotEqual(h2, sim.ComputeHash());
            }
        }

        [Test]
        public void DebugSetOwner_IsRejected_InRulesWorld()
        {
            using (var sim = TestData.Match(1))
            {
                int pixel = sim.World.Nations[0].Capital;
                ulong before = sim.ComputeHash();
                sim.Enqueue(new Command { Tick = 0, Type = CommandType.DebugSetOwner, A = pixel, B = 2 });
                sim.Step();
                Assert.AreEqual(1, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.DebugOnly, sim.Rejected[0].Reason);
                Assert.AreEqual(1, sim.World.Owner[pixel]);
            }
        }

        [Test]
        public void ReplayFormat4_WithHuman_RoundTrips()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            ReplayFile original;
            using (var sim = new Simulation(config, rules, 99, 3000, 1))
            {
                for (int t = 0; t < 3000 && !sim.World.IsOver; t++)
                {
                    if (t % 37 == 0) sim.EnqueueFromUi(CommandType.Noop, 1, t, 0);
                    sim.Step();
                }
                original = ReplayFile.Capture(sim, "test", 0x42);
            }
            var json = original.ToJson();
            StringAssert.Contains("\"format\": 4", json);
            StringAssert.Contains("\"human\": 1", json);
            var loaded = ReplayFile.FromJson(json);
            Assert.AreEqual(1, loaded.Human);
            var r = ReplayPlayer.Verify(loaded, config, rules);
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(5, r.CheckpointsCompared);
        }

        [Test]
        public void ReplayFormat3_IsRejected_BeforeDivisions()
        {
            const string json = "{\"format\":3,\"build\":\"x\",\"seed\":1,\"dataHash\":\"0x0\",\"ticks\":10,\"tickLimit\":0,\"human\":1,\"commands\":[],\"checkpoints\":[]}";
            var e = Assert.Throws<UnsupportedReplayFormatException>(() => ReplayFile.FromJson(json));
            Assert.AreEqual(3, e.Format);
            StringAssert.Contains("before divisions", e.Message);
        }

        [Test]
        public void ReplayFormat2_IsRejectedWithClearError()
        {
            const string json = "{\"format\":2,\"build\":\"x\",\"seed\":1,\"dataHash\":\"0x0\",\"ticks\":10,\"tickLimit\":0,\"human\":1,\"commands\":[],\"checkpoints\":[]}";
            var e = Assert.Throws<UnsupportedReplayFormatException>(() => ReplayFile.FromJson(json));
            Assert.AreEqual(2, e.Format);
            StringAssert.Contains("format 2", e.Message);
        }

        [Test]
        public void ReplayFormat1_IsRejectedWithClearError()
        {
            const string json = "{\"format\":1,\"build\":\"x\",\"seed\":1,\"dataHash\":\"0x0\",\"ticks\":10,\"tickLimit\":0,\"commands\":[],\"checkpoints\":[]}";
            var e = Assert.Throws<UnsupportedReplayFormatException>(() => ReplayFile.FromJson(json));
            Assert.AreEqual(1, e.Format);
            StringAssert.Contains("format 1", e.Message);
        }
    }
}
