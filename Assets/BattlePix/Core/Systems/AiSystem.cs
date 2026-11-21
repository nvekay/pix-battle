namespace BattlePix.Core
{
    /// <summary>
    /// ИИ (спека 01b §5.8). В PR 2 переделки — пусто: все нации стоят на старте в тылу, партия кончается по таймеру.
    /// Правила соединений придут в PR 3.
    /// </summary>
    public sealed class AiSystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
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
