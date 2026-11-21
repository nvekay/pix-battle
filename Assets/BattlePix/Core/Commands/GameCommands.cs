namespace BattlePix.Core
{
    /// <summary>
    /// Валидация и применение игровых команд (спека 01c §3.3, 02b §3.3). Одни и те же для человека и ИИ.
    /// Валидация детерминированная и идёт в момент применения: если на две покупки не хватает золота, вторая отклоняется.
    /// </summary>
    public static class GameCommands
    {
        public static bool IsGameCommand(CommandType t) =>
            t == CommandType.RecruitDivision || t == CommandType.AssignDivision || t == CommandType.SetFrontOrder
            || t == CommandType.DeclareWar || t == CommandType.ProposePeace || t == CommandType.AnswerPeace;

        public static bool IsObsolete(CommandType t) =>
            t == CommandType.Recruit || t == CommandType.BuildFort || t == CommandType.Strike
            || t == CommandType.RecruitUnit || t == CommandType.AssignFormation || t == CommandType.SetOrder;

        public static RejectReason Validate(World w, in Command c)
        {
            if (!w.HasRules) return RejectReason.RulesDisabled;
            if (IsObsolete(c.Type)) return RejectReason.Obsolete;
            if (c.Nation == 0 || c.Nation > w.NationCount || w.Nation(c.Nation).Alive == 0) return RejectReason.NationInvalid;
            switch (c.Type)
            {
                case CommandType.RecruitDivision:
                    return ValidateRecruit(w, c.Nation, c.A);
                case CommandType.AssignDivision:
                    return ValidateAssign(w, c.Nation, c.A, c.B);
                case CommandType.SetFrontOrder:
                    return ValidateFrontOrder(w, c.Nation, c.A, c.B);
                case CommandType.DeclareWar:
                    return WarSystem.ValidateDeclare(w, c.Nation, c.A, c.B);
                case CommandType.ProposePeace:
                    return WarSystem.ValidatePropose(w, c.Nation, c.A);
                case CommandType.AnswerPeace:
                    return WarSystem.ValidateAnswer(w, c.Nation, c.A, c.B);
                default:
                    return RejectReason.UnknownType;
            }
        }

        /// <summary>Живая дивизия k нации.</summary>
        public static bool OwnDivision(World w, byte nation, int k) =>
            k >= 0 && k < w.MaxDivisionsPerNation && w.DivisionAt(w.DivisionSlot(nation, k)).Alive != 0;

        /// <summary>RecruitDivision без проверки, жива ли нация (для UI и ИИ).</summary>
        public static RejectReason ValidateRecruit(World w, byte nation, int type)
        {
            var rules = w.Rules;
            if (type < 0 || type >= rules.Divisions.Length) return RejectReason.DivisionTypeInvalid;
            var n = w.Nation(nation);
            if (n.Divisions >= w.MaxDivisionsPerNation) return RejectReason.DivisionLimit;
            if (n.Gold < rules.DivisionCost(type)) return RejectReason.NotEnoughGold;
            return RejectReason.None;
        }

        /// <summary>AssignDivision без проверки, жива ли нация.</summary>
        public static RejectReason ValidateAssign(World w, byte nation, int k, int target)
        {
            if (!OwnDivision(w, nation, k)) return RejectReason.DivisionInvalid;
            if (target == 0) return RejectReason.None;
            if (target < 1 || target > w.NationCount || target == nation || w.Nation((byte)target).Alive == 0)
                return RejectReason.TargetInvalid;
            if (w.FrontLength(nation, target) <= 0) return RejectReason.NoContact;
            var v = w.DivisionAt(w.DivisionSlot(nation, k));
            // Уже стоит на этом фронте на месте. В пути (и готовящуюся, назначенную заранее) — можно: таймер перезапускается.
            if (v.Front == target && w.OnFront(v)) return RejectReason.AlreadyThere;
            return RejectReason.None;
        }

        /// <summary>SetFrontOrder без проверки, жива ли нация. Общий фронт не обязателен.</summary>
        public static RejectReason ValidateFrontOrder(World w, byte nation, int enemy, int order)
        {
            if (enemy < 1 || enemy > w.NationCount || enemy == nation || w.Nation((byte)enemy).Alive == 0) return RejectReason.TargetInvalid;
            if (order != Orders.Defend && order != Orders.Attack) return RejectReason.OrderInvalid;
            return RejectReason.None;
        }

        internal static void Apply(World w, in Command c)
        {
            var rules = w.Rules;
            switch (c.Type)
            {
                case CommandType.RecruitDivision:
                {
                    byte type = (byte)c.A;
                    var nations = w.NationsRW;
                    var n = nations[c.Nation - 1];
                    n.Gold -= rules.DivisionCost(type);
                    if (type == DivisionTypes.Armor) n.RecruitedArmorDiv += 1;
                    else n.RecruitedInfDiv += 1;
                    nations[c.Nation - 1] = n;
                    DivisionSystem.Create(w, c.Nation, DivisionSystem.FreeSlot(w, c.Nation), type, w.Tick + rules.Divisions[type].TrainTicks);
                    break;
                }
                case CommandType.AssignDivision:
                {
                    int slot = w.DivisionSlot(c.Nation, c.A);
                    var divisions = w.DivisionsRW;
                    var v = divisions[slot];
                    v.Front = (byte)c.B;
                    if (c.B == 0) v.ArriveTick = 0;
                    else v.ArriveTick = (v.ReadyTick > w.Tick ? v.ReadyTick : w.Tick) + rules.Army.TransferTicks;
                    divisions[slot] = v;
                    break;
                }
                case CommandType.SetFrontOrder:
                {
                    int n = w.NationCount;
                    w.FrontOrderRW[(c.Nation - 1) * n + (c.A - 1)] = (byte)c.B;
                    break;
                }
                case CommandType.DeclareWar:
                    WarSystem.ApplyDeclare(w, c.Nation, c.A, c.B);
                    break;
                case CommandType.ProposePeace:
                    WarSystem.ApplyPropose(w, c.Nation, c.A);
                    break;
                case CommandType.AnswerPeace:
                    WarSystem.ApplyAnswer(w, c.Nation, c.A, c.B);
                    break;
            }
        }
    }
}
