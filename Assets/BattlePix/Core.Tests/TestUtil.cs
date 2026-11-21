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
        /// набор пехотной или танковой дивизии, назначение дивизии на фронт или в тыл, смена приказа фронту.
        /// Не зависит от правил ИИ: тесты хеша и производного состояния гоняют все три команды.
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
            for (int type = 0; type < w.Rules.Divisions.Length; type++)
                if (GameCommands.ValidateRecruit(w, id, type) == RejectReason.None)
                    options.Add(new Command { Nation = id, Type = CommandType.RecruitDivision, A = type });
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                if (!GameCommands.OwnDivision(w, id, k)) continue;
                for (int d = 0; d <= w.NationCount; d++)
                    if (d != id && GameCommands.ValidateAssign(w, id, k, d) == RejectReason.None)
                        options.Add(new Command { Nation = id, Type = CommandType.AssignDivision, A = k, B = d });
            }
            for (int d = 1; d <= w.NationCount; d++)
                if (d != id && w.FrontLength(id, d) > 0 && GameCommands.ValidateFrontOrder(w, id, d, 0) == RejectReason.None)
                    options.Add(new Command { Nation = id, Type = CommandType.SetFrontOrder, A = d, B = 1 - w.FrontOrderOf(id, d) });
            if (options.Count == 0) return null;
            return options[rng.NextInt(options.Count)];
        }
    }
}
