namespace BattlePix.Core
{
    /// <summary>
    /// Валидация и применение игровых команд (спека 01b §4.4). Одни и те же для человека и ИИ.
    /// Валидация детерминированная и идёт в момент применения: если на две покупки не хватает золота, вторая отклоняется.
    /// </summary>
    public static class GameCommands
    {
        public static bool IsGameCommand(CommandType t) =>
            t == CommandType.RecruitUnit || t == CommandType.AssignFormation || t == CommandType.SetOrder;

        public static bool IsObsolete(CommandType t) =>
            t == CommandType.Recruit || t == CommandType.BuildFort || t == CommandType.Strike;

        public static RejectReason Validate(World w, in Command c)
        {
            if (!w.HasRules) return RejectReason.RulesDisabled;
            if (IsObsolete(c.Type)) return RejectReason.Obsolete;
            if (c.Nation == 0 || c.Nation > w.NationCount || w.Nation(c.Nation).Alive == 0) return RejectReason.NationInvalid;
            switch (c.Type)
            {
                case CommandType.RecruitUnit:
                    return ValidateRecruit(w, c.Nation, c.A, c.B);
                case CommandType.AssignFormation:
                    return ValidateAssign(w, c.Nation, c.A, c.B);
                case CommandType.SetOrder:
                    if (!OwnFormation(w, c.Nation, c.A)) return RejectReason.FormationInvalid;
                    if (c.B != Orders.Defend && c.B != Orders.Attack) return RejectReason.OrderInvalid;
                    return RejectReason.None;
                default:
                    return RejectReason.UnknownType;
            }
        }

        /// <summary>Живое соединение k нации.</summary>
        public static bool OwnFormation(World w, byte nation, int k) =>
            k >= 0 && k < w.MaxFormationsPerNation && w.FormationAt(w.FormationSlot(nation, k)).Alive != 0;

        /// <summary>Цена набора: батальон типа, плюс штаб для нового соединения (k = −1).</summary>
        public static int RecruitCost(World w, int k, int type)
        {
            int cost = w.Rules.Units[type].Cost_c;
            return k == -1 ? cost + w.Rules.Formations.HqCost_c : cost;
        }

        /// <summary>RecruitUnit без проверки, жива ли нация (для UI и ИИ).</summary>
        public static RejectReason ValidateRecruit(World w, byte nation, int k, int type)
        {
            var rules = w.Rules;
            var n = w.Nation(nation);
            if (type < 0 || type >= rules.Units.Length) return RejectReason.UnitTypeInvalid;
            if (k == -1)
            {
                if (n.Formations >= w.MaxFormationsPerNation) return RejectReason.FormationLimit;
            }
            else
            {
                if (!OwnFormation(w, nation, k)) return RejectReason.FormationInvalid;
                if (FormationSystem.BattalionCount(w, w.FormationSlot(nation, k)) >= w.MaxBattalions) return RejectReason.FormationFull;
            }
            if (n.Battalions >= rules.Economy.BattalionCap) return RejectReason.ArmyCap;
            if (n.Gold < RecruitCost(w, k, type)) return RejectReason.NotEnoughGold;
            return RejectReason.None;
        }

        /// <summary>AssignFormation без проверки, жива ли нация.</summary>
        public static RejectReason ValidateAssign(World w, byte nation, int k, int target)
        {
            if (!OwnFormation(w, nation, k)) return RejectReason.FormationInvalid;
            if (target == 0) return RejectReason.None;
            if (target < 1 || target > w.NationCount || target == nation || w.Nation((byte)target).Alive == 0)
                return RejectReason.TargetInvalid;
            if (w.FrontLength(nation, target) <= 0) return RejectReason.NoContact;
            var f = w.FormationAt(w.FormationSlot(nation, k));
            // Уже стоит на этом фронте на месте. В пути — можно: повторное назначение перезапускает таймер.
            if (f.Front == target && f.ArriveTick <= w.Tick) return RejectReason.AlreadyThere;
            return RejectReason.None;
        }

        internal static void Apply(World w, in Command c)
        {
            var rules = w.Rules;
            switch (c.Type)
            {
                case CommandType.RecruitUnit:
                {
                    int k = c.A;
                    byte type = (byte)c.B;
                    var nations = w.NationsRW;
                    var n = nations[c.Nation - 1];
                    n.Gold -= RecruitCost(w, k, type);
                    if (type == UnitTypes.Armor) n.RecruitedArmor += 1;
                    else n.RecruitedInf += 1;
                    nations[c.Nation - 1] = n;
                    if (k == -1)
                    {
                        k = FormationSystem.FreeFormation(w, c.Nation);
                        FormationSystem.CreateFormation(w, c.Nation, k);
                    }
                    FormationSystem.AddBattalion(w, w.FormationSlot(c.Nation, k), type);
                    break;
                }
                case CommandType.AssignFormation:
                {
                    int slot = w.FormationSlot(c.Nation, c.A);
                    var formations = w.FormationsRW;
                    var f = formations[slot];
                    f.Front = (byte)c.B;
                    f.ArriveTick = c.B == 0 ? 0 : w.Tick + rules.Formations.TransferTicks;
                    formations[slot] = f;
                    break;
                }
                case CommandType.SetOrder:
                {
                    int slot = w.FormationSlot(c.Nation, c.A);
                    var formations = w.FormationsRW;
                    var f = formations[slot];
                    f.Order = (byte)c.B;
                    formations[slot] = f;
                    break;
                }
            }
        }
    }
}
