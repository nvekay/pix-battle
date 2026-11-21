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
    ///   ... -seeds 1-100 -profile 1=spread [-baseline Reports/run/report.json] — абляция профиля ИИ
    ///   ... -analyze &lt;реплей&gt; [-out папка] — разбор партии человека, пишет analysis.md
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

            if (a.Has("-analyze")) return Analyzer.Run(a.Get("-analyze", ""), ResolveOut(projectRoot, a.Get("-analyze", "")),
                a.Has("-out") ? ResolveOut(projectRoot, a.Get("-out", "")) : null, config, rules, Log);
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
                    {
                        // Случайные игровые команды человека: часть заведомо отклонится, это тоже проверка реплея.
                        int slot = rng.NextInt(4); // одна из первых четырёх дивизий
                        int enemy = 1 + rng.NextInt(rules.NationCount);
                        switch (rng.NextInt(7))
                        {
                            case 4:
                            {
                                // 02b: война с репарациями или претензиями (центр предлагает ядро, если сосед есть).
                                int center = human != 0 && rng.NextInt(2) == 0 && enemy != human
                                    ? WarSystem.SuggestClaimCenter(sim.World, human, (byte)enemy) : -1;
                                sim.EnqueueFromUi(CommandType.DeclareWar, human, enemy, center >= 0 ? center + 1 : 0);
                                break;
                            }
                            case 5: sim.EnqueueFromUi(CommandType.ProposePeace, human, enemy, 0); break;
                            case 6: sim.EnqueueFromUi(CommandType.AnswerPeace, human, enemy, rng.NextInt(2)); break;
                            case 0: sim.EnqueueFromUi(CommandType.RecruitDivision, human, rng.NextInt(2), 0); break;
                            case 1: sim.EnqueueFromUi(CommandType.AssignDivision, human, slot, rng.NextInt(rules.NationCount + 1)); break;
                            case 2:
                            {
                                // Пачка: первые четыре дивизии на один фронт в один тик, как выделение в UI.
                                int target = rng.NextInt(rules.NationCount + 1);
                                for (int j = 0; j < 4; j++) sim.EnqueueFromUi(CommandType.AssignDivision, human, j, target);
                                break;
                            }
                            default: sim.EnqueueFromUi(CommandType.SetFrontOrder, human, 1 + rng.NextInt(rules.NationCount), rng.NextInt(2)); break;
                        }
                    }
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

            // -profile 1=spread[,3=no_armor]: назначение профилей поверх ai.json.
            var overrides = new List<KeyValuePair<int, string>>();
            foreach (var part in a.Get("-profile", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2 || !int.TryParse(kv[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    throw new ArgumentException("-profile expects <nationId>=<profile>, got " + part);
                overrides.Add(new KeyValuePair<int, string>(id, kv[1]));
            }
            var runRules = rules;
            if (overrides.Count > 0)
            {
                runRules = rules.Clone();
                foreach (var o in overrides) runRules.AssignProfile(o.Key, o.Value);
            }

            var info = NewInfo(ticks, dataHash);
            info.Profiles = Enumerable.Range(1, runRules.NationCount).Select(id => runRules.ProfileName((byte)id)).ToArray();

            // Абляция сравнивается с прогоном без неё: из -baseline или прогоном тут же.
            List<MatchResult> baseline = null;
            if (overrides.Count > 0)
            {
                string basePath = a.Get("-baseline", "");
                if (basePath != "")
                {
                    baseline = JsonReporter.ReadMatches(ResolveOut(projectRoot, basePath));
                    info.BaselineSource = basePath;
                }
                else
                {
                    Log($"baseline: {seeds.Count} matches without -profile");
                    baseline = PlayAll(config, rules, seeds, ticks, null, -1);
                    info.BaselineSource = "прогон без абляции на тех же сидах";
                }
            }

            Log($"batch: {seeds.Count} matches, tick limit {ticks} -> {outDir}");
            var total = Stopwatch.StartNew();
            var results = PlayAll(config, runRules, seeds, ticks, crashSeed, crashTick);
            total.Stop();
            info.TotalSeconds = total.Elapsed.TotalSeconds;

            Directory.CreateDirectory(outDir);
            var ctx = new ReportContext { Info = info, Results = results, Baseline = baseline, Overrides = overrides };
            IMatchReporter[] reporters = { new JsonReporter(), new MarkdownReporter() };
            foreach (var rep in reporters) rep.Write(outDir, ctx);

            int crashes = results.Count(r => r.Crashed);
            Log($"done: {results.Count} matches, {crashes} crashed, {info.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s. Report: {outDir}");
            return crashes > 0 ? 1 : 0;
        }

        static RunInfo NewInfo(int ticks, ulong dataHash) => new RunInfo
        {
            UnityVersion = Application.unityVersion,
            Build = BuildInfo.Version,
            DataHash = Fnv1a64.ToHex(dataHash),
            Ticks = ticks,
            TickBudgetMs = TickBudgetMs,
            BurstSafetyChecks = SafetyChecks,
            StartedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
        };

        static List<MatchResult> PlayAll(SimConfig config, Rules rules, List<ulong> seeds, int ticks, ulong? crashSeed, int crashTick)
        {
            var results = new List<MatchResult>(seeds.Count);
            foreach (var seed in seeds)
            {
                var r = PlayMatch(config, rules, seed, ticks, crashSeed == seed ? crashTick : -1);
                results.Add(r);
                Log(r.Crashed
                    ? $"seed {seed}: CRASH at tick {r.Ticks}: {r.Error}"
                    : $"seed {seed}: {r.EndReason} winner {r.Winner} at {r.EndMinutes:0.0} min, {r.MsPerTickMean:0.0000} ms/tick (p99 {r.MsPerTickP99:0.0000}, peace max {r.PeaceTickMaxMs:0.000})");
            }
            return results;
        }

        public static readonly int[] LeaderMinutes = { 10, 20, 30 };

        public static MatchResult PlayMatch(SimConfig config, Rules rules, ulong seed, int ticks, int crashAtTick = -1)
        {
            var r = new MatchResult { Seed = seed, FirstContactTick = -1, StartFairness_bp = -1 };
            var perTick = new List<double>(ticks);
            var sw = new Stopwatch();
            var total = Stopwatch.StartNew();
            Simulation sim = null;
            try
            {
                sim = new Simulation(config, rules, seed, ticks, 0);
                var w = sim.World;
                var mg = w.MapGen;
                r.LandShare_bp = mg.LandShare_bp;
                r.MapFairness_bp = mg.Fairness_bp;
                r.MapRetryFail = mg.MapRetryFail;
                r.UnfairStart = mg.UnfairStart;
                r.LeaderShare_bp = new int[LeaderMinutes.Length];
                int tps = config.TicksPerSecond;
                int[] neighbours5 = null;
                int nationCount = w.NationCount;
                var aliveSec = new int[nationCount];
                var attackSec = new int[nationCount];
                var focusSec = new int[nationCount];
                var peakDivisions = new int[nationCount];
                var exhaustedSec = new int[nationCount];
                var rotations = new int[nationCount];
                while (!w.IsOver)
                {
                    if (w.Tick == crashAtTick)
                        throw new InvalidOperationException($"artificial crash (-crashSeed {seed}) at tick {crashAtTick}");
                    int eventsBefore = w.WarEvents.Count;
                    sw.Restart();
                    sim.Step();
                    sw.Stop();
                    // Тик с контрольным хешем в бюджет не входит.
                    if (!sim.LastStepWroteCheckpoint) perTick.Add(sw.Elapsed.TotalMilliseconds);
                    // Тик мира (02b §6): проход по карте и карманы — максимум отдельной строкой.
                    for (int e = eventsBefore; e < w.WarEvents.Count; e++)
                        if (w.WarEvents[e].Kind == WarEventKind.Peace && w.WarEvents[e].Tick == w.Tick - 1)
                        {
                            double ms = sw.Elapsed.TotalMilliseconds;
                            if (ms > r.PeaceTickMaxMs) r.PeaceTickMaxMs = ms;
                            break;
                        }

                    if (r.FirstContactTick < 0 && AnyContact(w)) r.FirstContactTick = w.Tick;
                    for (int i = 0; i < nationCount; i++)
                        if (w.Nations[i].Alive != 0) rotations[i] += ArmyStats.RotationsThisTick(w, (byte)(i + 1));
                    if (w.Tick % tps == 0)
                        for (int i = 0; i < nationCount; i++)
                        {
                            var nat = w.Nations[i];
                            if (nat.Alive == 0) continue;
                            aliveSec[i]++;
                            if (nat.Divisions > peakDivisions[i]) peakDivisions[i] = nat.Divisions;
                            FocusState(w, (byte)(i + 1), out bool attacking, out bool focus);
                            if (attacking) attackSec[i]++;
                            if (attacking && ArmyStats.AllAttackersExhausted(w, (byte)(i + 1))) exhaustedSec[i]++;
                            if (focus) focusSec[i]++;
                        }
                    if (w.Tick == 300 * tps)
                    {
                        r.StartFairness_bp = PixelRatio(w);
                        neighbours5 = Neighbours(w);
                    }
                    for (int m = 0; m < LeaderMinutes.Length; m++)
                        if (w.Tick == LeaderMinutes[m] * 60 * tps) r.LeaderShare_bp[m] = LeaderShare(w);
                }
                // Партия кончилась раньше отметки — доля лидера на ней равна финальной.
                for (int m = 0; m < LeaderMinutes.Length; m++)
                    if (w.Tick < LeaderMinutes[m] * 60 * tps) r.LeaderShare_bp[m] = LeaderShare(w);
                if (r.StartFairness_bp < 0) r.StartFairness_bp = PixelRatio(w);
                if (neighbours5 == null) neighbours5 = Neighbours(w);

                r.Ticks = w.Tick;
                r.EndTick = w.Tick;
                r.EndMinutes = w.Tick / (60.0 * tps);
                r.FinalHash = Fnv1a64.ToHex(sim.LastHash);
                r.EndReason = w.EndReason.ToString();
                r.Winner = w.Winner;
                r.Checkpoints = sim.Checkpoints.Count;
                r.PixelsByNation = CountPixels(w);
                r.Nations = new NationResult[w.NationCount];
                for (int i = 0; i < w.NationCount; i++)
                {
                    var n = w.Nations[i];
                    r.Nations[i] = new NationResult
                    {
                        Id = n.Id,
                        Profile = rules.ProfileName(n.Id),
                        DiedAtTick = n.DiedAtTick,
                        Pixels = n.Pixels,
                        RecruitedInfDiv = n.RecruitedInfDiv,
                        RecruitedArmorDiv = n.RecruitedArmorDiv,
                        DivisionsLost = n.DivisionsLost,
                        PeakDivisions = peakDivisions[i],
                        AttackTime_bp = aliveSec[i] > 0 ? (int)((long)attackSec[i] * 10000 / aliveSec[i]) : 0,
                        FocusTime_bp = aliveSec[i] > 0 ? (int)((long)focusSec[i] * 10000 / aliveSec[i]) : 0,
                        ExhaustedTime_bp = attackSec[i] > 0 ? (int)((long)exhaustedSec[i] * 10000 / attackSec[i]) : 0,
                        Rotations = rotations[i],
                        NeighboursAt5 = neighbours5[i],
                    };
                }
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

        /// <summary>Сколько живых соседей с общей границей у каждой нации (индекс id − 1).</summary>
        /// <summary>
        /// attacking — у нации есть фронт с приказом «наступление» и дивизиями на месте. focus — такие фронты есть,
        /// но не все существующие (спека 01b §8, 01c §7: по FrontOrder и дивизиям на месте).
        /// </summary>
        public static void FocusState(World w, byte self, out bool attacking, out bool focus)
        {
            int n = w.NationCount, fronts = 0, attackedFronts = 0;
            for (int d = 1; d <= n; d++)
            {
                if (d == self || w.FrontLength(self, d) <= 0 || w.Nation((byte)d).Alive == 0) continue;
                fronts++;
                if (w.FrontOrderOf(self, d) != Orders.Attack) continue;
                for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                {
                    var v = w.DivisionAt(w.DivisionSlot(self, k));
                    if (w.OnFront(v) && v.Front == d)
                    {
                        attackedFronts++;
                        break;
                    }
                }
            }
            attacking = attackedFronts > 0;
            focus = attackedFronts > 0 && attackedFronts < fronts;
        }

        static int[] Neighbours(World w)
        {
            var r = new int[w.NationCount];
            for (int a = 1; a <= w.NationCount; a++)
            for (int d = 1; d <= w.NationCount; d++)
                if (a != d && w.Nations[d - 1].Alive != 0 && w.FrontLength(a, d) > 0) r[a - 1]++;
            return r;
        }

        static bool AnyContact(World w)
        {
            for (int a = 1; a <= w.NationCount; a++)
            for (int d = a + 1; d <= w.NationCount; d++)
                if (w.FrontLength(a, d) > 0) return true;
            return false;
        }

        /// <summary>max/min Pixels по всем нациям, bp. Нация без земли — int.MaxValue.</summary>
        static int PixelRatio(World w)
        {
            int min = int.MaxValue, max = 0;
            for (int i = 0; i < w.NationCount; i++)
            {
                int p = w.Nations[i].Pixels;
                if (p < min) min = p;
                if (p > max) max = p;
            }
            return min > 0 ? (int)((long)max * 10000 / min) : int.MaxValue;
        }

        static int LeaderShare(World w)
        {
            int max = 0;
            for (int i = 0; i < w.NationCount; i++)
                if (w.Nations[i].Alive != 0 && w.Nations[i].Pixels > max) max = w.Nations[i].Pixels;
            return w.TotalLand > 0 ? (int)((long)max * 10000 / w.TotalLand) : 0;
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

        internal static void Log(string s)
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
        /// <summary>Профиль ИИ каждой нации в этом прогоне, по id.</summary>
        public string[] Profiles;
        public string BaselineSource;
    }

    /// <summary>Счётчики армии для отчёта и -analyze (спека 01c §7). Только читают мир.</summary>
    public static class ArmyStats
    {
        /// <summary>
        /// Сколько дивизий нации пришли на фронт в этот тик (ArriveTick == Tick, не выдохлись), где уже стоит выдохшаяся своя.
        /// Вызывать после каждого Step.
        /// </summary>
        public static int RotationsThisTick(World w, byte self)
        {
            int c = 0, max = w.MaxDivisionsPerNation;
            for (int k = 0; k < max; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (!w.OnFront(v) || v.ArriveTick != w.Tick || v.Exhausted != 0) continue;
                for (int j = 0; j < max; j++)
                {
                    if (j == k) continue;
                    var u = w.DivisionAt(w.DivisionSlot(self, j));
                    if (w.OnFront(u) && u.Front == v.Front && u.Exhausted != 0) { c++; break; }
                }
            }
            return c;
        }

        /// <summary>Есть дивизии на месте на фронтах с приказом «наступление», и все они выдохлись.</summary>
        public static bool AllAttackersExhausted(World w, byte self)
        {
            int attackers = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (!w.OnFront(v) || w.FrontOrderOf(self, v.Front) != Orders.Attack) continue;
                if (v.Exhausted == 0) return false;
                attackers++;
            }
            return attackers > 0;
        }
    }

    public sealed class NationResult
    {
        public int Id;
        public string Profile;
        public int DiedAtTick;
        public int Pixels;
        public int RecruitedInfDiv;
        public int RecruitedArmorDiv;
        public int DivisionsLost;
        public int PeakDivisions;
        /// <summary>Доля секунд жизни, когда есть фронт с приказом «наступление» и дивизиями на месте, bp.</summary>
        public int AttackTime_bp;
        /// <summary>Доля секунд жизни с «фокусом»: наступление на одних фронтах, а не на всех существующих, bp.</summary>
        public int FocusTime_bp;
        /// <summary>Доля секунд в наступлении, когда все наступающие дивизии нации выдохлись, bp (01c §7).</summary>
        public int ExhaustedTime_bp;
        /// <summary>Сколько раз свежая дивизия пришла на фронт, где стояла выдохшаяся.</summary>
        public int Rotations;
        /// <summary>Живых соседей с общей границей на 5-й минуте.</summary>
        public int NeighboursAt5;
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
        public int Winner;
        public int EndTick;
        public double EndMinutes;
        /// <summary>Доля лидера от суши на 10, 20 и 30 минуте, bp (если партия кончилась раньше — финальная).</summary>
        public int[] LeaderShare_bp;
        public int FirstContactTick;
        /// <summary>max/min Pixels наций на тике 3000 (5-я минута), bp.</summary>
        public int StartFairness_bp;
        public NationResult[] Nations;
        public int Checkpoints;
        public double TimeMs;
        public double MsPerTickMean;
        public double MsPerTickP99;
        /// <summary>Самый долгий тик, в котором заключён мир (02b §6), мс; 0 — мира не было.</summary>
        public double PeaceTickMaxMs;
        /// <summary>Индекс 0 — ничья земля и вода, дальше по id наций.</summary>
        public int[] PixelsByNation;
        public bool Crashed;
        public string Error;
        public string Stack;
    }

    public sealed class ReportContext
    {
        public RunInfo Info;
        public IReadOnlyList<MatchResult> Results;
        /// <summary>Прогон без абляции для сравнения (null, если абляции нет).</summary>
        public IReadOnlyList<MatchResult> Baseline;
        public List<KeyValuePair<int, string>> Overrides = new List<KeyValuePair<int, string>>();
    }

    public interface IMatchReporter
    {
        void Write(string outDir, ReportContext ctx);
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

    /// <summary>Строка сводки ворот 1: что меряем, цель, факт, ок/провал.</summary>
    public struct GateRow
    {
        public string Name, Target, Actual;
        public bool Ok;
    }

    public static class Gate1
    {
        public const double MeanTickMs = 0.4, P99TickMs = 2.0;

        static bool Ended(MatchResult r) => !r.Crashed && (r.EndReason == "Conquest" || r.EndReason == "Timeout");

        public static List<GateRow> Rows(IReadOnlyList<MatchResult> results)
        {
            var inv = CultureInfo.InvariantCulture;
            int n = results.Count;
            string Pct(int k) => n > 0 ? string.Format(inv, "{0}/{1} ({2:0}%)", k, n, 100.0 * k / n) : "—";
            var rows = new List<GateRow>();

            int ended = results.Count(Ended);
            rows.Add(new GateRow { Name = "Партии с концом (Conquest, Timeout; не TickLimit и не крах)", Target = "100%", Actual = Pct(ended), Ok = ended == n });

            int inLen = results.Count(r => Ended(r) && r.EndMinutes >= 25 && r.EndMinutes <= 45);
            rows.Add(new GateRow { Name = "Длина 25–45 мин", Target = "≥ 80% партий", Actual = Pct(inLen), Ok = inLen * 10 >= n * 8 });

            int early = results.Count(r => !r.Crashed && r.EndMinutes < 10);
            rows.Add(new GateRow { Name = "Конец до 10-й минуты", Target = "0 партий", Actual = early.ToString(inv), Ok = early == 0 });

            int lead = results.Count(r => !r.Crashed && r.LeaderShare_bp != null && r.LeaderShare_bp[0] <= 2500);
            rows.Add(new GateRow { Name = "Доля лидера на 10-й минуте ≤ 25%", Target = "≥ 90% партий", Actual = Pct(lead), Ok = lead * 10 >= n * 9 });

            int timeout = results.Count(r => r.EndReason == "Timeout");
            rows.Add(new GateRow { Name = "Партии по таймеру (справочно до ворот 2)", Target = "≤ 10%", Actual = Pct(timeout), Ok = timeout * 10 <= n });

            int fair = results.Count(r => !r.Crashed && r.StartFairness_bp <= 20000);
            rows.Add(new GateRow { Name = "Честность стартов (max/min на 5-й минуте ≤ 2,0×)", Target = "≥ 90% партий", Actual = Pct(fair), Ok = fair * 10 >= n * 9 });

            var wins = results.Where(r => Ended(r) && r.Winner > 0).GroupBy(r => r.Winner).OrderByDescending(g => g.Count()).FirstOrDefault();
            int top = wins?.Count() ?? 0;
            rows.Add(new GateRow
            {
                Name = "Победы одного id нации",
                Target = "≤ 25% (ожидание 12,5%)",
                Actual = wins == null ? "—" : string.Format(inv, "нация {0}: {1}", wins.Key, Pct(top)),
                Ok = top * 4 <= n,
            });

            var (_, mean, p99) = RunSummary.Compute(results);
            rows.Add(new GateRow
            {
                Name = "Средний и p99 тик",
                Target = "≤ 0,4 / ≤ 2 мс",
                Actual = string.Format(inv, "{0:0.000} / {1:0.000} мс", mean, p99),
                Ok = mean <= MeanTickMs && p99 <= P99TickMs,
            });
            double peaceMax = results.Where(r => !r.Crashed).Select(r => r.PeaceTickMaxMs).DefaultIfEmpty(0).Max();
            rows.Add(new GateRow
            {
                Name = "Максимум тика мира (02b §6)",
                Target = "≤ 2 мс",
                Actual = string.Format(inv, "{0:0.000} мс", peaceMax),
                Ok = peaceMax <= P99TickMs,
            });
            return rows;
        }

        public static int WinsOf(IReadOnlyList<MatchResult> results, int nation) =>
            results.Count(r => Ended(r) && r.Winner == nation);
    }

    public sealed class JsonReporter : IMatchReporter
    {
        public void Write(string outDir, ReportContext ctx)
        {
            var (crashed, mean, p99) = RunSummary.Compute(ctx.Results);
            var doc = new
            {
                info = ctx.Info,
                summary = new
                {
                    matches = ctx.Results.Count,
                    crashed,
                    msPerTickMean = mean,
                    msPerTickP99Max = p99,
                    withinBudget = mean <= ctx.Info.TickBudgetMs,
                    gate1 = Gate1.Rows(ctx.Results),
                },
                matches = ctx.Results,
            };
            File.WriteAllText(Path.Combine(outDir, "report.json"), JsonConvert.SerializeObject(doc, Formatting.Indented));
        }

        /// <summary>Партии из report.json прошлого прогона (для -baseline).</summary>
        public static List<MatchResult> ReadMatches(string path)
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return root["matches"].ToObject<List<MatchResult>>();
        }
    }

    public sealed class MarkdownReporter : IMatchReporter
    {
        public void Write(string outDir, ReportContext ctx)
        {
            var info = ctx.Info;
            var results = ctx.Results;
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
            if (info.Profiles != null) sb.AppendLine("- Профили ИИ по id: " + string.Join(", ", info.Profiles.Select((p, i) => $"{i + 1}={p}")));
            sb.AppendLine();

            sb.AppendLine("## Ворота 1");
            sb.AppendLine();
            sb.AppendLine("| Строка | Цель | Факт | Итог |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var row in Gate1.Rows(results))
                sb.AppendLine($"| {row.Name} | {row.Target} | {row.Actual} | {(row.Ok ? "ок" : "**провал**")} |");
            foreach (var o in ctx.Overrides)
            {
                int mine = Gate1.WinsOf(results, o.Key);
                string baseText = ctx.Baseline != null
                    ? string.Format(inv, "{0}/{1}", Gate1.WinsOf(ctx.Baseline, o.Key), ctx.Baseline.Count)
                    : "—";
                sb.AppendLine(string.Format(inv, "| Победы профиля {0} у нации {1} | против прогона без абляции: {2} ({3}) | {4}/{5} | сравнение |",
                    o.Value, o.Key, baseText, info.BaselineSource, mine, results.Count));
            }
            sb.AppendLine();

            var done = results.Where(r => !r.Crashed).ToList();
            if (done.Count > 0)
            {
                sb.AppendLine("## Сводка");
                sb.AppendLine();
                sb.AppendLine(string.Format(inv, "- Исходы: {0}", string.Join(", ", done.GroupBy(r => r.EndReason).Select(g => $"{g.Key} {g.Count()}"))));
                sb.AppendLine(string.Format(inv, "- Длина партии: медиана {0:0.0} мин, min {1:0.0}, max {2:0.0}",
                    Median(done.Select(r => r.EndMinutes)), done.Min(r => r.EndMinutes), done.Max(r => r.EndMinutes)));
                for (int m = 0; m < HeadlessRunner.LeaderMinutes.Length; m++)
                    sb.AppendLine(string.Format(inv, "- Доля лидера на {0}-й минуте: медиана {1:0.0}%, max {2:0.0}%", HeadlessRunner.LeaderMinutes[m],
                        Median(done.Select(r => r.LeaderShare_bp[m] / 100.0)), done.Max(r => r.LeaderShare_bp[m] / 100.0)));
                var contacts = done.Where(r => r.FirstContactTick >= 0).Select(r => r.FirstContactTick / 600.0).ToList();
                if (contacts.Count > 0) sb.AppendLine(string.Format(inv, "- Первый контакт наций: медиана {0:0.0} мин", Median(contacts)));
                sb.AppendLine(string.Format(inv, "- Честность на 5-й минуте (max/min): медиана {0:0.00}×", Median(done.Select(r => Math.Min(r.StartFairness_bp, 1000000) / 10000.0))));
                sb.AppendLine(string.Format(inv, "- Генерация: mapRetryFail {0}, unfairStart {1}", done.Count(r => r.MapRetryFail), done.Count(r => r.UnfairStart)));
                sb.AppendLine("- Победы по id: " + string.Join(", ", Enumerable.Range(1, done[0].Nations.Length).Select(id => $"{id}: {Gate1.WinsOf(done, id)}")));
                var all = done.SelectMany(r => r.Nations).ToList();
                int earlyDeath = done.Count(r => r.Nations.Any(x => x.DiedAtTick >= 0 && x.DiedAtTick < 8 * 600));
                sb.AppendLine(string.Format(inv, "- Партий со смертью столицы до 8-й минуты: {0} из {1}", earlyDeath, done.Count));
                sb.AppendLine("- По числу соседей на 5-й минуте (нации всех партий): " + string.Join("; ",
                    done.SelectMany(r => r.Nations.Select(x => (x, r))).GroupBy(t => Math.Min(t.x.NeighboursAt5, 5)).OrderBy(g => g.Key)
                        .Select(g => string.Format(inv, "{0}{1}: наций {2}, побед {3} ({4:0}%), пали до 8-й мин {5} ({6:0}%)",
                            g.Key, g.Key == 5 ? "+" : "", g.Count(), g.Count(t => t.r.Winner == t.x.Id), 100.0 * g.Count(t => t.r.Winner == t.x.Id) / g.Count(),
                            g.Count(t => t.x.DiedAtTick >= 0 && t.x.DiedAtTick < 4800), 100.0 * g.Count(t => t.x.DiedAtTick >= 0 && t.x.DiedAtTick < 4800) / g.Count()))));
                sb.AppendLine(string.Format(inv, "- На нацию за партию в среднем: набрано пехотных дивизий {0:0.0}, танковых {1:0.0}, погибло дивизий {2:0.0}, пик дивизий {3:0.0}; пали до конца партии {4:0}%",
                    all.Average(x => x.RecruitedInfDiv), all.Average(x => x.RecruitedArmorDiv), all.Average(x => x.DivisionsLost),
                    all.Average(x => x.PeakDivisions), 100.0 * all.Count(x => x.DiedAtTick >= 0) / all.Count));
                long recruited = all.Sum(x => (long)x.RecruitedInfDiv + x.RecruitedArmorDiv);
                sb.AppendLine(string.Format(inv, "- Доля танковых в наборе: {0:0.0}%", recruited > 0 ? 100.0 * all.Sum(x => (long)x.RecruitedArmorDiv) / recruited : 0));
                sb.AppendLine(string.Format(inv, "- Время в наступлении (attackTime): в среднем по нациям {0:0.0}%; фокус (focusTime): {1:0.0}%",
                    all.Average(x => x.AttackTime_bp) / 100.0, all.Average(x => x.FocusTime_bp) / 100.0));
                sb.AppendLine(string.Format(inv, "- Выдохлись (exhaustedTime, доля времени наступления, когда все наступающие выдохлись): {0:0.0}%; ротаций на нацию за партию: {1:0.0}",
                    all.Average(x => x.ExhaustedTime_bp) / 100.0, all.Average(x => x.Rotations)));
                foreach (var g in done.SelectMany(r => r.Nations).GroupBy(x => x.Profile).OrderBy(g => g.Key))
                    sb.AppendLine(string.Format(inv, "- Профиль {0}: attackTime {1:0.0}%, focusTime {2:0.0}%, exhaustedTime {3:0.0}%, ротаций {4:0.0}, танковых {5:0.0} / пехотных {6:0.0}, потери дивизий {7:0.0}",
                        g.Key, g.Average(x => x.AttackTime_bp) / 100.0, g.Average(x => x.FocusTime_bp) / 100.0,
                        g.Average(x => x.ExhaustedTime_bp) / 100.0, g.Average(x => x.Rotations),
                        g.Average(x => x.RecruitedArmorDiv), g.Average(x => x.RecruitedInfDiv), g.Average(x => x.DivisionsLost)));
                sb.AppendLine();
            }

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
            sb.AppendLine("| Сид | Итог | Победитель | Минута | Лидер 10/20/30, % | Контакт, мин | Старт 5 мин, × | Пали (id@мин) | мс/тик | p99 | Хеш |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var r in results)
            {
                if (r.Crashed)
                {
                    sb.AppendLine($"| {r.Seed} | крах | — | — | — | — | — | — | — | — | — |");
                    continue;
                }
                string died = string.Join(" ", r.Nations.Where(x => x.DiedAtTick >= 0).Select(x => string.Format(inv, "{0}@{1:0}", x.Id, x.DiedAtTick / 600.0)));
                sb.AppendLine(string.Format(inv, "| {0} | {1} | {2} | {3:0.0} | {4} | {5} | {6:0.00} | {7} | {8:0.0000} | {9:0.0000} | {10} |",
                    r.Seed, r.EndReason, r.Winner == 0 ? "—" : r.Winner.ToString(inv), r.EndMinutes,
                    string.Join(" / ", r.LeaderShare_bp.Select(x => (x / 100.0).ToString("0", inv))),
                    r.FirstContactTick >= 0 ? (r.FirstContactTick / 600.0).ToString("0.0", inv) : "—",
                    Math.Min(r.StartFairness_bp, 1000000) / 10000.0, died == "" ? "—" : died,
                    r.MsPerTickMean, r.MsPerTickP99, r.FinalHash));
            }
            File.WriteAllText(Path.Combine(outDir, "report.md"), sb.ToString());
        }

        static double Median(IEnumerable<double> xs)
        {
            var l = xs.OrderBy(x => x).ToList();
            if (l.Count == 0) return 0;
            return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }
    }
}
