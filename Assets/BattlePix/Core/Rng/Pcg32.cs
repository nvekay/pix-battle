namespace BattlePix.Core
{
    /// <summary>
    /// PCG32, вариант XSH RR, как в эталонной pcg-c. Единственный источник случайности ядра.
    /// </summary>
    public struct Pcg32
    {
        public ulong State;
        public ulong Inc;

        const ulong Multiplier = 6364136223846793005UL;

        public Pcg32(ulong initState, ulong initSeq)
        {
            State = 0UL;
            Inc = (initSeq << 1) | 1UL;
            Next();
            State = unchecked(State + initState);
            Next();
        }

        public uint Next()
        {
            ulong old = State;
            State = unchecked(old * Multiplier + Inc);
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
        }

        /// <summary>
        /// PCG-хеш RXS-M-XS 32 (pcg_hash у Jarzynski и Olano, 2020). Без состояния: случайность фронта
        /// берётся из хеша (соль тика, пиксель, сосед), поэтому не зависит от порядка обработки и числа потоков.
        /// </summary>
        public static uint Hash(uint v)
        {
            uint state = unchecked(v * 747796405u + 2891336453u);
            uint word = unchecked(((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u);
            return (word >> 22) ^ word;
        }

        /// <summary>Бросок фронта для пикселя t и соседа k (0..3) в 0..9999.</summary>
        public static int FrontRoll(uint salt, int t, int k) =>
            (int)(Hash(salt ^ Hash(unchecked((uint)t * 4u + (uint)k))) % 10000u);

        /// <summary>Равномерное целое в [0, boundExclusive) без перекоса. bound должен быть больше 0.</summary>
        public int NextInt(int boundExclusive)
        {
            if (boundExclusive <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(boundExclusive), "bound must be > 0");
            uint bound = (uint)boundExclusive;
            uint threshold = unchecked(0u - bound) % bound;
            while (true)
            {
                uint r = Next();
                if (r >= threshold)
                    return (int)(r % bound);
            }
        }
    }
}
