using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Хеш состояния: FNV-1a 64 по байтам (int — 4 байта LE, short — 2 байта LE, ulong — 8 байт LE) в порядке:
    /// 1) Tick, Owner, Terrain;
    /// 2) для каждой нации по id: Id, Alive, Capital, Pixels, Gold, Battalions, FortsAlive, DiedAtTick, Recruits, FortsBuilt, Strikes;
    /// 3) Forts по слотам: Pixel, Owner, Alive;
    /// 4) StrikeUntil, FlipHistory целиком;
    /// 5) state и inc всех потоков в порядке RngStream;
    /// 6) IsOver, EndReason, Winner.
    /// FortCover, FrontLen и Frontier — производные, в хеш не входят.
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
                    Forts = world.Forts,
                    StrikeUntil = world.StrikeUntil,
                    FlipHistory = world.FlipHistory,
                    Rng = rngNative.AsReadOnly(),
                    IsOver = world.IsOver ? (byte)1 : (byte)0,
                    EndReason = (byte)world.EndReason,
                    Winner = world.Winner,
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
        [ReadOnly] public NativeArray<Fort>.ReadOnly Forts;
        [ReadOnly] public NativeArray<int>.ReadOnly StrikeUntil;
        [ReadOnly] public NativeArray<short>.ReadOnly FlipHistory;
        [ReadOnly] public NativeArray<ulong>.ReadOnly Rng;
        public byte IsOver;
        public byte EndReason;
        public byte Winner;
        [WriteOnly] public NativeArray<ulong> Result;

        const ulong Prime = Fnv1a64.Prime;

        static ulong B(ulong h, byte b) => unchecked((h ^ b) * Prime);

        static ulong I(ulong h, int v)
        {
            uint u = unchecked((uint)v);
            for (int i = 0; i < 4; i++) h = B(h, (byte)(u >> (8 * i)));
            return h;
        }

        public void Execute()
        {
            ulong h = Fnv1a64.Offset;

            h = I(h, Tick);
            for (int i = 0; i < Owner.Length; i++) h = B(h, Owner[i]);
            for (int i = 0; i < Terrain.Length; i++) h = B(h, Terrain[i]);

            for (int i = 0; i < Nations.Length; i++)
            {
                var n = Nations[i];
                h = B(h, n.Id);
                h = B(h, n.Alive);
                h = I(h, n.Capital);
                h = I(h, n.Pixels);
                h = I(h, n.Gold);
                h = I(h, n.Battalions);
                h = B(h, n.FortsAlive);
                h = I(h, n.DiedAtTick);
                h = I(h, n.Recruits);
                h = I(h, n.FortsBuilt);
                h = I(h, n.Strikes);
            }

            for (int i = 0; i < Forts.Length; i++)
            {
                var f = Forts[i];
                h = I(h, f.Pixel);
                h = B(h, f.Owner);
                h = B(h, f.Alive);
            }

            for (int i = 0; i < StrikeUntil.Length; i++) h = I(h, StrikeUntil[i]);
            for (int i = 0; i < FlipHistory.Length; i++)
            {
                ushort u = unchecked((ushort)FlipHistory[i]);
                h = B(h, (byte)u);
                h = B(h, (byte)(u >> 8));
            }

            for (int i = 0; i < Rng.Length; i++)
            {
                ulong v = Rng[i];
                for (int b = 0; b < 8; b++) h = B(h, (byte)(v >> (8 * b)));
            }

            h = B(h, IsOver);
            h = B(h, EndReason);
            h = B(h, Winner);
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
                var n = nations[i];
                h = Fnv1a64.Add(h, n.Id);
                h = Fnv1a64.Add(h, n.Alive);
                h = Fnv1a64.AddInt32(h, n.Capital);
                h = Fnv1a64.AddInt32(h, n.Pixels);
                h = Fnv1a64.AddInt32(h, n.Gold);
                h = Fnv1a64.AddInt32(h, n.Battalions);
                h = Fnv1a64.Add(h, n.FortsAlive);
                h = Fnv1a64.AddInt32(h, n.DiedAtTick);
                h = Fnv1a64.AddInt32(h, n.Recruits);
                h = Fnv1a64.AddInt32(h, n.FortsBuilt);
                h = Fnv1a64.AddInt32(h, n.Strikes);
            }
            var forts = world.Forts;
            for (int i = 0; i < forts.Length; i++)
            {
                h = Fnv1a64.AddInt32(h, forts[i].Pixel);
                h = Fnv1a64.Add(h, forts[i].Owner);
                h = Fnv1a64.Add(h, forts[i].Alive);
            }
            var strikes = world.StrikeUntil;
            for (int i = 0; i < strikes.Length; i++) h = Fnv1a64.AddInt32(h, strikes[i]);
            var flips = world.FlipHistory;
            for (int i = 0; i < flips.Length; i++) h = Fnv1a64.AddInt16(h, flips[i]);
            var rng = new ulong[RngStreams.SnapshotLength];
            world.Rng.WriteSnapshot(rng);
            for (int i = 0; i < rng.Length; i++) h = Fnv1a64.AddUInt64(h, rng[i]);
            h = Fnv1a64.Add(h, world.IsOver ? (byte)1 : (byte)0);
            h = Fnv1a64.Add(h, (byte)world.EndReason);
            h = Fnv1a64.Add(h, world.Winner);
            return h;
        }
    }
}
