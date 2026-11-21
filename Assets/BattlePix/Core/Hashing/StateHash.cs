using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Хеш состояния: FNV-1a 64 по байтам (int — 4 байта LE, short — 2 байта LE, ulong — 8 байт LE) в порядке:
    /// 1) Tick, Owner, Terrain, Elevation, Biome, Core (02b);
    /// 2) для каждой нации по id: Id, Alive, Capital, Pixels, Gold, Divisions, DiedAtTick,
    ///    RecruitedInfDiv, RecruitedArmorDiv, DivisionsLost, LastWarEndTick;
    /// 3) Divisions по слотам: Owner, Alive, Type, Front, ReadyTick, ArriveTick, Strength_bp, Org_bp, Exhausted;
    /// 4) FrontOrder целиком, затем Wars по парам a &lt; b (a по возрастанию, внутри b по возрастанию), поля в порядке объявления War;
    /// 5) FlipHistory целиком;
    /// 6) state и inc всех потоков в порядке RngStream;
    /// 7) IsOver, EndReason, Winner.
    /// CapitalCover, FrontLen, FrontTerrainLen, SidePow, Frontier, OccupiedLen, ClaimHeldLen — производные, в хеш не входят
    /// (спека 01b §4.5, 01c §3.4, 02b §3.4). WarEvents и TributePaid — журнал, не состояние.
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
                    Core = world.Core,
                    Nations = world.Nations,
                    Divisions = world.Divisions,
                    FrontOrder = world.FrontOrder,
                    Wars = world.Wars,
                    NationCount = world.Wars.Length > 0 ? world.NationCount : 0,
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
        [ReadOnly] public NativeArray<byte>.ReadOnly Core;
        [ReadOnly] public NativeArray<NationState>.ReadOnly Nations;
        [ReadOnly] public NativeArray<Division>.ReadOnly Divisions;
        [ReadOnly] public NativeArray<byte>.ReadOnly FrontOrder;
        [ReadOnly] public NativeArray<War>.ReadOnly Wars;
        /// <summary>N для обхода Wars; 0 — мир без правил (Wars пуст).</summary>
        public int NationCount;
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
            for (int i = 0; i < Core.Length; i++) h = B(h, Core[i]);

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
                h = I(h, n.LastWarEndTick);
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

            for (int a = 1; a <= NationCount; a++)
            for (int b = a + 1; b <= NationCount; b++)
            {
                var x = Wars[(a - 1) * NationCount + (b - 1)];
                h = B(h, x.Status);
                h = B(h, x.Attacker);
                h = B(h, x.Goal);
                h = I(h, x.ClaimCenter);
                h = I(h, x.ClaimTotal);
                h = I(h, x.StartTick);
                h = I(h, x.PreWarAtt);
                h = I(h, x.PreWarDef);
                h = S(h, x.HoldAtt);
                h = S(h, x.HoldDef);
                h = B(h, x.OfferFrom);
                h = I(h, x.OfferUntil);
                h = I(h, x.OfferCooldownAtt);
                h = I(h, x.OfferCooldownDef);
                h = B(h, x.PendingPeace);
                h = I(h, x.TruceUntil);
                h = B(h, x.TributePayer);
                h = B(h, x.TributePayee);
                h = I(h, x.TributeUntil);
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
            h = Fnv1a64.Add(h, world.Elevation.ToArray());
            h = Fnv1a64.Add(h, world.Biome.ToArray());
            h = Fnv1a64.Add(h, world.Core.ToArray());
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
                h = Fnv1a64.AddInt32(h, n.LastWarEndTick);
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
            int nc = world.Wars.Length > 0 ? world.NationCount : 0;
            var wars = world.Wars;
            for (int a = 1; a <= nc; a++)
            for (int b = a + 1; b <= nc; b++)
            {
                var x = wars[(a - 1) * nc + (b - 1)];
                h = Fnv1a64.Add(h, x.Status);
                h = Fnv1a64.Add(h, x.Attacker);
                h = Fnv1a64.Add(h, x.Goal);
                h = Fnv1a64.AddInt32(h, x.ClaimCenter);
                h = Fnv1a64.AddInt32(h, x.ClaimTotal);
                h = Fnv1a64.AddInt32(h, x.StartTick);
                h = Fnv1a64.AddInt32(h, x.PreWarAtt);
                h = Fnv1a64.AddInt32(h, x.PreWarDef);
                h = Fnv1a64.AddInt16(h, x.HoldAtt);
                h = Fnv1a64.AddInt16(h, x.HoldDef);
                h = Fnv1a64.Add(h, x.OfferFrom);
                h = Fnv1a64.AddInt32(h, x.OfferUntil);
                h = Fnv1a64.AddInt32(h, x.OfferCooldownAtt);
                h = Fnv1a64.AddInt32(h, x.OfferCooldownDef);
                h = Fnv1a64.Add(h, x.PendingPeace);
                h = Fnv1a64.AddInt32(h, x.TruceUntil);
                h = Fnv1a64.Add(h, x.TributePayer);
                h = Fnv1a64.Add(h, x.TributePayee);
                h = Fnv1a64.AddInt32(h, x.TributeUntil);
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
