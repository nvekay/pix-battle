using System;
using System.Collections.Generic;

namespace BattlePix.Core
{
    public struct Checkpoint
    {
        public int Tick;
        public ulong Hash;
    }

    /// <summary>
    /// Точка входа ядра. Одинаково крутится в игре и в headless.
    /// Принимает только конфиг, сид и команды. Времени и ввода напрямую не знает.
    /// </summary>
    public sealed class Simulation : IDisposable
    {
        public SimConfig Config { get; }
        /// <summary>Правила партии. null — мир Фундамента (старый конструктор, DebugPaint).</summary>
        public Rules Rules { get; }
        /// <summary>Нация под человеком. 0 — все нации под ИИ (headless).</summary>
        public byte HumanNation { get; }
        public World World { get; }
        public CommandQueue Queue { get; }
        public SystemPipeline Pipeline { get; }
        public ulong Seed { get; }

        /// <summary>0 — без лимита (игра). Иначе на этом тике партия заканчивается с EndReason.TickLimit.</summary>
        public int TickLimit { get; }

        readonly CommandContext _ctx;
        readonly List<Checkpoint> _checkpoints = new List<Checkpoint>();
        readonly List<RejectedCommand> _rejected = new List<RejectedCommand>();

        public IReadOnlyList<Checkpoint> Checkpoints => _checkpoints;
        /// <summary>Лог ядра: команды, отклонённые валидацией при применении.</summary>
        public IReadOnlyList<RejectedCommand> Rejected => _rejected;
        public IReadOnlyList<Command> CommandLog => Queue.Log;
        /// <summary>Команды (человека и ИИ), применённые в последнем тике, по (Nation, Seq).</summary>
        public IReadOnlyList<Command> AppliedLastTick => _ctx.Applied;

        /// <summary>true, если последний Step посчитал контрольную точку (раннер исключает такие тики из бюджета).</summary>
        public bool LastStepWroteCheckpoint { get; private set; }

        public ulong LastHash { get; private set; }
        public bool HasHash { get; private set; }

        /// <summary>Мир Фундамента: без правил, число наций из debugPaint.nations. На нём живут тесты этапа 0.</summary>
        public Simulation(SimConfig config, ulong seed, int tickLimit = 0)
            : this(config, seed, tickLimit, SystemPipeline.CreateDefault(config))
        {
        }

        /// <summary>
        /// Партия с правилами: карта генерируется здесь же, до тика 0, и попадает в первый хеш.
        /// tickLimit — страховочный лимит (0 — без лимита), настоящий конец задают правила.
        /// </summary>
        public Simulation(SimConfig config, Rules rules, ulong seed, int tickLimit, byte humanNation)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (tickLimit < 0) throw new ArgumentOutOfRangeException(nameof(tickLimit));
            if (humanNation > rules.NationCount) throw new ArgumentOutOfRangeException(nameof(humanNation));
            config.Validate();
            Config = config;
            Rules = rules;
            HumanNation = humanNation;
            Seed = seed;
            TickLimit = tickLimit;
            World = new World(config.Width, config.Height, rules.NationCount, seed, rules);
            MapGenerator.Generate(World);
            FrontSystem.StampCapitals(World);
            FrontSystem.RebuildFrontier(World);
            FrontSystem.RecountFrontLen(World);
            World.MarkAllDirty();
            Queue = new CommandQueue(config.CommandHorizonTicks);
            Pipeline = SystemPipeline.CreateWithRules(config, rules);
            _ctx = new CommandContext(config, rules, humanNation) { Queue = Queue };
        }

