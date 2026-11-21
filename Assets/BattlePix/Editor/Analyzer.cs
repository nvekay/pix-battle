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
    /// решения за первые 10 минут (цель ≥ 10), фокус-эпизоды — отрезки ≥ 30 с, когда на одном фронте приказ «наступление»
    /// и есть дивизии на месте, а другой существующий фронт в обороне или пустой (цель ≥ 1), наборы под рельеф — танковая
    /// при доле равнины фронта наступления ≥ 60%, пехотная при ≤ 40% (цель ≥ 1), и ротации человека (01c §7).
    /// Метрика ворот 1 выполнена, если ≥ 1 по фокусу или набору под рельеф.
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
            public int Rotations;
            public bool Gate => FocusEpisodes.Count >= 1 || ArmorOnPlains + InfantryOffPlains >= 1;
            public List<string> Timeline = new List<string>();
            // 02b §7: мир как переброска, выбор цели, «давил дальше после выполнения цели».
            public List<string> PeaceThenWar = new List<string>();
            public int ClaimsDeclared, ReparationsDeclared;
            public List<string> PressedOn = new List<string>();
            public List<string> Wars = new List<string>();
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
            log($"analysis -> {Path.Combine(dir, "analysis.md")}: decisions in first 10 min {r.DecisionsFirst10Min} (goal >= 10), focus episodes {r.FocusEpisodes.Count}, terrain recruits {r.ArmorOnPlains + r.InfantryOffPlains}, rotations {r.Rotations}");
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
                int next = 0, focusStart = -1, seenEvents = 0, lastPeaceTick = -1, lastPeaceWith = 0;
                var fulfilledSince = new int[w.NationCount + 1];
                for (int i = 0; i < fulfilledSince.Length; i++) fulfilledSince[i] = -1;
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
                    if (w.Nation(h).Alive != 0) res.Rotations += ArmyStats.RotationsThisTick(w, h);
                    for (; seenEvents < w.WarEvents.Count; seenEvents++)
                    {
                        var e = w.WarEvents[seenEvents];
                        if (e.A != h && e.D != h) continue;
                        int other = e.A == h ? e.D : e.A;
                        double min = e.Tick / (60.0 * tps);
                        if (e.Kind == WarEventKind.Declared)
                        {
                            if (e.A == h)
                            {
                                if (e.Goal == WarGoals.Claims) res.ClaimsDeclared++; else res.ReparationsDeclared++;
                                if (lastPeaceTick >= 0 && e.Tick - lastPeaceTick <= 60 * tps && other != lastPeaceWith)
                                    res.PeaceThenWar.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} мин: мир с {1}, через {2:0} с война с {3}",
                                        lastPeaceTick / (60.0 * tps), lastPeaceWith, (e.Tick - lastPeaceTick) / (double)tps, other));
                            }
                            res.Wars.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} мин — {1} объявил войну {2}: {3}",
                                min, e.A, e.D, e.Goal == WarGoals.Claims ? "претензии" : "репарации"));
                        }
                        else if (e.Kind == WarEventKind.Peace || e.Kind == WarEventKind.EndedByDeath)
                        {
                            lastPeaceTick = e.Tick;
                            lastPeaceWith = other;
                            res.Wars.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0} мин — война {1}–{2} кончилась: {3}, закреплено {4} px",
                                min, e.A, e.D, e.Outcome, e.Pixels));
                        }
                    }
                    if (w.Tick % tps == 0)
                        for (int d = 1; d <= w.NationCount; d++)
                        {
                            if (d == h) continue;
                            bool done = w.GoalFulfilled(h, d, h);
                            if (done && fulfilledSince[d] < 0) fulfilledSince[d] = w.Tick;
                            if (!done && fulfilledSince[d] >= 0)
                            {
                                PressedEpisode(res, d, fulfilledSince[d], w.Tick, tps);
                                fulfilledSince[d] = -1;
                            }
                        }
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
                for (int d = 1; d < fulfilledSince.Length; d++)
                    if (fulfilledSince[d] >= 0) PressedEpisode(res, d, fulfilledSince[d], w.Tick, tps);
                res.Finished = w.IsOver;
                res.Minute = w.Tick / (60.0 * tps);
                res.Outcome = !w.IsOver ? "партия не доиграна"
                    : w.Winner == h ? $"победа ({w.EndReason})"
                    : $"поражение ({w.EndReason}, победитель {(w.Winner == 0 ? "—" : w.Winner.ToString())})";
            }
            return res;
        }

        /// <summary>Своя цель выполнена ≥ 30 с, а мир не заключён: «давил дальше».</summary>
        static void PressedEpisode(Result res, int enemy, int from, int to, int tps)
        {
            if (to - from < 30 * tps) return;
            res.PressedOn.Add(string.Format(CultureInfo.InvariantCulture, "против {0}: {1:0.0}–{2:0.0} мин", enemy, from / (60.0 * tps), to / (60.0 * tps)));
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

        static string Name(CommandType t) => t == CommandType.RecruitDivision ? "набор" : t == CommandType.AssignDivision ? "назначение"
            : t == CommandType.SetFrontOrder ? "приказ" : t == CommandType.DeclareWar ? "объявление" : t == CommandType.ProposePeace ? "мир" : "ответ";

        static string ToMarkdown(Result r, string file)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine($"# Разбор партии {file}");
            sb.AppendLine();
            sb.AppendLine($"- Человек: нация {r.Human}");
            sb.AppendLine(string.Format(inv, "- Исход: {0}, минута {1:0.0}", r.Outcome, r.Minute));
            sb.AppendLine("- Принятые команды: " + string.Join(", ", new[] { CommandType.RecruitDivision, CommandType.AssignDivision, CommandType.SetFrontOrder,
                    CommandType.DeclareWar, CommandType.ProposePeace, CommandType.AnswerPeace }
                .Select(t => $"{Name(t)} {(r.Accepted.TryGetValue(t, out var k) ? k : 0)}")));
            sb.AppendLine($"- Решений за первые 10 минут: {r.DecisionsFirst10Min} (цель ≥ 10: {(r.DecisionsFirst10Min >= 10 ? "ок" : "провал")})");
            sb.AppendLine($"- Фокус-эпизоды (≥ 30 с): {r.FocusEpisodes.Count}" + (r.FocusEpisodes.Count > 0 ? " — " + string.Join(", ", r.FocusEpisodes) : ""));
            sb.AppendLine($"- Набор под рельеф: танковых при равнине ≥ 60% {r.ArmorOnPlains}, пехотных при равнине ≤ 40% {r.InfantryOffPlains}");
            sb.AppendLine($"- Ротации (свежая дивизия пришла туда, где стоит выдохшаяся): {r.Rotations}");
            sb.AppendLine($"- Метрика ворот 1 (≥ 1 фокус-эпизод или набор под рельеф): {(r.Gate ? "ок" : "провал")}");
            sb.AppendLine($"- Выбор цели (02b): претензии {r.ClaimsDeclared}, репарации {r.ReparationsDeclared}");
            sb.AppendLine($"- Мир как переброска (мир с одним, в течение 60 с война с другим): {r.PeaceThenWar.Count}" + (r.PeaceThenWar.Count > 0 ? " — " + string.Join("; ", r.PeaceThenWar) : ""));
            sb.AppendLine($"- Давил дальше после выполнения цели (≥ 30 с без мира): {r.PressedOn.Count}" + (r.PressedOn.Count > 0 ? " — " + string.Join("; ", r.PressedOn) : ""));
            sb.AppendLine();
            if (r.Wars.Count > 0)
            {
                sb.AppendLine("## Войны");
                sb.AppendLine();
                foreach (var line in r.Wars) sb.AppendLine("- " + line);
                sb.AppendLine();
            }
            sb.AppendLine("## Решения");
            sb.AppendLine();
            foreach (var line in r.Timeline) sb.AppendLine("- " + line);
            return sb.ToString();
        }
    }
}
