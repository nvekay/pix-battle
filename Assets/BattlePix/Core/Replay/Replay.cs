using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BattlePix.Core
{
    /// <summary>Реплей формата, который нельзя проиграть (формат 1 этапа 0 не знает правил).</summary>
    public sealed class UnsupportedReplayFormatException : FormatException
    {
        public int Format { get; }

        public UnsupportedReplayFormatException(int format, string message) : base(message)
        {
            Format = format;
        }
    }

    /// <summary>
    /// Файл реплея = баг-репорт: сид + лог команд человека + контрольные хеши.
    /// Команды ИИ в файл не пишутся: при проигрывании ИИ отдаёт их сам.
    /// </summary>
    public sealed class ReplayFile
    {
        /// <summary>3 — фронты и соединения (спека 01b). 2 — прототип с крепостями и ударом, 1 — этап 0.</summary>
        public const int SupportedFormat = 3;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("build")] public string Build = "";
        [JsonProperty("seed")] public ulong Seed;
        [JsonProperty("dataHash")] public string DataHash = "";
        [JsonProperty("ticks")] public int Ticks;
        /// <summary>Лимит тиков, с которым шла партия. 0 — без лимита (партия из игры).</summary>
        [JsonProperty("tickLimit")] public int TickLimit;
        /// <summary>Нация под человеком. 0 — партия без человека.</summary>
        [JsonProperty("human")] public byte Human;
        [JsonProperty("commands")] public List<ReplayCommand> Commands = new List<ReplayCommand>();
        [JsonProperty("checkpoints")] public List<ReplayCheckpoint> Checkpoints = new List<ReplayCheckpoint>();

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            Formatting = Formatting.Indented,
        };

        public string ToJson() => JsonConvert.SerializeObject(this, Settings);

        public static ReplayFile FromJson(string json)
        {
            var r = JsonConvert.DeserializeObject<ReplayFile>(json, Settings)
                    ?? throw new FormatException("replay: empty");
            if (r.Format == 1)
                throw new UnsupportedReplayFormatException(1,
                    "replay: format 1 is from stage 0 (Foundation): it has no rules and cannot be replayed, expected format 3");
            if (r.Format == 2)
                throw new UnsupportedReplayFormatException(2,
                    "replay: format 2 is from before fronts and formations (forts and strikes): it cannot be replayed, expected format 3");
            if (r.Format != SupportedFormat)
                throw new UnsupportedReplayFormatException(r.Format,
                    $"replay: format {r.Format} is not supported, expected {SupportedFormat}");
            return r;
        }

        /// <summary>
        /// Снимок партии. Если текущий тик не попал на контрольную точку, добавляется точка на текущем тике,
        /// чтобы реплей сверял состояние ровно в момент сохранения.
        /// </summary>
        public static ReplayFile Capture(Simulation sim, string build, ulong dataHash)
        {
            var r = new ReplayFile
            {
                Build = build,
                Seed = sim.Seed,
                DataHash = Fnv1a64.ToHex(dataHash),
                Ticks = sim.World.Tick,
                TickLimit = sim.TickLimit,
                Human = sim.HumanNation,
            };
            foreach (var c in sim.CommandLog)
            {
                if (c.Tick >= sim.World.Tick) continue; // ещё не применённые команды в реплей не идут
                r.Commands.Add(ReplayCommand.From(c));
            }
            foreach (var cp in sim.Checkpoints)
                r.Checkpoints.Add(new ReplayCheckpoint { Tick = cp.Tick, Hash = Fnv1a64.ToHex(cp.Hash) });
            if (r.Checkpoints.Count == 0 || r.Checkpoints[r.Checkpoints.Count - 1].Tick != sim.World.Tick)
                r.Checkpoints.Add(new ReplayCheckpoint { Tick = sim.World.Tick, Hash = Fnv1a64.ToHex(sim.ComputeHash()) });
            return r;
        }
    }

    public sealed class ReplayCommand
    {
        [JsonProperty("t")] public int Tick;
        [JsonProperty("n")] public byte Nation;
        [JsonProperty("s")] public int Seq;
        [JsonProperty("type")] public CommandType Type;
        [JsonProperty("a")] public int A;
        [JsonProperty("b")] public int B;

        public static ReplayCommand From(Command c) => new ReplayCommand
        {
            Tick = c.Tick, Nation = c.Nation, Seq = c.Seq, Type = c.Type, A = c.A, B = c.B,
        };

        public Command ToCommand() => new Command
        {
            Tick = Tick, Nation = Nation, Seq = Seq, Type = Type, A = A, B = B,
        };
    }

    public sealed class ReplayCheckpoint
    {
        [JsonProperty("t")] public int Tick;
        [JsonProperty("h")] public string Hash = "";
    }

    public struct ReplayResult
    {
        public bool Ok;
        public int CheckpointsCompared;
        /// <summary>Первая контрольная точка, где хеш разошёлся. −1, если расхождения нет.</summary>
        public int FirstDivergentTick;
        /// <summary>Последняя совпавшая точка перед расхождением (0, если такой нет).</summary>
        public int LastMatchingTick;
        public ulong Expected;
        public ulong Actual;
        public string Error;
        public ulong FinalHash;
    }

    /// <summary>Проигрывает реплей и сверяет все контрольные точки.</summary>
    public static class ReplayPlayer
    {
        /// <summary>Партия с правилами: Simulation строится по текущим данным и human из файла.</summary>
        public static ReplayResult Verify(ReplayFile replay, SimConfig config, Rules rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (replay.Human > rules.NationCount)
                return Fail(new ReplayResult { FirstDivergentTick = -1 }, $"human nation {replay.Human} is out of range 0..{rules.NationCount}");
            return Verify(replay, () => new Simulation(config, rules, replay.Seed, replay.TickLimit, replay.Human));
        }

        /// <summary>Мир Фундамента (без правил): для тестов этапа 0.</summary>
        public static ReplayResult Verify(ReplayFile replay, SimConfig config) =>
            Verify(replay, () => new Simulation(config, replay.Seed, replay.TickLimit));

        static ReplayResult Verify(ReplayFile replay, Func<Simulation> create)
        {
            var result = new ReplayResult { Ok = true, FirstDivergentTick = -1 };

            // Контрольные точки и команды по тикам. Порядок команд внутри тика — как в логе (по Seq).
            var expected = new Dictionary<int, ulong>();
            foreach (var cp in replay.Checkpoints)
            {
                if (!Fnv1a64.TryParseHex(cp.Hash, out var h))
                    return Fail(result, $"bad checkpoint hash '{cp.Hash}' at tick {cp.Tick}");
                expected[cp.Tick] = h;
            }

            var commands = new List<Command>(replay.Commands.Count);
            foreach (var rc in replay.Commands) commands.Add(rc.ToCommand());
            commands.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Seq.CompareTo(b.Seq));

            using (var sim = create())
            {
                int next = 0;
                while (sim.World.Tick < replay.Ticks && !sim.World.IsOver)
                {
                    if (next < commands.Count && commands[next].Tick < sim.World.Tick)
                        return Fail(result, $"command {commands[next]} is earlier than tick {sim.World.Tick}");
                    while (next < commands.Count && commands[next].Tick == sim.World.Tick)
                    {
                        var c = commands[next++];
                        var er = sim.Enqueue(c);
                        if (er != EnqueueResult.Accepted)
                            return Fail(result, $"command {c} was not accepted: {er}");
                    }

                    sim.Step();

                    if (expected.TryGetValue(sim.World.Tick, out var exp))
                    {
                        ulong act = sim.LastStepWroteCheckpoint ? sim.LastHash : sim.ComputeHash();
                        result.CheckpointsCompared++;
                        if (act != exp)
                        {
                            result.Ok = false;
                            result.FirstDivergentTick = sim.World.Tick;
                            result.Expected = exp;
                            result.Actual = act;
                            return result;
                        }
                        result.LastMatchingTick = sim.World.Tick;
                    }
                }

                if (sim.World.Tick != replay.Ticks)
                    return Fail(result, $"replay ended at tick {sim.World.Tick}, expected {replay.Ticks}");
                if (result.CheckpointsCompared != expected.Count)
                    return Fail(result, $"compared {result.CheckpointsCompared} of {expected.Count} checkpoints");
                result.FinalHash = sim.ComputeHash();
            }
            return result;
        }

        static ReplayResult Fail(ReplayResult r, string error)
        {
            r.Ok = false;
            r.Error = error;
            return r;
        }
    }
}
