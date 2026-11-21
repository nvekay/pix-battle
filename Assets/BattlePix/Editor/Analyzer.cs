using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BattlePix.Core;

namespace BattlePix.Editor
{
    /// <summary>
    /// -analyze &lt;реплей&gt;: машинная половина метрики ворот 1 по партии человека.
    /// Проигрывает реплей с текущими правилами и считает: исход и минуту, принятые команды по типам,
    /// решения за первые 10 минут (цель ≥ 10), фокус-эпизоды — отрезки ≥ 30 с, когда соединение человека наступало на одном
    /// фронте, а другой существующий фронт стоял в обороне или пустой (цель ≥ 1), и наборы под рельеф — броня при доле
    /// равнины фронта соединения ≥ 60%, пехота при ≤ 40% (цель ≥ 1). Метрика ворот 1 выполнена, если ≥ 1 по любой из двух.
    /// </summary>
    public static class Analyzer
    {
        public sealed class Result
        {
            public byte Human;
            public string Outcome;
            public double Minute;
            public bool Finished;
            public Dictionary<CommandType, int> Accepted = new Dictionary<CommandType, int>();
            public int DecisionsFirst10Min;
            public List<string> FocusEpisodes = new List<string>();
            public int ArmorOnPlains, InfantryOffPlains;
            public bool Gate => FocusEpisodes.Count >= 1 || ArmorOnPlains + InfantryOffPlains >= 1;
            public List<string> Timeline = new List<string>();
        }

        public static int Run(string arg, string path, string outDir, SimConfig config, Rules rules, Action<string> log)
        {
            var replay = ReplayFile.FromJson(File.ReadAllText(path));
            var r = Analyze(replay, config, rules);
            string md = ToMarkdown(r, Path.GetFileName(path));
            Console.WriteLine(md);
            string dir = outDir ?? Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "analysis.md"), md);
            log($"analysis -> {Path.Combine(dir, "analysis.md")}: decisions in first 10 min {r.DecisionsFirst10Min} (goal >= 10), focus episodes {r.FocusEpisodes.Count}, terrain recruits {r.ArmorOnPlains + r.InfantryOffPlains}");
            return 0;
        }

        public static Result Analyze(ReplayFile replay, SimConfig config, Rules rules)
        {
            var res = new Result { Human = replay.Human };
            if (replay.Human == 0) throw new ArgumentException("replay has no human nation (human = 0)");
            var commands = replay.Commands.Select(c => c.ToCommand()).OrderBy(c => c.Tick).ThenBy(c => c.Seq).ToList();
            int tps = config.TicksPerSecond;
            byte h = replay.Human;
            using (var sim = new Simulation(config, rules, replay.Seed, replay.TickLimit, replay.Human))
            {
                var w = sim.World;
                int next = 0, focusStart = -1;
                while (w.Tick < replay.Ticks && !w.IsOver)
                {
                    if (w.Tick % tps == 0 && w.Nation(h).Alive != 0)
                    {
                        HeadlessRunner.FocusState(w, h, out _, out bool focus);
                        if (focus && focusStart < 0) focusStart = w.Tick;
                        if (!focus && focusStart >= 0) CloseEpisode(res, focusStart, w.Tick, tps);
                        if (!focus) focusStart = -1;
                    }
                    while (next < commands.Count && commands[next].Tick == w.Tick) sim.Enqueue(commands[next++]);
                    int tick = w.Tick;
                    sim.Step();
                    foreach (var c in sim.AppliedLastTick)
                    {
                        if (c.Nation != h || !GameCommands.IsGameCommand(c.Type)) continue;
                        res.Accepted.TryGetValue(c.Type, out int k);
                        res.Accepted[c.Type] = k + 1;
                        if (tick < 600 * tps) res.DecisionsFirst10Min++;
                        string note = "";
                        if (c.Type == CommandType.RecruitDivision)
                        {
                            // Набор под рельеф (01c §7): по фронту наступления — первому существующему фронту с приказом «наступление».
                            int front = AttackFront(w, h);
                            if (front != 0)
                            {
                                int plain = AiSystem.PlainShare(w, h, front);
                                if (c.A == DivisionTypes.Armor && plain >= 6000) { res.ArmorOnPlains++; note = $" (танковая, равнина фронта наступления {plain / 100}%)"; }
                                if (c.A == DivisionTypes.Infantry && plain <= 4000) { res.InfantryOffPlains++; note = $" (пехотная, равнина фронта наступления {plain / 100}%)"; }
                            }
                        }
                        res.Timeline.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} мин — {1} {2}, {3}{4}",
                            tick / (60.0 * tps), Name(c.Type), c.A, c.B, note));
                    }
                }
                if (focusStart >= 0) CloseEpisode(res, focusStart, w.Tick, tps);
                res.Finished = w.IsOver;
                res.Minute = w.Tick / (60.0 * tps);
                res.Outcome = !w.IsOver ? "партия не доиграна"
                    : w.Winner == h ? $"победа ({w.EndReason})"
                    : $"поражение ({w.EndReason}, победитель {(w.Winner == 0 ? "—" : w.Winner.ToString())})";
            }
            return res;
        }

        static void CloseEpisode(Result res, int from, int to, int tps)
        {
            if (to - from < 30 * tps) return;
            res.FocusEpisodes.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0}–{1:0.0} мин", from / (60.0 * tps), to / (60.0 * tps)));
        }

        static int AttackFront(World w, byte h)
        {
            for (int d = 1; d <= w.NationCount; d++)
                if (d != h && w.FrontLength(h, d) > 0 && w.Nation((byte)d).Alive != 0 && w.FrontOrderOf(h, d) == Orders.Attack) return d;
            return 0;
        }

        static string Name(CommandType t) => t == CommandType.RecruitDivision ? "набор" : t == CommandType.AssignDivision ? "назначение" : "приказ";

        static string ToMarkdown(Result r, string file)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"# Разбор партии {file}");
            sb.AppendLine();
            sb.AppendLine($"- Человек: нация {r.Human}");
            sb.AppendLine(string.Format(inv, "- Исход: {0}, минута {1:0.0}", r.Outcome, r.Minute));
            sb.AppendLine("- Принятые команды: " + string.Join(", ", new[] { CommandType.RecruitDivision, CommandType.AssignDivision, CommandType.SetFrontOrder }
                .Select(t => $"{Name(t)} {(r.Accepted.TryGetValue(t, out var k) ? k : 0)}")));
            sb.AppendLine($"- Решений за первые 10 минут: {r.DecisionsFirst10Min} (цель ≥ 10: {(r.DecisionsFirst10Min >= 10 ? "ок" : "провал")})");
            sb.AppendLine($"- Фокус-эпизоды (≥ 30 с): {r.FocusEpisodes.Count}" + (r.FocusEpisodes.Count > 0 ? " — " + string.Join(", ", r.FocusEpisodes) : ""));
            sb.AppendLine($"- Набор под рельеф: танковых при равнине ≥ 60% {r.ArmorOnPlains}, пехотных при равнине ≤ 40% {r.InfantryOffPlains}");
            sb.AppendLine($"- Метрика ворот 1 (≥ 1 фокус-эпизод или набор под рельеф): {(r.Gate ? "ок" : "провал")}");
            sb.AppendLine();
            sb.AppendLine("## Решения");
            sb.AppendLine();
            foreach (var line in r.Timeline) sb.AppendLine("- " + line);
            return sb.ToString();
        }
    }
}
