namespace BattlePix.Core
{
    /// <summary>
    /// Номера потоков фиксированы навсегда. Только дописывать в конец, никогда не перенумеровывать.
    /// </summary>
    public enum RngStream : ulong
    {
        MapGen = 1,
        Front = 2,
        Debug = 3,
        Ai = 4,
        Events = 5,
        /// <summary>Влажность для биомов (спека 02a §5.1). Только картинка, но в хеше, как всё состояние генерации.</summary>
        Biome = 6,
    }

    /// <summary>
    /// По потоку PCG32 на систему: новая система не сдвигает случайность старых.
    /// Поток streamId = new Pcg32(seed, streamId).
    /// </summary>
    public sealed class RngStreams
    {
        /// <summary>Все потоки в порядке возрастания номера. Этот порядок использует хеш состояния.</summary>
        public static readonly RngStream[] All =
        {
            RngStream.MapGen,
            RngStream.Front,
            RngStream.Debug,
            RngStream.Ai,
            RngStream.Events,
            RngStream.Biome,
        };

        readonly Pcg32[] _streams;

        public ulong Seed { get; }

        public RngStreams(ulong seed)
        {
            Seed = seed;
            _streams = new Pcg32[All.Length];
            for (int i = 0; i < All.Length; i++)
                _streams[i] = new Pcg32(seed, (ulong)All[i]);
        }

        static int IndexOf(RngStream stream)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == stream) return i;
            throw new System.ArgumentOutOfRangeException(nameof(stream), stream, "unknown rng stream");
        }

        /// <summary>Ссылка на поток: вызывай Next/NextInt прямо на ней, чтобы состояние продвигалось.</summary>
        public ref Pcg32 Get(RngStream stream) => ref _streams[IndexOf(stream)];

        /// <summary>Число ulong в снимке: state и inc на каждый поток.</summary>
        public static int SnapshotLength => All.Length * 2;

        /// <summary>Пишет state, inc всех потоков в порядке RngStream.</summary>
        public void WriteSnapshot(ulong[] dst)
        {
            for (int i = 0; i < _streams.Length; i++)
            {
                dst[i * 2] = _streams[i].State;
                dst[i * 2 + 1] = _streams[i].Inc;
            }
        }
    }
}
