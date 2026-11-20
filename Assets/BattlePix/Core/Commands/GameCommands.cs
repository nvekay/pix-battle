namespace BattlePix.Core
{
    /// <summary>
    /// Валидация и применение игровых команд (спека 4.4). Одни и те же для человека и ИИ.
    /// Валидация детерминированная и идёт в момент применения: если на две покупки не хватает золота, вторая отклоняется.
    /// </summary>
    public static class GameCommands
    {
        public static bool IsGameCommand(CommandType t) =>
            t == CommandType.Recruit || t == CommandType.BuildFort || t == CommandType.Strike;

        public static RejectReason Validate(World w, in Command c)
        {
            if (!w.HasRules) return RejectReason.RulesDisabled;
            if (c.Nation == 0 || c.Nation > w.NationCount || w.Nation(c.Nation).Alive == 0) return RejectReason.NationInvalid;
            var rules = w.Rules;
            var n = w.Nation(c.Nation);
            switch (c.Type)
            {
                case CommandType.Recruit:
                    if (n.Gold < rules.Economy.BattalionCost_c) return RejectReason.NotEnoughGold;
                    if (n.Battalions >= rules.Economy.BattalionCap) return RejectReason.ArmyCap;
                    return RejectReason.None;
                case CommandType.BuildFort:
                    return ValidateFort(w, c.Nation, c.A);
                case CommandType.Strike:
                    if (c.A < 1 || c.A > w.NationCount || c.A == c.Nation || w.Nation((byte)c.A).Alive == 0)
                        return RejectReason.TargetInvalid;
                    if (w.FrontLength(c.Nation, c.A) <= 0) return RejectReason.NoContact;
                    if (n.Battalions < 1) return RejectReason.NoBattalions;
                    return RejectReason.None;
                default:
                    return RejectReason.UnknownType;
            }
        }

        /// <summary>Можно ли нации поставить крепость на пиксель (без проверки, жива ли нация).</summary>
        public static RejectReason ValidateFort(World w, byte nation, int pixel)
        {
            var rules = w.Rules;
            var n = w.Nation(nation);
            if (pixel < 0 || pixel >= w.PixelCount) return RejectReason.PixelOutOfMap;
            if (w.Owner[pixel] != nation) return RejectReason.NotOwnPixel;
            if (n.FortsAlive >= w.MaxFortsPerNation) return RejectReason.FortLimit;
            if (TooClose(w, nation, pixel)) return RejectReason.TooCloseToFort;
            if (n.Gold < rules.FortCost(n.FortsAlive)) return RejectReason.NotEnoughGold;
            return RejectReason.None;
        }

        /// <summary>Есть ли своя живая крепость ближе fortMinSpacing (евклидово, сравнение квадратов).</summary>
        public static bool TooClose(World w, byte nation, int pixel)
        {
            int width = w.Width, max = w.MaxFortsPerNation;
            long s = w.Rules.Forts.FortMinSpacing;
            long px = pixel % width, py = pixel / width;
            for (int k = 0; k < max; k++)
            {
                var f = w.Forts[(nation - 1) * max + k];
                if (f.Alive == 0) continue;
                long dx = f.Pixel % width - px, dy = f.Pixel / width - py;
                if (dx * dx + dy * dy < s * s) return true;
            }
            return false;
        }

        internal static void Apply(World w, in Command c)
        {
            var rules = w.Rules;
            var nations = w.NationsRW;
            var n = nations[c.Nation - 1];
            switch (c.Type)
            {
                case CommandType.Recruit:
                    n.Gold -= rules.Economy.BattalionCost_c;
                    n.Battalions += 1;
                    n.Recruits += 1;
                    break;
                case CommandType.BuildFort:
                {
                    n.Gold -= rules.FortCost(n.FortsAlive);
                    int max = w.MaxFortsPerNation;
                    var forts = w.FortsRW;
                    for (int k = 0; k < max; k++)
                    {
                        int slot = (c.Nation - 1) * max + k;
                        if (forts[slot].Alive != 0) continue;
                        forts[slot] = new Fort { Pixel = c.A, Owner = c.Nation, Alive = 1 };
                        break;
                    }
                    n.FortsAlive += 1;
                    n.FortsBuilt += 1;
                    Fortifications.Stamp(w, c.A, c.Nation, true);
                    break;
                }
                case CommandType.Strike:
                {
                    n.Battalions -= 1;
                    int idx = (c.Nation - 1) * w.NationCount + (c.A - 1);
                    var strike = w.StrikeUntilRW;
                    int from = strike[idx] > w.Tick ? strike[idx] : w.Tick;
                    strike[idx] = from + rules.Strikes.StrikeDurationTicks;
                    n.Strikes += 1;
                    break;
                }
            }
            nations[c.Nation - 1] = n;
        }
    }
}
