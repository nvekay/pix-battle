using System.Collections.Generic;

namespace BattlePix.Core
{
    /// <summary>
    /// Очередь команд. Принимает команду на тик в [World.Tick, World.Tick + horizon],
    /// присваивает Seq внутри тика и пишет каждую принятую команду в лог.
    /// </summary>
    public sealed class CommandQueue
    {
        readonly int _horizon;
        readonly List<Command> _pending = new List<Command>();
        readonly List<Command> _log = new List<Command>();
        readonly List<Command> _scratch = new List<Command>();

        public CommandQueue(int horizonTicks)
        {
            _horizon = horizonTicks;
        }

        /// <summary>Все принятые команды в порядке приёма. Это и есть лог для реплея.</summary>
        public IReadOnlyList<Command> Log => _log;

        public int PendingCount => _pending.Count;

        public EnqueueResult Enqueue(int currentTick, bool isOver, ref Command cmd)
        {
            if (isOver) return EnqueueResult.GameOver;
            if (cmd.Tick < currentTick) return EnqueueResult.TickInPast;
            if (cmd.Tick > currentTick + _horizon) return EnqueueResult.BeyondHorizon;

            int seq = 0;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Tick == cmd.Tick) seq++;
            cmd.Seq = seq;

            _pending.Add(cmd);
            _log.Add(cmd);
            return EnqueueResult.Accepted;
        }

        /// <summary>
        /// Команда ядра (ИИ) на тик currentTick + 1. Валидация и применение те же, но в лог реплея она не идёт:
        /// при проигрывании ИИ отдаст её сам.
        /// </summary>
        internal void EnqueueInternal(int currentTick, ref Command cmd)
        {
            cmd.Tick = currentTick + 1;
            int seq = 0;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Tick == cmd.Tick) seq++;
            cmd.Seq = seq;
            _pending.Add(cmd);
        }

        /// <summary>
        /// Забирает команды на данный тик, отсортированные по (Nation, Seq).
        /// Результат живёт до следующего вызова.
        /// </summary>
        internal List<Command> TakeForTick(int tick)
        {
            _scratch.Clear();
            int write = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var c = _pending[i];
                if (c.Tick == tick) _scratch.Add(c);
                else _pending[write++] = c;
            }
            _pending.RemoveRange(write, _pending.Count - write);
            _scratch.Sort(CompareNationSeq);
            return _scratch;
        }

        static int CompareNationSeq(Command a, Command b)
        {
            int c = a.Nation.CompareTo(b.Nation);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }
    }
}
