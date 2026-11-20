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
        /// <summary>Для DebugSetOwner: A = индекс пикселя, B = новый владелец.</summary>
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
        TooCloseToFort = 8,
        FortLimit = 9,
        NoBattalions = 10,
        TargetInvalid = 11,
        NoContact = 12,
        /// <summary>Игровая команда в мире без правил.</summary>
        RulesDisabled = 13,
        /// <summary>Отладочная команда (DebugSetOwner) в мире с правилами.</summary>
        DebugOnly = 14,
        // Значения только дописываются.
    }

    public struct RejectedCommand
    {
        public Command Command;
        public RejectReason Reason;
    }
}
