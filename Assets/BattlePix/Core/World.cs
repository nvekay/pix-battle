using System;
using Unity.Collections;

namespace BattlePix.Core
{
    public enum EndReason : byte
    {
        None = 0,
        TickLimit = 1,
        // Victory, Defeat добавит прототип. Значения только дописываются.
    }

    public struct NationState
    {
        public byte Id;
        public byte Alive;
    }

    /// <summary>
    /// Состояние мира. Снаружи Owner и Terrain доступны только на чтение,
    /// пишут в них системы ядра и применение команд.
    /// </summary>
    public sealed class World : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public int PixelCount => Width * Height;

        NativeArray<byte> _owner;
        NativeArray<byte> _terrain;
        NativeArray<NationState> _nations;

        public int Tick { get; internal set; }
        public RngStreams Rng { get; }
        public bool IsOver { get; internal set; }
        public EndReason EndReason { get; internal set; }

        /// <summary>Диапазон строк, изменённых с последнего чтения рендером. −1, если изменений нет.</summary>
        public int DirtyRowMin { get; private set; } = -1;
        public int DirtyRowMax { get; private set; } = -1;

        public bool IsDisposed { get; private set; }

        public World(int width, int height, int nationCount, ulong seed)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (nationCount < 0 || nationCount > 255) throw new ArgumentOutOfRangeException(nameof(nationCount));
            Width = width;
            Height = height;
            _owner = new NativeArray<byte>(width * height, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _terrain = new NativeArray<byte>(width * height, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _nations = new NativeArray<NationState>(nationCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < nationCount; i++)
                _nations[i] = new NationState { Id = (byte)(i + 1), Alive = 1 };
            Rng = new RngStreams(seed);
        }

        public NativeArray<byte>.ReadOnly Owner => _owner.AsReadOnly();
        public NativeArray<byte>.ReadOnly Terrain => _terrain.AsReadOnly();
        public NativeArray<NationState>.ReadOnly Nations => _nations.AsReadOnly();
        public int NationCount => _nations.Length;

        // Доступ на запись — только внутри ядра.
        internal NativeArray<byte> OwnerRW => _owner;
        internal NativeArray<byte> TerrainRW => _terrain;
        internal NativeArray<NationState> NationsRW => _nations;

        public int Index(int x, int y) => y * Width + x;
        public bool InMap(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        internal void SetOwner(int index, byte owner)
        {
            _owner[index] = owner;
            MarkDirtyRow(index / Width);
        }

        internal void MarkDirtyRow(int y)
        {
            if (DirtyRowMin < 0 || y < DirtyRowMin) DirtyRowMin = y;
            if (DirtyRowMax < 0 || y > DirtyRowMax) DirtyRowMax = y;
        }

        /// <summary>Рендер вызывает после того, как забрал изменённые строки. На хеш не влияет.</summary>
        public void ClearDirtyRows()
        {
            DirtyRowMin = -1;
            DirtyRowMax = -1;
        }

        /// <summary>Пометить всю карту изменённой (например, для первой загрузки текстуры).</summary>
        public void MarkAllDirty()
        {
            DirtyRowMin = 0;
            DirtyRowMax = Height - 1;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_owner.IsCreated) _owner.Dispose();
            if (_terrain.IsCreated) _terrain.Dispose();
            if (_nations.IsCreated) _nations.Dispose();
        }
    }
}
