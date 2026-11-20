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
    }

    public struct RejectedCommand
    {
        public Command Command;
        public RejectReason Reason;
    }
}
