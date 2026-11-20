using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class ReplayTests
    {
        const int Ticks = 6000;

        static ReplayFile RecordMatch(ulong seed, int commandCount, int tickLimit)
        {
            using (var sim = new Simulation(TestUtil.Config(), seed, tickLimit))
            {
                var rng = new Pcg32(seed, 777);
                // Заранее разложим 200 команд по тикам и будем отдавать их «из UI» по ходу партии.
                var plan = new int[Ticks];
                for (int i = 0; i < commandCount; i++) plan[rng.NextInt(Ticks - 1)]++;
                while (!sim.World.IsOver)
                {
                    for (int k = 0; k < plan[sim.World.Tick]; k++)
                        Assert.AreEqual(EnqueueResult.Accepted, sim.EnqueueFromUi(CommandType.DebugSetOwner,
                            (byte)rng.NextInt(9), rng.NextInt(sim.World.PixelCount), rng.NextInt(9)));
                    sim.Step();
                }
                return ReplayFile.Capture(sim, "test", 0x1234);
            }
        }

        [Test]
        public void Replay_With200Commands_MatchesAllCheckpoints_AfterJsonRoundTrip()
        {
            var original = RecordMatch(77, 200, Ticks);
            Assert.AreEqual(200, original.Commands.Count);
            var loaded = ReplayFile.FromJson(original.ToJson());

            var r = ReplayPlayer.Verify(loaded, TestUtil.Config());
            Assert.IsTrue(r.Ok, r.Error);
            Assert.AreEqual(Ticks / 600, r.CheckpointsCompared);
            Assert.AreEqual(-1, r.FirstDivergentTick);
        }

        [Test]
        public void Replay_TamperedCommand_ReportsFirstDivergentCheckpoint()
        {
            var replay = ReplayFile.FromJson(RecordMatch(77, 200, Ticks).ToJson());
            // Подменим команду где-то в середине партии.
            // Берём команду незадолго до контрольной точки, чтобы DebugPaint не успел случайно затереть разницу.
            int idx = replay.Commands.FindIndex(c => c.Tick > 2400 && c.Tick % 600 >= 500);
            Assert.GreaterOrEqual(idx, 0);
            var victim = replay.Commands[idx];
            victim.B = (victim.B + 1) % 9;
            victim.A = (victim.A + 1) % (512 * 256);
            int expectedTick = (victim.Tick / 600 + 1) * 600;

            var r = ReplayPlayer.Verify(replay, TestUtil.Config());
            Assert.IsFalse(r.Ok);
            Assert.IsNull(r.Error);
            Assert.AreEqual(expectedTick, r.FirstDivergentTick);
            Assert.AreEqual(expectedTick - 600, r.LastMatchingTick);
        }

        [Test]
        public void Replay_SavedMidGame_WithoutTickLimit_Verifies()
        {
            using (var sim = new Simulation(TestUtil.Config(), ulong.MaxValue))
            {
                for (int i = 0; i < 1337; i++)
                {
                    if (i % 13 == 0) sim.EnqueueFromUi(CommandType.DebugSetOwner, 0, i * 31, i % 9);
                    sim.Step();
                }
                // Команда на будущий тик ещё не применена и в реплей не попадает.
                sim.EnqueueFromUi(CommandType.DebugSetOwner, 0, 1, 1);
                var replay = ReplayFile.FromJson(ReplayFile.Capture(sim, "test", 0).ToJson());
                Assert.AreEqual(ulong.MaxValue, replay.Seed);
                Assert.AreEqual(1337, replay.Ticks);
                Assert.AreEqual(1337, replay.Checkpoints[replay.Checkpoints.Count - 1].Tick);
                var r = ReplayPlayer.Verify(replay, TestUtil.Config());
                Assert.IsTrue(r.Ok, r.Error);
                Assert.AreEqual(sim.ComputeHash(), r.FinalHash);
            }
        }

        [Test]
        public void Replay_JsonUsesSpecFieldNames()
        {
            var json = RecordMatch(3, 5, 1200).ToJson();
            StringAssert.Contains("\"type\": \"DebugSetOwner\"", json);
            StringAssert.Contains("\"checkpoints\"", json);
            StringAssert.Contains("\"dataHash\": \"0x0000000000001234\"", json);
        }
    }
}
