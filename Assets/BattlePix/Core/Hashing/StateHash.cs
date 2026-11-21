using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Хеш состояния: FNV-1a 64 по байтам (int — 4 байта LE, short — 2 байта LE, ulong — 8 байт LE) в порядке:
    /// 1) Tick, Owner, Terrain, Elevation, Biome;
    /// 2) для каждой нации по id: Id, Alive, Capital, Pixels, Gold, Divisions, DiedAtTick,
    ///    RecruitedInfDiv, RecruitedArmorDiv, DivisionsLost;
    /// 3) Divisions по слотам: Owner, Alive, Type, Front, ReadyTick, ArriveTick, Strength_bp, Org_bp, Exhausted;
    /// 4) FrontOrder целиком;
    /// 5) FlipHistory целиком;
    /// 6) state и inc всех потоков в порядке RngStream;
    /// 7) IsOver, EndReason, Winner.
    /// CapitalCover, FrontLen, FrontTerrainLen, SidePow и Frontier — производные, в хеш не входят (спека 01b §4.5, 01c §3.4).
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
                    Elevation = world.Elevation,
                    Biome = world.Biome,
                    Nations = world.Nations,
                    Divisions = world.Divisions,
                    FrontOrder = world.FrontOrder,
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
        [ReadOnly] public NativeArray<byte>.ReadOnly Elevation;
        [ReadOnly] public NativeArray<byte>.ReadOnly Biome;
        [ReadOnly] public NativeArray<NationState>.ReadOnly Nations;
        [ReadOnly] public NativeArray<Division>.ReadOnly Divisions;
        [ReadOnly] public NativeArray<byte>.ReadOnly FrontOrder;
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
            for (int i = 0; i < Elevation.Length; i++) h = B(h, Elevation[i]);
            for (int i = 0; i < Biome.Length; i++) h = B(h, Biome[i]);

            for (int i = 0; i < Nations.Length; i++)
            {
                var n = Nations[i];
                h = B(h, n.Id);
                h = B(h, n.Alive);
                h = I(h, n.Capital);
                h = I(h, n.Pixels);
                h = I(h, n.Gold);
                h = B(h, n.Divisions);
                h = I(h, n.DiedAtTick);
                h = I(h, n.RecruitedInfDiv);
                h = I(h, n.RecruitedArmorDiv);
                h = I(h, n.DivisionsLost);
            }

            for (int i = 0; i < Divisions.Length; i++)
            {
                var v = Divisions[i];
                h = B(h, v.Owner);
                h = B(h, v.Alive);
                h = B(h, v.Type);
                h = B(h, v.Front);
                h = I(h, v.ReadyTick);
                h = I(h, v.ArriveTick);
                h = S(h, v.Strength_bp);
                h = S(h, v.Org_bp);
                h = B(h, v.Exhausted);
            }

            for (int i = 0; i < FrontOrder.Length; i++) h = B(h, FrontOrder[i]);

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
            h = Fnv1a64.Add(h, world.Elevation.ToArray());
            h = Fnv1a64.Add(h, world.Biome.ToArray());
            var nations = world.Nations;
            for (int i = 0; i < nations.Length; i++)
            {
                var n = nations[i];
                h = Fnv1a64.Add(h, n.Id);
                h = Fnv1a64.Add(h, n.Alive);
                h = Fnv1a64.AddInt32(h, n.Capital);
                h = Fnv1a64.AddInt32(h, n.Pixels);
                h = Fnv1a64.AddInt32(h, n.Gold);
                h = Fnv1a64.Add(h, n.Divisions);
                h = Fnv1a64.AddInt32(h, n.DiedAtTick);
                h = Fnv1a64.AddInt32(h, n.RecruitedInfDiv);
                h = Fnv1a64.AddInt32(h, n.RecruitedArmorDiv);
                h = Fnv1a64.AddInt32(h, n.DivisionsLost);
            }
            var divisions = world.Divisions;
            for (int i = 0; i < divisions.Length; i++)
            {
                var v = divisions[i];
                h = Fnv1a64.Add(h, v.Owner);
                h = Fnv1a64.Add(h, v.Alive);
                h = Fnv1a64.Add(h, v.Type);
                h = Fnv1a64.Add(h, v.Front);
                h = Fnv1a64.AddInt32(h, v.ReadyTick);
                h = Fnv1a64.AddInt32(h, v.ArriveTick);
                h = Fnv1a64.AddInt16(h, v.Strength_bp);
                h = Fnv1a64.AddInt16(h, v.Org_bp);
                h = Fnv1a64.Add(h, v.Exhausted);
            }
            var order = world.FrontOrder;
            for (int i = 0; i < order.Length; i++) h = Fnv1a64.Add(h, order[i]);
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
