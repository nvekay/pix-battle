namespace BattlePix.Core
{
    /// <summary>
    /// Экономика (спека 5.6), раз в секунду на последнем тике секунды: доход с земли и плоский доход, содержание армии,
    /// дезертирство по одному батальону в секунду, если золото ушло в минус. У мёртвой нации ничего не считается.
    /// </summary>
    public sealed class EconomySystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
            int tps = ctx.Config.TicksPerSecond;
            if (world.Tick % tps != tps - 1) return;
            var e = world.Rules.Economy;
            var nations = world.NationsRW;
            for (int i = 0; i < nations.Length; i++)
            {
                var n = nations[i];
                if (n.Alive == 0) continue;
                long income = e.FlatIncome_c + (long)n.Pixels * 100 / e.PixelsPerGold;
                long upkeep = (long)n.Battalions * e.Upkeep_c_per_battalion;
                long gold = n.Gold + income - upkeep;
                if (gold < 0)
                {
                    if (n.Battalions > 0) n.Battalions -= 1;
                    gold = 0;
                }
                n.Gold = gold > int.MaxValue ? int.MaxValue : (int)gold;
                nations[i] = n;
            }
        }

        /// <summary>Чистый доход нации в сотых за секунду (для UI и ИИ): доход минус содержание.</summary>
        public static int NetIncome(World world, byte nation)
        {
            var e = world.Rules.Economy;
            var n = world.Nation(nation);
            return e.FlatIncome_c + n.Pixels * 100 / e.PixelsPerGold - n.Battalions * e.Upkeep_c_per_battalion;
        }
    }

    /// <summary>Удары (спека 5.2, п. 4): обнулить истёкшие StrikeUntil и удары от мёртвых и по мёртвым.</summary>
    public sealed class StrikeSystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
            int n = world.NationCount, next = world.Tick + 1;
            var strike = world.StrikeUntilRW;
            for (int a = 0; a < n; a++)
            {
                bool aliveA = world.NationsRW[a].Alive != 0;
                for (int d = 0; d < n; d++)
                {
                    int i = a * n + d;
                    if (strike[i] == 0) continue;
                    if (strike[i] <= next || !aliveA || world.NationsRW[d].Alive == 0) strike[i] = 0;
                }
            }
        }
    }
}
