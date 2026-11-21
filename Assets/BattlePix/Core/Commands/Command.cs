namespace BattlePix.Core
{
    /// <summary>
    /// Единственный список способов изменить мир. Игрок, ИИ и отладка пользуются им одинаково.
    /// Значения только дописываются и никогда не переиспользуются.
    /// </summary>
    public enum CommandType : byte
    {
        Noop = 0,
        DebugSetOwner = 1,
        /// <summary>Устарело (прототип до соединений): в мире с правилами отклоняется с Obsolete.</summary>
        Recruit = 2,
        /// <summary>Устарело: крепости убраны (01b). Obsolete.</summary>
        BuildFort = 3,
        /// <summary>Устарело: удар убран (01b). Obsolete.</summary>
        Strike = 4,
        /// <summary>Устарело (соединения 01b): в мире с правилами Obsolete.</summary>
        RecruitUnit = 5,
        /// <summary>Устарело (соединения 01b): Obsolete.</summary>
        AssignFormation = 6,
        /// <summary>Устарело (соединения 01b): Obsolete.</summary>
        SetOrder = 7,
        /// <summary>Набрать дивизию (01c). A = тип дивизии, B не используется.</summary>
        RecruitDivision = 8,
        /// <summary>Назначить дивизию. A = номер дивизии k, B = id противника (0 — в тыл).</summary>
        AssignDivision = 9,
        /// <summary>Приказ своей стороне фронта. A = id противника, B = 0 оборона / 1 наступление.</summary>
        SetFrontOrder = 10,
        // Значения только дописываются.
    }

    public struct Command
    {
        /// <summary>Тик, в который команда применяется.</summary>
        public int Tick;
        /// <summary>Кто отдал. 0 — система или отладка.</summary>
        public byte Nation;
        /// <summary>Порядковый номер внутри тика. Присваивает очередь.</summary>
        public int Seq;
        public CommandType Type;
        /// <summary>DebugSetOwner: A = пиксель, B = владелец. Игровые команды — см. CommandType.</summary>
        public int A, B;

        public override string ToString() => $"[t={Tick} n={Nation} s={Seq} {Type} a={A} b={B}]";
    }

    public enum EnqueueResult : byte
    {
        Accepted = 0,
        TickInPast = 1,
        BeyondHorizon = 2,
        GameOver = 3,
    }

    public enum RejectReason : byte
    {
        None = 0,
        PixelOutOfMap = 1,
        OwnerOutOfRange = 2,
        UnknownType = 3,
        /// <summary>Nation = 0, вне диапазона или мертва.</summary>
        NationInvalid = 4,
        NotEnoughGold = 5,
        ArmyCap = 6,
        NotOwnPixel = 7,
        /// <summary>Больше не выдаётся (крепости убраны).</summary>
        TooCloseToFort = 8,
        /// <summary>Больше не выдаётся (крепости убраны).</summary>
        FortLimit = 9,
        NoBattalions = 10,
        TargetInvalid = 11,
        NoContact = 12,
        /// <summary>Игровая команда в мире без правил.</summary>
        RulesDisabled = 13,
        /// <summary>Отладочная команда (DebugSetOwner) в мире с правилами.</summary>
        DebugOnly = 14,
        /// <summary>Устаревшая команда (Recruit, BuildFort, Strike, RecruitUnit, AssignFormation, SetOrder) в мире с правилами.</summary>
        Obsolete = 15,
        /// <summary>Больше не выдаётся (соединения 01b).</summary>
        FormationInvalid = 16,
        /// <summary>Больше не выдаётся (соединения 01b).</summary>
        FormationFull = 17,
        /// <summary>Больше не выдаётся (соединения 01b).</summary>
        FormationLimit = 18,
        /// <summary>Больше не выдаётся (соединения 01b).</summary>
        UnitTypeInvalid = 19,
        /// <summary>Дивизия уже стоит на этом фронте (и не в пути).</summary>
        AlreadyThere = 20,
        OrderInvalid = 21,
        /// <summary>Нет такой живой дивизии у нации.</summary>
        DivisionInvalid = 22,
        /// <summary>У нации уже maxDivisionsPerNation дивизий.</summary>
        DivisionLimit = 23,
        DivisionTypeInvalid = 24,
        // Значения только дописываются.
    }

    public struct RejectedCommand
    {
        public Command Command;
        public RejectReason Reason;
    }
}
