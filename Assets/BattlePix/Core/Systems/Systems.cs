using System;
using System.Collections.Generic;

namespace BattlePix.Core
{
    /// <summary>Контекст тика для систем: конфиг, правила и команды, применённые в этот тик.</summary>
    public sealed class CommandContext
    {
        public SimConfig Config { get; }
        /// <summary>null — мир Фундамента.</summary>
        public Rules Rules { get; }
        public byte HumanNation { get; }
        internal readonly List<Command> AppliedThisTick = new List<Command>();
        public IReadOnlyList<Command> Applied => AppliedThisTick;
        /// <summary>Очередь партии: через неё ИИ отдаёт команды. null в контекстах, собранных тестами вручную.</summary>
        internal CommandQueue Queue;

        /// <summary>Команда ядра на Tick + 1: та же валидация, но не в логе реплея.</summary>
        internal void IssueInternal(World world, Command cmd)
        {
            if (Queue == null) throw new System.InvalidOperationException("no command queue in this context");
            Queue.EnqueueInternal(world.Tick, ref cmd);
        }

        public CommandContext(SimConfig config) : this(config, null, 0)
        {
        }

        public CommandContext(SimConfig config, Rules rules, byte humanNation)
        {
            Config = config;
            Rules = rules;
            HumanNation = humanNation;
        }
    }

    public interface ISimSystem
    {
        void Tick(World world, CommandContext ctx);
    }

    /// <summary>
    /// Фиксированный порядок систем. Порядок задаётся здесь списком, а не порядком регистрации в рантайме.
    /// </summary>
    public sealed class SystemPipeline : IDisposable
    {
        readonly ISimSystem[] _systems;

        public SystemPipeline(ISimSystem[] systemsInOrder)
        {
            _systems = systemsInOrder;
        }

        public int Count => _systems.Length;

        public static SystemPipeline CreateDefault(SimConfig cfg)
        {
            var list = new List<ISimSystem>();
            if (cfg.DebugPaint.Enabled && cfg.DebugPaint.PixelsPerTick > 0)
                list.Add(new DebugPaintSystem());
            return new SystemPipeline(list.ToArray());
        }

        /// <summary>Пайплайн партии с правилами. Порядок — спека 5.2.</summary>
        public static SystemPipeline CreateWithRules(SimConfig cfg, Rules rules)
        {
            var list = new List<ISimSystem>
            {
                new FrontSystem(),
                new EconomySystem(),
                new StrikeSystem(),
                new AiSystem(),
                new EndSystem(),
            };
            return new SystemPipeline(list.ToArray());
        }

        /// <summary>Система пайплайна по типу (тесты: однопоточный фронт).</summary>
        public T Get<T>() where T : class, ISimSystem
        {
            foreach (var s in _systems)
                if (s is T t) return t;
            return null;
        }

        public void Dispose()
        {
            foreach (var s in _systems)
                if (s is IDisposable d) d.Dispose();
        }

        public void Tick(World world, CommandContext ctx)
        {
            for (int i = 0; i < _systems.Length; i++) _systems[i].Tick(world, ctx);
        }
    }

    /// <summary>
    /// Отладочная система этапа 0: красит случайные пиксели случайным владельцем.
    /// Не механика. Живёт только в мире без правил: на ней держатся тесты Фундамента.
    /// </summary>
    public sealed class DebugPaintSystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
            var cfg = ctx.Config.DebugPaint;
            ref var rng = ref world.Rng.Get(RngStream.Debug);
            int nations = cfg.Nations < world.NationCount ? cfg.Nations : world.NationCount;
            for (int i = 0; i < cfg.PixelsPerTick; i++)
            {
                int x = rng.NextInt(world.Width);
                int y = rng.NextInt(world.Height);
                byte owner = (byte)(1 + rng.NextInt(nations));
                world.SetOwner(y * world.Width + x, owner);
            }
        }
    }
}
