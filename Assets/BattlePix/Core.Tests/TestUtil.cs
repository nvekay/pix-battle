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

        /// <summary>
        /// Партия, где все нации (через очередь, как человек) раз в period тиков отдают случайную допустимую команду:
        /// набор пехоты или брони (в своё или новое соединение), назначение на фронт или в тыл, смена приказа.
        /// Нужна, пока у ИИ нет правил соединений: иначе соединения в тестах всю партию стоят в тылу.
        /// </summary>
        public static void RunWithBot(Simulation sim, int ticks, ulong botSeed, int period = 20)
        {
            var rng = new Pcg32(botSeed, 0xB07);
            var w = sim.World;
            for (int i = 0; i < ticks && !w.IsOver; i++)
            {
                if (w.Tick % period == 0)
                    for (byte id = 1; id <= w.NationCount; id++)
                    {
                        var c = BotCommand(w, id, ref rng);
                        if (c.HasValue) sim.EnqueueFromUi(c.Value.Type, id, c.Value.A, c.Value.B);
                    }
                sim.Step();
            }
        }

        public static Command? BotCommand(World w, byte id, ref Pcg32 rng)
        {
            if (w.Nation(id).Alive == 0) return null;
            var options = new System.Collections.Generic.List<Command>();
            for (int k = -1; k < w.MaxFormationsPerNation; k++)
            {
                if (k >= 0 && !GameCommands.OwnFormation(w, id, k)) continue;
                for (int type = 0; type < w.Rules.Units.Length; type++)
                    if (GameCommands.ValidateRecruit(w, id, k, type) == RejectReason.None)
                        options.Add(new Command { Nation = id, Type = CommandType.RecruitUnit, A = k, B = type });
                if (k < 0) continue;
                for (int d = 0; d <= w.NationCount; d++)
                    if (d != id && GameCommands.ValidateAssign(w, id, k, d) == RejectReason.None)
                        options.Add(new Command { Nation = id, Type = CommandType.AssignFormation, A = k, B = d });
                var f = w.FormationAt(w.FormationSlot(id, k));
                options.Add(new Command { Nation = id, Type = CommandType.SetOrder, A = k, B = 1 - f.Order });
            }
            if (options.Count == 0) return null;
            return options[rng.NextInt(options.Count)];
        }
    }
}
