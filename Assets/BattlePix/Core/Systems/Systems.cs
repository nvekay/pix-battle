using System.Collections.Generic;

namespace BattlePix.Core
{
    /// <summary>Контекст тика для систем: конфиг и команды, применённые в этот тик.</summary>
    public sealed class CommandContext
    {
        public SimConfig Config { get; }
        internal readonly List<Command> AppliedThisTick = new List<Command>();
        public IReadOnlyList<Command> Applied => AppliedThisTick;

        public CommandContext(SimConfig config)
        {
            Config = config;
        }
    }

    public interface ISimSystem
    {
        void Tick(World world, CommandContext ctx);
    }

    /// <summary>
    /// Фиксированный порядок систем. Порядок задаётся здесь списком, а не порядком регистрации в рантайме.
    /// Прототип дописывает сюда фронт, экономику, ИИ.
    /// </summary>
    public sealed class SystemPipeline
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

        public void Tick(World world, CommandContext ctx)
        {
            for (int i = 0; i < _systems.Length; i++) _systems[i].Tick(world, ctx);
        }
    }

    /// <summary>
    /// Отладочная система этапа 0: красит случайные пиксели случайным владельцем.
    /// Не механика. Удаляется в прототипе.
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
