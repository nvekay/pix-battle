using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Мир с правилами: хеш, реплей формата 2, отказ отладочных команд.</summary>
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
                    var forts = w.FortsRW;
                    for (int i = 0; i < forts.Length; i++)
                        forts[i] = new Fort { Pixel = rng.NextInt(w.PixelCount), Owner = (byte)rng.NextInt(nations + 1), Alive = (byte)rng.NextInt(2) };
                    var strikes = w.StrikeUntilRW;
                    for (int i = 0; i < strikes.Length; i++) strikes[i] = rng.NextInt(100000) - 50;
                    var flips = w.FlipHistoryRW;
                    for (int i = 0; i < flips.Length; i++) flips[i] = (short)(rng.NextInt(65536) - 32768);
                    var nat = w.NationsRW;
                    for (int i = 0; i < nat.Length; i++)
                    {
                        var s = nat[i];
                        s.Gold = (int)rng.Next();
                        s.Recruits = rng.NextInt(1000);
                        s.FortsAlive = (byte)rng.NextInt(9);
                        s.DiedAtTick = rng.NextInt(3) - 1;
                        nat[i] = s;
                    }
                    w.Winner = (byte)rng.NextInt(nations + 1);
                    TestUtil.Run(sim, rng.NextInt(200));
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
                var forts = w.FortsRW;
                forts[3] = new Fort { Pixel = 1, Owner = 1, Alive = 1 };
                ulong h1 = sim.ComputeHash();
                Assert.AreNotEqual(h0, h1);
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
        public void ReplayFormat2_WithHuman_RoundTrips()
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
            StringAssert.Contains("\"format\": 2", json);
            StringAssert.Contains("\"human\": 1", json);
            var loaded = ReplayFile.FromJson(json);
            Assert.AreEqual(1, loaded.Human);
            var r = ReplayPlayer.Verify(loaded, config, rules);
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(5, r.CheckpointsCompared);
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
