using NUnit.Framework;
using Unity.Collections;

namespace BattlePix.Core.Tests
{
    /// <summary>Все EditMode-тесты ядра идут с полной детекцией утечек NativeArray.</summary>
    [SetUpFixture]
    public sealed class LeakDetectionSetup
    {
        NativeLeakDetectionMode _previous;

        [OneTimeSetUp]
        public void Enable()
        {
            _previous = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [OneTimeTearDown]
        public void Restore() => NativeLeakDetection.Mode = _previous;
    }

    static class TestUtil
    {
        public static SimConfig Config(int pixelsPerTick = 64, int nations = 8, int width = 512, int height = 256)
        {
            var c = new SimConfig { Width = width, Height = height };
            c.DebugPaint.PixelsPerTick = pixelsPerTick;
            c.DebugPaint.Nations = nations;
            c.DebugPaint.Enabled = pixelsPerTick > 0;
            return c;
        }

        public static void Run(Simulation sim, int ticks)
        {
            for (int i = 0; i < ticks && !sim.World.IsOver; i++) sim.Step();
        }
    }
}
