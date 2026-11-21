namespace BattlePix.Core
{
    /// <summary>
    /// Экономика (спека 01 §5.6, дельта 01b §5.4), раз в секунду на последнем тике секунды: доход с земли и плоский доход,
    /// содержание батальонов по типам и штабов, дезертирство по одному батальону в секунду, если золото ушло в минус.
    /// У мёртвой нации ничего не считается. Потери и пополнение идут после (FormationLossStep).
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
                byte id = (byte)(i + 1);
                long income = e.FlatIncome_c + (long)n.Pixels * 100 / e.PixelsPerGold;
                long upkeep = FormationSystem.Upkeep(world, id);
                long gold = n.Gold + income - upkeep;
                if (gold < 0)
                {
                    FormationSystem.Desert(world, id);
                    n = nations[i]; // Desert меняет Battalions и BattalionsLost
                    gold = 0;
                }
                n.Gold = gold > int.MaxValue ? int.MaxValue : (int)gold;
                nations[i] = n;
            }
        }

        /// <summary>Доход нации в сотых за секунду: земля + плоский.</summary>
        public static int Income(World world, byte nation)
        {
            var e = world.Rules.Economy;
            return e.FlatIncome_c + world.Nation(nation).Pixels * 100 / e.PixelsPerGold;
        }

        /// <summary>Чистый доход нации в сотых за секунду (для UI и ИИ): доход минус содержание, без пополнения.</summary>
        public static int NetIncome(World world, byte nation) => (int)(Income(world, nation) - FormationSystem.Upkeep(world, nation));
    }
}