        public Simulation(SimConfig config, ulong seed, int tickLimit, SystemPipeline pipeline)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (tickLimit < 0) throw new ArgumentOutOfRangeException(nameof(tickLimit));
            config.Validate();
            Config = config;
            Seed = seed;
            TickLimit = tickLimit;
            World = new World(config.Width, config.Height, config.NationCount, seed);
            World.MarkAllDirty();
            Queue = new CommandQueue(config.CommandHorizonTicks);
            Pipeline = pipeline;
            _ctx = new CommandContext(config);
        }

        public EnqueueResult Enqueue(Command cmd) => Queue.Enqueue(World.Tick, World.IsOver, ref cmd);

        /// <summary>Команда из UI всегда ставится на следующий тик.</summary>
        public EnqueueResult EnqueueFromUi(CommandType type, byte nation, int a, int b)
        {
            var cmd = new Command { Tick = World.Tick + 1, Nation = nation, Type = type, A = a, B = b };
            return Enqueue(cmd);
        }

        public void Step()
        {
            LastStepWroteCheckpoint = false;
            if (World.IsOver) return;

            // 1. Команды этого тика по (Nation, Seq).
            _ctx.AppliedThisTick.Clear();
            var cmds = Queue.TakeForTick(World.Tick);
            for (int i = 0; i < cmds.Count; i++)
            {
                var c = cmds[i];
                var reason = Validate(c);
                if (reason != RejectReason.None)
                {
                    _rejected.Add(new RejectedCommand { Command = c, Reason = reason });
                    continue;
                }
                Apply(c);
                _ctx.AppliedThisTick.Add(c);
            }

            // 2. Системы в фиксированном порядке.
            Pipeline.Tick(World, _ctx);

            // 3. Следующий тик.
            World.Tick += 1;

            // 4. Конец партии по лимиту. Ставится до контрольной точки, чтобы последняя точка видела IsOver.
            if (TickLimit > 0 && World.Tick >= TickLimit && !World.IsOver)
            {
                World.IsOver = true;
                World.EndReason = EndReason.TickLimit;
            }

            // 5. Контрольная точка: каждый checkpointInterval тиков и после последнего.
            if (World.Tick % Config.CheckpointInterval == 0 || World.IsOver)
                WriteCheckpoint();
        }

        /// <summary>Хеш текущего состояния без записи контрольной точки.</summary>
        public ulong ComputeHash() => StateHash.Compute(World);

        void WriteCheckpoint()
        {
            ulong h = StateHash.Compute(World);
            _checkpoints.Add(new Checkpoint { Tick = World.Tick, Hash = h });
            LastHash = h;
            HasHash = true;
            LastStepWroteCheckpoint = true;
        }

        RejectReason Validate(in Command c)
        {
            switch (c.Type)
            {
                case CommandType.Noop:
                    return RejectReason.None;
                case CommandType.DebugSetOwner:
                    // В мире с правилами владельцев меняет только фронт: иначе разойдутся Pixels, Frontier и FrontLen.
                    if (Rules != null) return RejectReason.DebugOnly;
                    if (c.A < 0 || c.A >= World.PixelCount) return RejectReason.PixelOutOfMap;
                    if (c.B < 0 || c.B > World.NationCount) return RejectReason.OwnerOutOfRange;
                    return RejectReason.None;
                case CommandType.Recruit:
                case CommandType.BuildFort:
                case CommandType.Strike:
                case CommandType.RecruitUnit:
                case CommandType.AssignFormation:
                case CommandType.SetOrder:
                case CommandType.RecruitDivision:
                case CommandType.AssignDivision:
                case CommandType.SetFrontOrder:
                    return GameCommands.Validate(World, c);
                default:
                    return RejectReason.UnknownType;
            }
        }

        void Apply(in Command c)
        {
            switch (c.Type)
            {
                case CommandType.Noop:
                    break;
                case CommandType.DebugSetOwner:
                    World.SetOwner(c.A, (byte)c.B);
                    break;
                case CommandType.RecruitDivision:
                case CommandType.AssignDivision:
                case CommandType.SetFrontOrder:
                    GameCommands.Apply(World, c);
                    break;
            }
        }

        public void Dispose()
        {
            Pipeline.Dispose();
            World.Dispose();
        }
    }
}
