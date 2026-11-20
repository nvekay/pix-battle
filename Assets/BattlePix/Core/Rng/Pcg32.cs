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
