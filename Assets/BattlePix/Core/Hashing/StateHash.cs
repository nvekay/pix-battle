using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Хеш состояния: FNV-1a 64 по байтам в порядке Tick (4 байта LE), Owner, Terrain,
    /// все NationState побайтно (Id, Alive), state и inc всех потоков в порядке RngStream (по 8 байт LE),
    /// IsOver, EndReason.
    /// </summary>
    public static class StateHash
    {
        /// <summary>Основной путь: Burst-джоб на главном потоке.</summary>
        public static ulong Compute(World world)
        {
            var rng = new ulong[RngStreams.SnapshotLength];
            world.Rng.WriteSnapshot(rng);
            var rngNative = new NativeArray<ulong>(rng, Allocator.TempJob);
            var result = new NativeArray<ulong>(1, Allocator.TempJob);
            try
            {
                new HashJob
                {
                    Tick = world.Tick,
                    Owner = world.Owner,
                    Terrain = world.Terrain,
                    Nations = world.Nations,
                    Rng = rngNative.AsReadOnly(),
                    IsOver = world.IsOver ? (byte)1 : (byte)0,
                    EndReason = (byte)world.EndReason,
                    Result = result,
                }.Run();
                return result[0];
            }
            finally
            {
                rngNative.Dispose();
                result.Dispose();
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct HashJob : IJob
    {
        public int Tick;
        [ReadOnly] public NativeArray<byte>.ReadOnly Owner;
        [ReadOnly] public NativeArray<byte>.ReadOnly Terrain;
        [ReadOnly] public NativeArray<NationState>.ReadOnly Nations;
        [ReadOnly] public NativeArray<ulong>.ReadOnly Rng;
        public byte IsOver;
        public byte EndReason;
        [WriteOnly] public NativeArray<ulong> Result;

        public void Execute()
        {
            const ulong prime = Fnv1a64.Prime;
            ulong h = Fnv1a64.Offset;

            uint t = unchecked((uint)Tick);
            for (int i = 0; i < 4; i++) h = unchecked((h ^ (byte)(t >> (8 * i))) * prime);

            for (int i = 0; i < Owner.Length; i++) h = unchecked((h ^ Owner[i]) * prime);
            for (int i = 0; i < Terrain.Length; i++) h = unchecked((h ^ Terrain[i]) * prime);

            for (int i = 0; i < Nations.Length; i++)
            {
                var n = Nations[i];
                h = unchecked((h ^ n.Id) * prime);
                h = unchecked((h ^ n.Alive) * prime);
            }

            for (int i = 0; i < Rng.Length; i++)
            {
                ulong v = Rng[i];
                for (int b = 0; b < 8; b++) h = unchecked((h ^ (byte)(v >> (8 * b))) * prime);
            }

            h = unchecked((h ^ IsOver) * prime);
            h = unchecked((h ^ EndReason) * prime);
            Result[0] = h;
        }
    }

    /// <summary>Managed-версия хеша для сверки с HashJob в тестах. В игре не используется.</summary>
    public static class HashManaged
    {
        public static ulong Compute(World world)
        {
            ulong h = Fnv1a64.Offset;
            h = Fnv1a64.AddInt32(h, world.Tick);
            h = Fnv1a64.Add(h, world.Owner.ToArray());
            h = Fnv1a64.Add(h, world.Terrain.ToArray());
            var nations = world.Nations;
            for (int i = 0; i < nations.Length; i++)
            {
                h = Fnv1a64.Add(h, nations[i].Id);
                h = Fnv1a64.Add(h, nations[i].Alive);
            }
            var rng = new ulong[RngStreams.SnapshotLength];
            world.Rng.WriteSnapshot(rng);
            for (int i = 0; i < rng.Length; i++) h = Fnv1a64.AddUInt64(h, rng[i]);
            h = Fnv1a64.Add(h, world.IsOver ? (byte)1 : (byte)0);
            h = Fnv1a64.Add(h, (byte)world.EndReason);
            return h;
        }
    }
}
