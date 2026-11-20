using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BattlePix.Core;
using BattlePix.Game;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BattlePix.Editor
{
    /// <summary>
    /// Headless-раннер бот-партий.
    ///   Unity -batchmode -nographics -projectPath . -executeMethod BattlePix.Editor.HeadlessRunner.Run
    ///         -seeds 1-100 [-ticks 30000] -out Reports/run
    ///   ... -replay &lt;файл&gt; [-ignoreDataHash] [-human N]
    ///   ... -determinism &lt;сид&gt; [-ticks N]
    ///   ... -recordReplay &lt;файл&gt; [-seed N] [-ticks N] [-commands 200] [-human N] — синтетический реплей для проверки
    ///   -safetyChecks — включить проверки безопасности Burst (по умолчанию выключены: замер тика как в игроке).
    /// Партии идут с правилами из Data/. -ticks — страховочный лимит: партию заканчивают правила.
    /// Отладка: -crashSeed N [-crashTick T] бросает искусственное исключение в партии с сидом N.
    /// Коды выхода: 0 — всё прошло; 1 — крах или расхождение; 2 — реплей от других данных.
    /// </summary>
    public static class HeadlessRunner
    {
        public const double TickBudgetMs = 0.25;
        /// <summary>Страховочный лимит по умолчанию: 50 минут. Упёрлись — это баг правил.</summary>
        public const int DefaultTickLimit = 30000;

        /// <summary>Были ли включены проверки безопасности Burst в этом прогоне.</summary>
        public static bool SafetyChecks { get; private set; }

        public static void Run()
        {
            int code;
            try
            {
                code = RunWithArgs(Environment.GetCommandLineArgs());
            }
            catch (Exception e)
            {
                Log("FATAL: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
            else Log($"exit code would be {code}");
        }

        public static int RunWithArgs(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // точки в числах отчёта на любой локали
            var a = new Args(args);
            // Проверки безопасности Burst в редакторе на порядок замедляют горячие джобы (в игроке их нет).
            // Для замера бюджета тика они выключены; -safetyChecks включает их для отладки краха.
            bool safety = a.Has("-safetyChecks");
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = safety;
            SafetyChecks = safety;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var data = GameData.LoadFromDisk(GameData.DataDir);
            ulong dataHash = data.DataHash;
            var config = data.Config();
            var rules = data.Rules();

            Log($"Unity {Application.unityVersion}, build {BuildInfo.Version}, dataHash {Fnv1a64.ToHex(dataHash)}");

            if (a.Has("-replay")) return RunReplay(a, projectRoot, config, rules, dataHash);
            if (a.Has("-determinism")) return RunDeterminism(a, config, rules);
            if (a.Has("-recordReplay")) return RecordReplay(a, projectRoot, config, rules, dataHash);
            return RunBatch(a, projectRoot, config, rules, dataHash);
        }

        // ---------- record (отладка: синтетический реплей со случайными командами человека) ----------

        static int RecordReplay(Args a, string projectRoot, SimConfig config, Rules rules, ulong dataHash)
        {
            string path = ResolveOut(projectRoot, a.Get("-recordReplay", "Replays/synthetic.json"));
            ulong seed = ulong.Parse(a.Get("-seed", "42"), CultureInfo.InvariantCulture);
            int ticks = a.GetInt("-ticks", DefaultTickLimit);
            int count = a.GetInt("-commands", 200);
            byte human = (byte)a.GetInt("-human", 0);
            using (var sim = new Simulation(config, rules, seed, ticks, human))
            {
                // Отдельный генератор только для выбора команд; на мир он не влияет.
                var rng = new Pcg32(seed, 0xC0FFEE);
                var perTick = new int[ticks];
                for (int i = 0; i < count; i++) perTick[rng.NextInt(ticks - 1)]++;
                while (!sim.World.IsOver)
                {
                    for (int k = 0; k < perTick[sim.World.Tick]; k++)
                        sim.EnqueueFromUi(CommandType.Noop, human, rng.NextInt(sim.World.PixelCount), 0);
                    sim.Step();
                }
                var replay = ReplayFile.Capture(sim, BuildInfo.Version, dataHash);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, replay.ToJson());
                Log($"recorded {replay.Commands.Count} commands, {replay.Checkpoints.Count} checkpoints -> {path}");
            }
            return 0;
        }

        // ---------- batch ----------

        static int RunBatch(Args a, string projectRoot, SimConfig config, Rules rules, ulong dataHash)
        {
            var seeds = ParseSeeds(a.Get("-seeds", "1-100"));
            int ticks = a.GetInt("-ticks", DefaultTickLimit);
            string outDir = ResolveOut(projectRoot, a.Get("-out", "Reports/run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            ulong? crashSeed = a.Has("-crashSeed") ? ulong.Parse(a.Get("-crashSeed", "0"), CultureInfo.InvariantCulture) : (ulong?)null;
            int crashTick = a.GetInt("-crashTick", ticks / 2);

            Log($"batch: {seeds.Count} matches x {ticks} ticks -> {outDir}");
            var info = new RunInfo
            {
                UnityVersion = Application.unityVersion,
                Build = BuildInfo.Version,
                DataHash = Fnv1a64.ToHex(dataHash),
                Ticks = ticks,
                TickBudgetMs = TickBudgetMs,
                BurstSafetyChecks = SafetyChecks,
                StartedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            };

            var total = Stopwatch.StartNew();
            var results = new List<MatchResult>(seeds.Count);
            foreach (var seed in seeds)
            {
                var r = PlayMatch(config, rules, seed, ticks, crashSeed == seed ? crashTick : -1);
                results.Add(r);
                Log(r.Crashed
                    ? $"seed {seed}: CRASH at tick {r.Ticks}: {r.Error}"
                    : $"seed {seed}: {r.Ticks} ticks, hash {r.FinalHash}, {r.MsPerTickMean:0.0000} ms/tick (p99 {r.MsPerTickP99:0.0000})");
            }
            total.Stop();
            info.TotalSeconds = total.Elapsed.TotalSeconds;

            Directory.CreateDirectory(outDir);
            IMatchReporter[] reporters = { new JsonReporter(), new MarkdownReporter() };
            foreach (var rep in reporters) rep.Write(outDir, info, results);

            int crashes = results.Count(r => r.Crashed);
            Log($"done: {results.Count} matches, {crashes} crashed, {info.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s. Report: {outDir}");
            return crashes > 0 ? 1 : 0;
        }

        public static MatchResult PlayMatch(SimConfig config, Rules rules, ulong seed, int ticks, int crashAtTick = -1)
        {
            var r = new MatchResult { Seed = seed };
            var perTick = new List<double>(ticks);
            var sw = new Stopwatch();
            var total = Stopwatch.StartNew();
            Simulation sim = null;
            try
            {
                sim = new Simulation(config, rules, seed, ticks, 0);
                var mg = sim.World.MapGen;
                r.LandShare_bp = mg.LandShare_bp;
                r.MapFairness_bp = mg.Fairness_bp;
                r.MapRetryFail = mg.MapRetryFail;
                r.UnfairStart = mg.UnfairStart;
                while (!sim.World.IsOver)
                {
                    if (sim.World.Tick == crashAtTick)
                        throw new InvalidOperationException($"artificial crash (-crashSeed {seed}) at tick {crashAtTick}");
                    sw.Restart();
                    sim.Step();
                    sw.Stop();
                    // Тик с контрольным хешем в бюджет не входит.
                    if (!sim.LastStepWroteCheckpoint) perTick.Add(sw.Elapsed.TotalMilliseconds);
                }
                r.Ticks = sim.World.Tick;
                r.FinalHash = Fnv1a64.ToHex(sim.LastHash);
                r.EndReason = sim.World.EndReason.ToString();
                r.Checkpoints = sim.Checkpoints.Count;
                r.PixelsByNation = CountPixels(sim.World);
            }
            catch (Exception e)
            {
                r.Crashed = true;
                r.Ticks = sim?.World.Tick ?? 0;
                r.Error = e.Message;
                r.Stack = e.ToString();
            }
            finally
            {
                sim?.Dispose();
                total.Stop();
            }
            r.TimeMs = total.Elapsed.TotalMilliseconds;
            if (perTick.Count > 0)
            {
                r.MsPerTickMean = perTick.Average();
                perTick.Sort();
                r.MsPerTickP99 = perTick[Math.Min(perTick.Count - 1, (int)Math.Ceiling(perTick.Count * 0.99) - 1)];
            }
            return r;
        }

        static int[] CountPixels(World w)
        {
            var counts = new int[w.NationCount + 1];
            var owner = w.Owner;
            for (int i = 0; i < owner.Length; i++)
            {
                int o = owner[i];
                if (o < counts.Length) counts[o]++;
            }
            return counts;
        }

        // ---------- replay ----------

        static int RunReplay(Args a, string projectRoot, SimConfig config, Rules rules, ulong dataHash)
        {
            string path = ResolveOut(projectRoot, a.Get("-replay", ""));
            Log($"replay: {path}");
            ReplayFile replay;
            try
            {
                replay = ReplayFile.FromJson(File.ReadAllText(path));
            }
            catch (UnsupportedReplayFormatException e)
            {
                Log("REPLAY REJECTED: " + e.Message);
                return 2;
            }
            if (a.Has("-human")) replay.Human = (byte)a.GetInt("-human", replay.Human);
            Log($"replay build {replay.Build}, seed {replay.Seed}, ticks {replay.Ticks}, human {replay.Human}, " +
                $"{replay.Commands.Count} commands, {replay.Checkpoints.Count} checkpoints");

            if (!string.Equals(replay.DataHash, Fnv1a64.ToHex(dataHash), StringComparison.OrdinalIgnoreCase))
            {
                Log($"WARNING: dataHash mismatch: replay {replay.DataHash}, current {Fnv1a64.ToHex(dataHash)}");
                if (!a.Has("-ignoreDataHash")) return 2;
            }

            var r = ReplayPlayer.Verify(replay, config, rules);
            if (r.Ok)
            {
                Log($"REPLAY OK: {r.CheckpointsCompared} checkpoints match, final hash {Fnv1a64.ToHex(r.FinalHash)}");
                return 0;
            }
            if (r.Error != null)
            {
                Log("REPLAY FAILED: " + r.Error);
                return 1;
            }
            Log($"REPLAY DIVERGED: first divergent checkpoint at tick {r.FirstDivergentTick} " +
                $"(last match at tick {r.LastMatchingTick}): expected {Fnv1a64.ToHex(r.Expected)}, got {Fnv1a64.ToHex(r.Actual)}");
            return 1;
        }

        // ---------- determinism ----------

        static int RunDeterminism(Args a, SimConfig config, Rules rules)
        {
            ulong seed = ulong.Parse(a.Get("-determinism", "42"), CultureInfo.InvariantCulture);
            int ticks = a.GetInt("-ticks", DefaultTickLimit);
            Log($"determinism: seed {seed}, {ticks} ticks, two runs");

            List<Checkpoint> RunOnce()
            {
                using (var sim = new Simulation(config, rules, seed, ticks, 0))
                {
                    while (!sim.World.IsOver) sim.Step();
                    return new List<Checkpoint>(sim.Checkpoints);
                }
            }

            var first = RunOnce();
            var second = RunOnce();
            if (first.Count != second.Count)
            {
                Log($"DETERMINISM FAILED: checkpoint count {first.Count} vs {second.Count}");
                return 1;
            }
            for (int i = 0; i < first.Count; i++)
            {
                if (first[i].Tick != second[i].Tick || first[i].Hash != second[i].Hash)
                {
                    Log($"DETERMINISM FAILED: first divergent checkpoint at tick {first[i].Tick}: " +
                        $"{Fnv1a64.ToHex(first[i].Hash)} vs {Fnv1a64.ToHex(second[i].Hash)}");
                    return 1;
                }
            }
            Log($"DETERMINISM OK: {first.Count} checkpoints equal, final {Fnv1a64.ToHex(first[first.Count - 1].Hash)}");
            return 0;
        }

        // ---------- helpers ----------

        static string ResolveOut(string projectRoot, string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectRoot, path));

        public static List<ulong> ParseSeeds(string spec)
        {
            var seeds = new List<ulong>();
            foreach (var part in spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    ulong from = ulong.Parse(p.Substring(0, dash), CultureInfo.InvariantCulture);
                    ulong to = ulong.Parse(p.Substring(dash + 1), CultureInfo.InvariantCulture);
                    for (ulong s = from; s <= to; s++)
                    {
                        seeds.Add(s);
                        if (s == ulong.MaxValue) break;
                    }
                }
                else seeds.Add(ulong.Parse(p, CultureInfo.InvariantCulture));
            }
            return seeds;
        }

        static void Log(string s)
        {
            // В batchmode Debug.Log попадает в лог Unity, Console — в stdout.
            Console.WriteLine("[BattlePix] " + s);
            Debug.Log("[BattlePix] " + s);
        }

        sealed class Args
        {
            readonly string[] _args;
            public Args(string[] args) { _args = args; }

            public bool Has(string key) => Array.IndexOf(_args, key) >= 0;

            public string Get(string key, string fallback)
            {
                int i = Array.IndexOf(_args, key);
                return i >= 0 && i + 1 < _args.Length && !_args[i + 1].StartsWith("-") ? _args[i + 1] : fallback;
            }

            public int GetInt(string key, int fallback) =>
                int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }

    // ---------- отчёт ----------

    public sealed class RunInfo
    {
        public string UnityVersion;
        public string Build;
        public string DataHash;
        public int Ticks;
        public double TickBudgetMs;
        public bool BurstSafetyChecks;
        public string StartedAt;
        public double TotalSeconds;
    }

    /// <summary>Итог одной партии.</summary>
    public sealed class MatchResult
    {
        public ulong Seed;
        /// <summary>Доля суши континента, bp.</summary>
        public int LandShare_bp;
        /// <summary>max/min стартовых ячеек при генерации, bp.</summary>
        public int MapFairness_bp;
        public bool MapRetryFail;
        public bool UnfairStart;
        public int Ticks;
        public string FinalHash;
        public string EndReason;
        public int Checkpoints;
        public double TimeMs;
        public double MsPerTickMean;
        public double MsPerTickP99;
        /// <summary>Индекс 0 — ничья земля, дальше по id наций.</summary>
        public int[] PixelsByNation;
        public bool Crashed;
        public string Error;
        public string Stack;
    }

    public interface IMatchReporter
    {
        void Write(string outDir, RunInfo info, IReadOnlyList<MatchResult> results);
    }

    public static class RunSummary
    {
        public static (int crashed, double mean, double p99) Compute(IReadOnlyList<MatchResult> results)
        {
            var ok = results.Where(r => !r.Crashed && r.MsPerTickMean > 0).ToList();
            int crashed = results.Count(r => r.Crashed);
            double mean = ok.Count > 0 ? ok.Average(r => r.MsPerTickMean) : 0;
            double p99 = ok.Count > 0 ? ok.Max(r => r.MsPerTickP99) : 0;
            return (crashed, mean, p99);
        }
    }

    public sealed class JsonReporter : IMatchReporter
    {
        public void Write(string outDir, RunInfo info, IReadOnlyList<MatchResult> results)
        {
            var (crashed, mean, p99) = RunSummary.Compute(results);
            var doc = new
            {
                info,
                summary = new
                {
                    matches = results.Count,
                    crashed,
                    msPerTickMean = mean,
                    msPerTickP99Max = p99,
                    withinBudget = mean <= info.TickBudgetMs,
                },
                matches = results,
            };
            File.WriteAllText(Path.Combine(outDir, "report.json"), JsonConvert.SerializeObject(doc, Formatting.Indented));
        }
    }

    public sealed class MarkdownReporter : IMatchReporter
    {
        public void Write(string outDir, RunInfo info, IReadOnlyList<MatchResult> results)
        {
            var (crashed, mean, p99) = RunSummary.Compute(results);
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# Отчёт бот-прогона");
            sb.AppendLine();
            sb.AppendLine($"- Unity: {info.UnityVersion}, сборка: {info.Build}, dataHash: {info.DataHash}");
            sb.AppendLine($"- Старт: {info.StartedAt}");
            sb.AppendLine($"- Партий: {results.Count}, упало: {crashed}");
            sb.AppendLine($"- Страховочный лимит тиков: {info.Ticks}");
            sb.AppendLine($"- Проверки безопасности Burst: {(info.BurstSafetyChecks ? "включены (замер тика завышен)" : "выключены, как в игроке")}");
            sb.AppendLine(string.Format(inv, "- Общее время: {0:0.0} с", info.TotalSeconds));
            sb.AppendLine(string.Format(inv, "- Средний тик: {0:0.0000} мс, худший p99 по партиям: {1:0.0000} мс", mean, p99));
            sb.AppendLine(string.Format(inv, "- Бюджет тика {0} мс: {1}", info.TickBudgetMs,
                mean <= info.TickBudgetMs ? "укладываемся" : "**НЕ укладываемся**"));
            sb.AppendLine();
            if (crashed > 0)
            {
                sb.AppendLine("## Крахи");
                sb.AppendLine();
                foreach (var r in results.Where(r => r.Crashed))
                {
                    sb.AppendLine($"### Сид {r.Seed}, тик {r.Ticks}");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(r.Stack);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
            sb.AppendLine("## Партии");
            sb.AppendLine();
            sb.AppendLine("| Сид | Тики | Финальный хеш | Время, мс | мс/тик | p99 | Итог |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var r in results)
                sb.AppendLine(string.Format(inv, "| {0} | {1} | {2} | {3:0} | {4:0.0000} | {5:0.0000} | {6} |",
                    r.Seed, r.Ticks, r.FinalHash ?? "—", r.TimeMs, r.MsPerTickMean, r.MsPerTickP99,
                    r.Crashed ? "крах" : r.EndReason));
            File.WriteAllText(Path.Combine(outDir, "report.md"), sb.ToString());
        }
    }
}
