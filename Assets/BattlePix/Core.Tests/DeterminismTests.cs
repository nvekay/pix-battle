using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class DeterminismTests
    {
        static List<Checkpoint> RunMatch(ulong seed, int ticks)
        {
            using (var sim = new Simulation(TestUtil.Config(), seed, ticks))
            {
                TestUtil.Run(sim, ticks);
                Assert.IsTrue(sim.World.IsOver);
                return new List<Checkpoint>(sim.Checkpoints);
            }
        }

        [Test]
        public void SameSeed_FullMatch_AllCheckpointsEqual()
        {
            var a = RunMatch(42, 24000);
            var b = RunMatch(42, 24000);
            Assert.AreEqual(40, a.Count);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Tick, b[i].Tick);
                Assert.AreEqual(a[i].Hash, b[i].Hash, $"checkpoint at tick {a[i].Tick}");
            }
        }

        [Test]
        public void DifferentSeeds_DifferentFinalHash()
        {
            var a = RunMatch(1, 24000);
            var b = RunMatch(2, 24000);
            Assert.AreNotEqual(a[a.Count - 1].Hash, b[b.Count - 1].Hash);
        }

        [TestCase(0UL)]
        [TestCase(ulong.MaxValue)]
        public void ExtremeSeeds_AreDeterministic(ulong seed)
        {
            var a = RunMatch(seed, 1200);
            var b = RunMatch(seed, 1200);
            Assert.AreEqual(a[a.Count - 1].Hash, b[b.Count - 1].Hash);
        }

        /// <summary>
        /// Скорость и пауза меняют только то, сколько тиков делается за кадр.
        /// UI ставит команды на следующий тик, поэтому результат от нарезки кадров не зависит.
        /// </summary>
        [Test]
        public void FrameChunking_SpeedAndPause_DoNotChangeHashes()
        {
            ulong x1 = Play(new[] { 1 });
            ulong x4 = Play(new[] { 4 });
            ulong paused = Play(new[] { 0, 0, 1, 0, 8, 2, 0, 3 });
            Assert.AreEqual(x1, x4);
            Assert.AreEqual(x1, paused);
        }

        static ulong Play(int[] ticksPerFrame)
        {
            using (var sim = new Simulation(TestUtil.Config(), 9))
            {
                var rng = new Pcg32(123, 99);
                // Команды «из UI» отдаются на определённых тиках, независимо от нарезки кадров.
                var uiAtTick = new Dictionary<int, int[]>();
                for (int i = 0; i < 150; i++)
                    uiAtTick[rng.NextInt(2900)] = new[] { rng.NextInt(512 * 256), rng.NextInt(9) };
                int frame = 0;
                while (sim.World.Tick < 3000)
                {
                    int n = ticksPerFrame[frame++ % ticksPerFrame.Length];
                    for (int i = 0; i < n && sim.World.Tick < 3000; i++)
                    {
                        if (uiAtTick.TryGetValue(sim.World.Tick, out var c))
                            Assert.AreEqual(EnqueueResult.Accepted,
                                sim.EnqueueFromUi(CommandType.DebugSetOwner, 0, c[0], c[1]));
                        sim.Step();
                    }
                }
                return sim.ComputeHash();
            }
        }
    }
}
