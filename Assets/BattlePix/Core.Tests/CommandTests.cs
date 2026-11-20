using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class CommandTests
    {
        static Command Set(int tick, byte nation, int a, int b) =>
            new Command { Tick = tick, Nation = nation, Type = CommandType.DebugSetOwner, A = a, B = b };

        [Test]
        public void Enqueue_RespectsPastAndHorizon()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                TestUtil.Run(sim, 10);
                Assert.AreEqual(EnqueueResult.TickInPast, sim.Enqueue(Set(9, 0, 0, 1)));
                Assert.AreEqual(EnqueueResult.Accepted, sim.Enqueue(Set(10, 0, 0, 1)));
                Assert.AreEqual(EnqueueResult.Accepted, sim.Enqueue(Set(110, 0, 0, 1)));
                Assert.AreEqual(EnqueueResult.BeyondHorizon, sim.Enqueue(Set(111, 0, 0, 1)));
                Assert.AreEqual(2, sim.CommandLog.Count, "refused commands are not logged");
            }
        }

        [Test]
        public void SameTick_AppliedByNationThenSeq()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                // Нация 2 ставится раньше, но нация 1 применяется первой; внутри нации — по Seq.
                sim.Enqueue(Set(1, 2, 100, 2));
                sim.Enqueue(Set(1, 1, 100, 1));
                sim.Enqueue(Set(1, 1, 200, 3));
                sim.Enqueue(Set(1, 1, 200, 4));
                TestUtil.Run(sim, 2);
                Assert.AreEqual(2, sim.World.Owner[100]);
                Assert.AreEqual(4, sim.World.Owner[200]);
            }
        }

        [Test]
        public void InvalidCommands_RejectedDeterministically_ButLogged()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0, nations: 8), 1))
            {
                sim.Enqueue(Set(0, 0, -1, 1));
                sim.Enqueue(Set(0, 0, 512 * 256, 1));
                sim.Enqueue(Set(0, 0, 5, 9));
                sim.Enqueue(Set(0, 0, 6, 8));
                sim.Step();
                Assert.AreEqual(4, sim.CommandLog.Count);
                Assert.AreEqual(3, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.PixelOutOfMap, sim.Rejected[0].Reason);
                Assert.AreEqual(RejectReason.OwnerOutOfRange, sim.Rejected[2].Reason);
                Assert.AreEqual(8, sim.World.Owner[6]);
                Assert.AreEqual(0, sim.World.Owner[5]);
            }
        }

        [Test]
        public void UiCommand_GoesToNextTick()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                TestUtil.Run(sim, 3);
                sim.EnqueueFromUi(CommandType.DebugSetOwner, 0, 7, 2);
                Assert.AreEqual(4, sim.CommandLog[0].Tick);
                sim.Step();
                Assert.AreEqual(0, sim.World.Owner[7]);
                sim.Step();
                Assert.AreEqual(2, sim.World.Owner[7]);
            }
        }

        [Test]
        public void GameOver_RefusesCommands()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1, 2))
            {
                TestUtil.Run(sim, 5);
                Assert.AreEqual(EnqueueResult.GameOver, sim.EnqueueFromUi(CommandType.Noop, 0, 0, 0));
            }
        }

        [Test]
        public void CommandTypeValues_AreFrozen()
        {
            Assert.AreEqual(0, (byte)CommandType.Noop);
            Assert.AreEqual(1, (byte)CommandType.DebugSetOwner);
        }

        [Test]
        public void Config_ParsesShippedJson_AndRejectsBadValues()
        {
            const string json = "{\"format\":1,\"width\":512,\"height\":256,\"ticksPerSecond\":10,\"maxTicksPerFrame\":8," +
                                "\"checkpointInterval\":600,\"commandHorizonTicks\":100," +
                                "\"debugPaint\":{\"enabled\":true,\"pixelsPerTick\":64,\"nations\":8}}";
            var cfg = SimConfig.FromJson(json);
            Assert.AreEqual(512, cfg.Width);
            Assert.AreEqual(64, cfg.DebugPaint.PixelsPerTick);
            Assert.Throws<System.FormatException>(() => SimConfig.FromJson(json.Replace("\"width\":512", "\"width\":0")));
            Assert.Throws<System.FormatException>(() => SimConfig.FromJson(json.Replace("\"format\":1", "\"format\":2")));
        }
    }
}
