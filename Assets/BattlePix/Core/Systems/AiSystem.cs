using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// ИИ-заглушка (спека 5.8). ИИ — не отдельная логика, а профиль весов из ai.json, который выбирает из тех же команд.
    /// Каждая ИИ-нация думает раз в thinkIntervalTicks со сдвигом (id − 1) % interval и отдаёт не больше одной команды
    /// на Tick + 1 через ту же очередь. Читает только World и свои веса.
    /// </summary>
    public sealed class AiSystem : ISimSystem, IDisposable
    {
        const int FortTries = 32;

        NativeList<int> _candidates;

        public void Tick(World world, CommandContext ctx)
        {
            int n = world.NationCount;
            for (int id = 1; id <= n; id++)
            {
                if (id == ctx.HumanNation || world.Nation((byte)id).Alive == 0) continue;
                var p = world.Rules.Profile((byte)id);
                if (world.Tick % p.ThinkIntervalTicks != (id - 1) % p.ThinkIntervalTicks) continue;
                if (Think(world, (byte)id, p, out var cmd)) ctx.IssueInternal(world, cmd);
            }
        }

        bool Think(World w, byte self, AiProfile p, out Command cmd)
        {
            cmd = default;
            var rules = w.Rules;
            var me = w.Nation(self);
            ref var rng = ref w.Rng.Get(RngStream.Ai);
            int n = w.NationCount;

            // 1–2. Крепость под давлением, или копим на неё.
            int worst = 0, worstNet = int.MaxValue;
            for (int d = 1; d <= n; d++)
            {
                if (d == self || w.Nation((byte)d).Alive == 0 || w.FrontLength(self, d) <= 0) continue;
                int net = w.NetFlips10s(self, d);
                if (net < worstNet)
                {
                    worstNet = net;
                    worst = d;
                }
            }
            if (worst != 0 && worstNet <= -p.FortLossThresholdPx && me.FortsAlive < w.MaxFortsPerNation)
            {
                int count = CollectFrontPixels(w, self, (byte)worst);
                if (count > 0 && !AnyCovered(w, self, count))
                {
                    if (me.Gold < rules.FortCost(me.FortsAlive)) return false; // копим на крепость
                    for (int i = 0; i < FortTries; i++)
                    {
                        int pick = _candidates[rng.NextInt(count)];
                        int px = Retreat(w, self, pick, me.Capital, p.FortDepthPx);
                        if (GameCommands.ValidateFort(w, self, px) != RejectReason.None) continue;
                        cmd = new Command { Nation = self, Type = CommandType.BuildFort, A = px };
                        return true;
                    }
                }
            }

            // 3. Набор: плотность ниже цели или армии меньше минимума.
            var e = rules.Economy;
            long warLen = w.WarLen(self);
            bool wantArmy = (long)me.Battalions * 1000 < (long)p.TargetDensity_milli * warLen / 100 || me.Battalions < p.MinBattalions;
            if (wantArmy && me.Battalions < e.BattalionCap && (long)me.Gold >= (long)e.BattalionCost_c + p.RecruitReserve_c)
            {
                cmd = new Command { Nation = self, Type = CommandType.Recruit };
                return true;
            }

            // 4. Удар по самому выгодному соседу; лидер получает бонус (догпайл — тормоз snowball).
            // Только из излишка над целью плотности и не чаще раза в strikeCooldownTicks.
            long surplusNeed = (long)p.TargetDensity_milli * warLen / 100 + (long)p.StrikeSurplusBattalions * 1000;
            bool cooled = me.LastStrikeTick < 0 || w.Tick - me.LastStrikeTick >= p.StrikeCooldownTicks;
            if (me.Battalions >= 1 && (long)me.Battalions * 1000 >= surplusNeed && cooled && !HasActiveStrike(w, self))
            {
                int best = 0;
                long bestScore = long.MinValue;
                for (int d = 1; d <= n; d++)
                {
                    if (d == self || w.Nation((byte)d).Alive == 0 || w.FrontLength(self, d) <= 0) continue;
                    long score = (long)w.Pow(self, d) * 10000 / w.Pow(d, self);
                    if ((long)w.Nation((byte)d).Pixels * 10000 >= (long)p.LeaderShare_bp * w.TotalLand) score += p.LeaderBonus_bp;
                    score += rng.NextInt(p.Noise_bp + 1);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = d;
                    }
                }
                if (best != 0 && bestScore >= p.StrikeMinScore_bp)
                {
                    cmd = new Command { Nation = self, Type = CommandType.Strike, A = best };
                    return true;
                }
            }
            return false;
        }

        static bool HasActiveStrike(World w, byte self)
        {
            int n = w.NationCount;
            for (int d = 0; d < n; d++)
                if (w.StrikeUntil[(self - 1) * n + d] > w.Tick) return true;
            return false;
        }

        /// <summary>Свои пограничные пиксели, соседние с d (по Frontier, без прохода по карте).</summary>
        int CollectFrontPixels(World w, byte self, byte d)
        {
            if (!_candidates.IsCreated) _candidates = new NativeList<int>(1024, Allocator.Persistent);
            _candidates.Clear();
            new FrontPixelsJob
            {
                Frontier = w.FrontierRW.AsArray(),
                Owner = w.OwnerRW,
                Terrain = w.TerrainRW,
                EdgeMask = w.EdgeMaskRW,
                W = w.Width,
                Self = self,
                Enemy = d,
                Result = _candidates,
            }.Run();
            return _candidates.Length;
        }

        /// <summary>Есть ли на фронте с d своя крепость: хоть один найденный пиксель накрыт своим битом.</summary>
        bool AnyCovered(World w, byte self, int count)
        {
            int bit = 1 << (self - 1);
            var cover = w.FortCover;
            for (int i = 0; i < count; i++)
                if ((cover[_candidates[i]] & bit) != 0) return true;
            return false;
        }

        /// <summary>
        /// Сдвинуть пиксель на depth шагов по прямой к столице (Брезенхем, целые),
        /// остановиться на последнем своём пикселе.
        /// </summary>
        internal static int Retreat(World w, byte self, int from, int capital, int depth)
        {
            if (capital < 0) return from;
            int width = w.Width;
            int x = from % width, y = from / width, tx = capital % width, ty = capital / width;
            int dx = Math.Abs(tx - x), dy = -Math.Abs(ty - y);
            int sx = x < tx ? 1 : -1, sy = y < ty ? 1 : -1;
            int err = dx + dy, last = from;
            for (int step = 0; step < depth && (x != tx || y != ty); step++)
            {
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x += sx; }
                if (e2 <= dx) { err += dx; y += sy; }
                int i = y * width + x;
                if (w.Owner[i] != self) break;
                last = i;
            }
            return last;
        }

        public void Dispose()
        {
            if (_candidates.IsCreated) _candidates.Dispose();
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct FrontPixelsJob : IJob
    {
        [ReadOnly] public NativeArray<int> Frontier;
        [ReadOnly] public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        public int W;
        public byte Self, Enemy;
        public NativeList<int> Result;

        public void Execute()
        {
            for (int i = 0; i < Frontier.Length; i++)
            {
                int p = Frontier[i];
                if (Owner[p] != Self) continue;
                byte edge = EdgeMask[p];
                for (int k = 0; k < 4; k++)
                {
                    if (!Grid.Neighbour(p, edge, k, W, out int q)) continue;
                    if (Terrain[q] != (byte)Core.Terrain.Water && Owner[q] == Enemy)
                    {
                        Result.Add(p);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Конец партии (спека 5.7): проверки по порядку, первая сработавшая заканчивает партию.</summary>
    public sealed class EndSystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
            var end = world.Rules.End;
            int n = world.NationCount;
            byte human = ctx.HumanNation;

            if (human != 0 && world.Nation(human).Alive == 0)
            {
                Finish(world, EndReason.HumanDefeated, 0);
                return;
            }

            byte leader = 0, largest = 0;
            int leaderPx = -1, largestPx = -1, alive = 0;
            byte lastAlive = 0;
            for (int id = 1; id <= n; id++)
            {
                var s = world.Nation((byte)id);
                if (s.Alive == 0) continue;
                alive++;
                lastAlive = (byte)id;
                if (s.Pixels > largestPx)
                {
                    largestPx = s.Pixels;
                    largest = (byte)id;
                }
                if ((long)s.Pixels * 10000 >= (long)end.VictoryShare_bp * world.TotalLand && s.Pixels > leaderPx)
                {
                    leaderPx = s.Pixels;
                    leader = (byte)id;
                }
            }
            if (leader != 0)
            {
                Finish(world, EndReason.Conquest, leader);
                return;
            }
            if (alive == 1)
            {
                Finish(world, EndReason.Conquest, lastAlive);
                return;
            }
            if (world.Tick + 1 >= end.TimeoutTicks)
                Finish(world, EndReason.Timeout, largest);
        }

        static void Finish(World w, EndReason reason, byte winner)
        {
            w.IsOver = true;
            w.EndReason = reason;
            w.Winner = winner;
        }
    }
}
