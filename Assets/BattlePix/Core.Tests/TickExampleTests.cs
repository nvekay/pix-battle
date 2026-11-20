using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Пример тика из раздела 5.2 спеки.</summary>
    public class TickExampleTests
    {
        [Test]
        public void Seed42_OnePixelPerTick_MatchesSpecExample()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 1), 42))
            {
                sim.World.ClearDirtyRows();
                sim.Step();
                Assert.AreEqual(1, sim.World.Tick);
                Assert.AreEqual(3, sim.World.Owner[128143]);
                Assert.AreEqual(250, sim.World.DirtyRowMin);
                Assert.AreEqual(250, sim.World.DirtyRowMax);
                Assert.AreEqual(0xabc2017ac9febb95UL, sim.World.Rng.Get(RngStream.Debug).State);

                sim.Step();
                Assert.AreEqual(2, sim.World.Tick);
                Assert.AreEqual(6, sim.World.Owner[121794]);
                Assert.AreEqual(121794, sim.World.Index(450, 237));
            }
        }

        [Test]
        public void OddMapSize_Works()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 64, width: 333, height: 127), 5))
            {
                TestUtil.Run(sim, 200);
                Assert.AreEqual(200, sim.World.Tick);
            }
        }

        [Test]
        public void TickLimit_EndsGame_AndStopsStepping()
        {
            using (var sim = new Simulation(TestUtil.Config(), 1, tickLimit: 50))
            {
                TestUtil.Run(sim, 1000);
                Assert.IsTrue(sim.World.IsOver);
                Assert.AreEqual(EndReason.TickLimit, sim.World.EndReason);
                Assert.AreEqual(50, sim.World.Tick);
                Assert.AreEqual(50, sim.Checkpoints[sim.Checkpoints.Count - 1].Tick);
                sim.Step();
                Assert.AreEqual(50, sim.World.Tick);
            }
        }
    }
}
