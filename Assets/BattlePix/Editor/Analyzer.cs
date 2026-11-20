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
    /// решения за первые 10 минут (цель ≥ 5) и fortOverArmy — сколько раз человек поставил крепость,
    /// когда на батальон хватало уже ≥ 10 с (цель ≥ 1).
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
            public int FortOverArmy;
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
            log($"analysis -> {Path.Combine(dir, "analysis.md")}: decisions in first 10 min {r.DecisionsFirst10Min} (goal >= 5), fortOverArmy {r.FortOverArmy} (goal >= 1)");
            return 0;
        }

        public static Result Analyze(ReplayFile replay, SimConfig config, Rules rules)
        {
            var res = new Result { Human = replay.Human };
            if (replay.Human == 0) throw new ArgumentException("replay has no human nation (human = 0)");
            var commands = replay.Commands.Select(c => c.ToCommand()).OrderBy(c => c.Tick).ThenBy(c => c.Seq).ToList();
            int tps = config.TicksPerSecond;
            int cost = rules.Economy.BattalionCost_c, cap = rules.Economy.BattalionCap;
            byte h = replay.Human;
            using (var sim = new Simulation(config, rules, replay.Seed, replay.TickLimit, replay.Human))
            {
                var w = sim.World;
                int next = 0, affordSince = -1;
                while (w.Tick < replay.Ticks && !w.IsOver)
                {
                    // На начало тика: хватает ли на батальон и с какого тика.
                    var me = w.Nation(h);
                    bool canRecruit = me.Gold >= cost && me.Battalions < cap;
                    if (!canRecruit) affordSince = -1;
                    else if (affordSince < 0) affordSince = w.Tick;
                    int affordTicks = affordSince < 0 ? -1 : w.Tick - affordSince;

                    while (next < commands.Count && commands[next].Tick == w.Tick) sim.Enqueue(commands[next++]);
                    int tick = w.Tick;
                    sim.Step();
                    foreach (var c in sim.AppliedLastTick)
                    {
                        if (c.Nation != h || !GameCommands.IsGameCommand(c.Type)) continue;
                        res.Accepted.TryGetValue(c.Type, out int k);
                        res.Accepted[c.Type] = k + 1;
                        if (tick < 600 * tps) res.DecisionsFirst10Min++;
                        bool overArmy = c.Type == CommandType.BuildFort && affordTicks >= 10 * tps;
                        if (overArmy) res.FortOverArmy++;
                        res.Timeline.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} мин — {1}{2}{3}",
                            tick / (60.0 * tps), Name(c.Type), c.Type == CommandType.Strike ? " по " + c.A : "",
                            overArmy ? " (крепость вместо армии)" : ""));
                    }
                }
                res.Finished = w.IsOver;
                res.Minute = w.Tick / (60.0 * tps);
                res.Outcome = !w.IsOver ? "партия не доиграна"
                    : w.Winner == h ? $"победа ({w.EndReason})"
                    : $"поражение ({w.EndReason}, победитель {(w.Winner == 0 ? "—" : w.Winner.ToString())})";
            }
            return res;
        }

        static string Name(CommandType t) => t == CommandType.Recruit ? "батальон" : t == CommandType.BuildFort ? "крепость" : "удар";

        static string ToMarkdown(Result r, string file)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"# Разбор партии {file}");
            sb.AppendLine();
            sb.AppendLine($"- Человек: нация {r.Human}");
            sb.AppendLine(string.Format(inv, "- Исход: {0}, минута {1:0.0}", r.Outcome, r.Minute));
            sb.AppendLine("- Принятые команды: " + string.Join(", ", new[] { CommandType.Recruit, CommandType.BuildFort, CommandType.Strike }
                .Select(t => $"{Name(t)} {(r.Accepted.TryGetValue(t, out var k) ? k : 0)}")));
            sb.AppendLine($"- Решений за первые 10 минут: {r.DecisionsFirst10Min} (цель ≥ 5: {(r.DecisionsFirst10Min >= 5 ? "ок" : "провал")})");
            sb.AppendLine($"- fortOverArmy: {r.FortOverArmy} (цель ≥ 1: {(r.FortOverArmy >= 1 ? "ок" : "провал")})");
            sb.AppendLine();
            sb.AppendLine("## Решения");
            sb.AppendLine();
            foreach (var line in r.Timeline) sb.AppendLine("- " + line);
            return sb.ToString();
        }
    }
}
