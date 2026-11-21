using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Хеш состояния: FNV-1a 64 по байтам (int — 4 байта LE, short — 2 байта LE, ulong — 8 байт LE) в порядке:
    /// 1) Tick, Owner, Terrain;
    /// 2) для каждой нации по id: Id, Alive, Capital, Pixels, Gold, Battalions, Formations, DiedAtTick,
    ///    RecruitedInf, RecruitedArmor, BattalionsLost;
    /// 3) Formations по слотам: Owner, Alive, Front, Order, ArriveTick;
    /// 4) Battalions по слотам: Type, Alive, Strength_bp;
    /// 5) FlipHistory целиком;
    /// 6) state и inc всех потоков в порядке RngStream;
    /// 7) IsOver, EndReason, Winner.
    /// CapitalCover, FrontLen, FrontTerrainLen, SidePow и Frontier — производные, в хеш не входят (спека 01b §4.5).
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
                    Formations = world.Formations,
                    Battalions = world.Battalions,
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
        [ReadOnly] public NativeArray<Formation>.ReadOnly Formations;
        [ReadOnly] public NativeArray<Battalion>.ReadOnly Battalions;
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

        static ulong S(ulong h, short v)
        {
            ushort u = unchecked((ushort)v);
            return B(B(h, (byte)u), (byte)(u >> 8));
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
                h = B(h, n.Formations);
                h = I(h, n.DiedAtTick);
                h = I(h, n.RecruitedInf);
                h = I(h, n.RecruitedArmor);
                h = I(h, n.BattalionsLost);
            }

            for (int i = 0; i < Formations.Length; i++)
            {
                var f = Formations[i];
                h = B(h, f.Owner);
                h = B(h, f.Alive);
                h = B(h, f.Front);
                h = B(h, f.Order);
                h = I(h, f.ArriveTick);
            }

            for (int i = 0; i < Battalions.Length; i++)
            {
                var b = Battalions[i];
                h = B(h, b.Type);
                h = B(h, b.Alive);
                h = S(h, b.Strength_bp);
            }

            for (int i = 0; i < FlipHistory.Length; i++) h = S(h, FlipHistory[i]);

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
                h = Fnv1a64.Add(h, n.Formations);
                h = Fnv1a64.AddInt32(h, n.DiedAtTick);
                h = Fnv1a64.AddInt32(h, n.RecruitedInf);
                h = Fnv1a64.AddInt32(h, n.RecruitedArmor);
                h = Fnv1a64.AddInt32(h, n.BattalionsLost);
            }
            var formations = world.Formations;
            for (int i = 0; i < formations.Length; i++)
            {
                var f = formations[i];
                h = Fnv1a64.Add(h, f.Owner);
                h = Fnv1a64.Add(h, f.Alive);
                h = Fnv1a64.Add(h, f.Front);
                h = Fnv1a64.Add(h, f.Order);
                h = Fnv1a64.AddInt32(h, f.ArriveTick);
            }
            var battalions = world.Battalions;
            for (int i = 0; i < battalions.Length; i++)
            {
                var b = battalions[i];
                h = Fnv1a64.Add(h, b.Type);
                h = Fnv1a64.Add(h, b.Alive);
                h = Fnv1a64.AddInt16(h, b.Strength_bp);
            }
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
