namespace BattlePix.Core
{
    /// <summary>
    /// Экономика (спека 01 §5.6, дельта 01c §4.4), раз в секунду на последнем тике секунды: доход с земли и плоский доход,
    /// содержание дивизий по типам (включая готовящиеся), репарации (02b §4.4), дезертирство одной дивизии в секунду, если золото ушло в минус.
    /// У мёртвой нации ничего не считается. Организация, потери и пополнение идут после (DivisionSecondStep).
    /// </summary>
    public sealed class EconomySystem : ISimSystem
    {
        long[] _gold = new long[0];

        public void Tick(World world, CommandContext ctx)
        {
            int tps = ctx.Config.TicksPerSecond;
            if (world.Tick % tps != tps - 1) return;
            var e = world.Rules.Economy;
            var nations = world.NationsRW;
            int n = nations.Length;
            if (_gold.Length != n) _gold = new long[n];

            // 1. Доход и содержание.
            for (int i = 0; i < n; i++)
            {
                var s = nations[i];
                if (s.Alive == 0) continue;
                byte id = (byte)(i + 1);
                long income = e.FlatIncome_c + (long)s.Pixels * 100 / e.PixelsPerGold;
                _gold[i] = s.Gold + income - DivisionSystem.Upkeep(world, id);
            }

            // 2. Репарации (02b §4.4): по парам a < b, не уводят золото в минус.
            var wars = world.WarsRW;
            int tribute = world.Rules.War.Tribute_bp;
            for (int a = 1; a <= n; a++)
            for (int b = a + 1; b <= n; b++)
            {
                int idx = (a - 1) * n + (b - 1);
                var war = wars[idx];
                if (war.TributePayer == 0) continue;
                byte payer = war.TributePayer, payee = war.TributePayee;
                if (world.Tick >= war.TributeUntil || nations[payer - 1].Alive == 0 || nations[payee - 1].Alive == 0)
                {
                    war.TributePayer = 0;
                    war.TributePayee = 0;
                    war.TributeUntil = 0;
                    wars[idx] = war;
                    continue;
                }
                long pay = (long)Income(world, payer) * tribute / 10000;
                long have = _gold[payer - 1] > 0 ? _gold[payer - 1] : 0;
                if (pay > have) pay = have;
                _gold[payer - 1] -= pay;
                _gold[payee - 1] += pay;
                world.TributePaid += pay;
            }

            // 3. Дезертирство, если золото ушло в минус.
            for (int i = 0; i < n; i++)
            {
                if (nations[i].Alive == 0) continue;
                long gold = _gold[i];
                if (gold < 0)
                {
                    DivisionSystem.Desert(world, (byte)(i + 1));
                    gold = 0;
                }
                var s = nations[i]; // Desert может убить дивизию: меняет Divisions и DivisionsLost
                s.Gold = gold > int.MaxValue ? int.MaxValue : (int)gold;
                nations[i] = s;
            }
        }

        /// <summary>Доход нации в сотых за секунду: земля + плоский.</summary>
        public static int Income(World world, byte nation)
        {
            var e = world.Rules.Economy;
            return e.FlatIncome_c + world.Nation(nation).Pixels * 100 / e.PixelsPerGold;
        }

        /// <summary>Чистый доход нации в сотых за секунду (для UI и ИИ): доход минус содержание, без пополнения.</summary>
        public static int NetIncome(World world, byte nation) => (int)(Income(world, nation) - DivisionSystem.Upkeep(world, nation));
    }
}
