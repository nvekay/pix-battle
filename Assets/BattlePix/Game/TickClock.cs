namespace BattlePix.Game
{
    /// <summary>
    /// Аккумулятор времени: сколько тиков сделать в этом кадре.
    /// Скорость меняет только темп, а не результат. Не больше maxTicksPerFrame за кадр;
    /// если не успеваем, остаток догоняется в следующих кадрах, тики не пропускаются.
    /// </summary>
    public sealed class TickClock
    {
        readonly int _ticksPerSecond;
        readonly int _maxTicksPerFrame;

        /// <summary>Сколько секунд реального времени можно «задолжать». Больше — игра просто замедляется.</summary>
        public double MaxDebtSeconds = 4.0;

        double _accumulatedTicks;

        public int Speed { get; private set; } = 1;
        public bool Paused { get; private set; }
        public int PendingSingleSteps { get; private set; }

        public TickClock(int ticksPerSecond, int maxTicksPerFrame)
        {
            _ticksPerSecond = ticksPerSecond;
            _maxTicksPerFrame = maxTicksPerFrame;
        }

        public void SetSpeed(int speed)
        {
            Speed = speed;
            Paused = false;
        }

        public void Pause()
        {
            Paused = true;
            _accumulatedTicks = 0;
        }

        /// <summary>Один тик на паузе.</summary>
        public void RequestStep()
        {
            if (Paused) PendingSingleSteps++;
        }

        public void Reset()
        {
            _accumulatedTicks = 0;
            PendingSingleSteps = 0;
        }

        public int TicksForFrame(double unscaledDeltaSeconds)
        {
            if (Paused)
            {
                int s = PendingSingleSteps > 0 ? 1 : 0;
                PendingSingleSteps -= s;
                return s;
            }

            _accumulatedTicks += unscaledDeltaSeconds * _ticksPerSecond * Speed;
            double maxDebt = MaxDebtSeconds * _ticksPerSecond * Speed;
            if (_accumulatedTicks > maxDebt) _accumulatedTicks = maxDebt;

            int ticks = (int)_accumulatedTicks;
            if (ticks > _maxTicksPerFrame) ticks = _maxTicksPerFrame;
            _accumulatedTicks -= ticks;
            return ticks;
        }
    }
}
