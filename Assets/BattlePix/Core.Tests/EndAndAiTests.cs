using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Acceptance 14: все ветки 5.7 и порядок проверок.</summary>
    public class EndTests
    {
        static void Set(World w, byte id, int pixels, byte alive = 1)
        {
            var nat = w.NationsRW;
            var s = nat[id - 1];
            s.Pixels = pixels;
            s.Alive = alive;
            nat[id - 1] = s;
        }

        static World Run(byte human, System.Action<World> setup, int tick = 100)
        {
            var sim = TestData.Match(1, 0, human);
            var w = sim.World;
            w.Tick = tick;
            setup(w);
            new EndSystem().Tick(w, new CommandContext(sim.Config, sim.Rules, human));
            _keep = sim;
            return w;
        }

        static Simulation _keep;

        [TearDown] public void Dispose() { _keep?.Dispose(); _keep = null; }

        static int Threshold(World w) => (int)((long)w.Rules.End.VictoryShare_bp * w.TotalLand / 10000) + 1;

        [Test]
        public void HumanDefeated_First()
        {
            var w = Run(1, w2 =>
            {
                Set(w2, 1, 0, alive: 0);
                Set(w2, 2, Threshold(w2)); // кто-то взял порог в тот же тик — сначала поражение человека
            });
            Assert.IsTrue(w.IsOver);
            Assert.AreEqual(EndReason.HumanDefeated, w.EndReason);
            Assert.AreEqual(0, w.Winner);
        }

        [Test]
        public void Conquest_ByThreshold_LargestWins_TieSmallerId()
        {
            var w = Run(0, w2 =>
            {
                int t = Threshold(w2);
                Set(w2, 3, t + 5);
                Set(w2, 5, t + 5);
                Set(w2, 2, t);
            });
            Assert.AreEqual(EndReason.Conquest, w.EndReason);
            Assert.AreEqual(3, w.Winner);
        }

        [Test]
        public void Conquest_LastAlive()
        {
            var w = Run(1, w2 => { for (byte id = 2; id <= 8; id++) Set(w2, id, 0, alive: 0); });
            Assert.AreEqual(EndReason.Conquest, w.EndReason);
            Assert.AreEqual(1, w.Winner, "all AI fell, the human wins");
        }

        [Test]
        public void Timeout_LargestAliveWins()
        {
            var w = Run(0, w2 =>
            {
                Set(w2, 4, 9000);
                Set(w2, 6, 9000);
                Set(w2, 1, 20000, alive: 0);
            }, tick: 26999);
            Assert.AreEqual(EndReason.Timeout, w.EndReason);
            Assert.AreEqual(4, w.Winner);
        }

        [Test]
        public void NoEnd_BeforeTimeout()
        {
            var w = Run(0, w2 => { }, tick: 26998);
            Assert.IsFalse(w.IsOver);
        }

        [Test]
        public void FullMatch_EndsByRules_NotTickLimit()
        {
            using (var sim = TestData.Match(2, 30000))
            {
                TestUtil.Run(sim, 30000);
                Assert.IsTrue(sim.World.IsOver);
                Assert.AreNotEqual(EndReason.TickLimit, sim.World.EndReason);
                Assert.AreEqual(sim.World.Tick, sim.Checkpoints[sim.Checkpoints.Count - 1].Tick);
                TestContext.WriteLine($"seed 2: {sim.World.EndReason} winner {sim.World.Winner} at tick {sim.World.Tick}");
            }
        }
    }

    public class AiTests
    {
        [Test]
        public void Ai_IssuesCommands_NotLogged_ReplayStillPasses()
        {
            ReplayFile replay;
            int ai = 0;
            using (var sim = TestData.Match(5, 6000, human: 1))
            {
                while (!sim.World.IsOver)
                {
                    if (sim.World.Tick == 100) sim.EnqueueFromUi(CommandType.Recruit, 1, 0, 0);
                    sim.Step();
                    foreach (var c in sim.AppliedLastTick) if (c.Nation != 1) ai++;
                }
                Assert.AreEqual(1, sim.CommandLog.Count, "AI commands are not in the log");
                for (byte id = 2; id <= 8; id++) Assert.Greater(sim.World.Nation(id).Recruits, 0, $"AI {id} never recruited");
                replay = ReplayFile.FromJson(ReplayFile.Capture(sim, "t", 0).ToJson());
            }
            Assert.Greater(ai, 0);
            var r = ReplayPlayer.Verify(replay, TestData.Config(), TestData.Rules());
            Assert.IsTrue(r.Ok, r.Error);
        }

        [Test]
        public void Ai_UsesAllThreeCommands_OverAMatch()
        {
            using (var sim = TestData.Match(7, 12000))
            {
                TestUtil.Run(sim, 12000);
                int forts = 0, strikes = 0, recruits = 0;
                for (byte id = 1; id <= 8; id++)
                {
                    forts += sim.World.Nation(id).FortsBuilt;
                    strikes += sim.World.Nation(id).Strikes;
                    recruits += sim.World.Nation(id).Recruits;
                }
                TestContext.WriteLine($"recruits {recruits}, forts {forts}, strikes {strikes}");
                Assert.Greater(recruits, 0);
                Assert.Greater(forts, 0);
                Assert.Greater(strikes, 0);
            }
        }

        [Test]
        public void Retreat_StopsAtLastOwnPixel()
        {
            var rules = Rules.Default(2);
            using (var w = new World(10, 1, 2, 1, rules))
            {
                byte[] owners = { 1, 1, 1, 2, 1, 1, 1, 1, 1, 1 };
                for (int i = 0; i < 10; i++) w.OwnerRW[i] = owners[i];
                Assert.AreEqual(4, AiSystem.Retreat(w, 1, 9, 0, 6), "walks 9→4, stops before enemy pixel 3");
                Assert.AreEqual(7, AiSystem.Retreat(w, 1, 9, 0, 2));
                Assert.AreEqual(1, AiSystem.Retreat(w, 1, 2, 0, 1));
            }
        }
    }
}
