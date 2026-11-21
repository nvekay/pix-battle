using System;
using BattlePix.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>
    /// Запекание 2.5D-карты (спека 02a §5.3): один раз при старте партии, на мир не влияет.
    /// Текстура W·S × (H·S + PAD), север сверху. Массивы лежат в порядке Texture2D: строка 0 — низ картинки,
    /// поэтому пиксель картинки (bx, by), где by считается сверху, живёт по индексу (Hb − 1 − by) · Wb + bx.
    /// Случайность — только хеш координат (Hash), RngStreams рендер не трогает.
    /// </summary>
    public static class TerrainBaker
    {
        public const ushort NoCell = 0xFFFF;

        public const byte KindWater = 0, KindTop = 1, KindWall = 2, KindDecor = 3;

        /// <summary>Порядок спрайтов декора в атласе (DecorAtlas.Sprites). Только дописывается.</summary>
        public enum Decor { Tree = 0, TreeL = 1, PineL = 2, Tree2 = 3, Pine = 4, SPine = 5, Cact = 6, Rock = 7, Bush = 8 }

        public const int DecorCount = 9;

        /// <summary>
        /// Хеш клетки или пикселя: x·73856093 ^ y·19349663 (как в docs/art/mock25.py) ^ seed·0x9E3779B9,
        /// затем перемешивание младших бит (finalizer lowbias32), чтобы % давал ровное распределение.
        /// </summary>
        public static uint Hash(int x, int y, uint seed)
        {
            uint h = unchecked((uint)x * 73856093u ^ (uint)y * 19349663u ^ seed * 0x9E3779B9u);
            h ^= h >> 16;
            h = unchecked(h * 0x7FEB352Du);
            h ^= h >> 15;
            h = unchecked(h * 0x846CA68Bu);
            h ^= h >> 16;
            return h;
        }

        /// <summary>Пиксели спрайтов декора, сверху вниз, подряд; Rects[k] = (смещение, ширина, высота, 0).</summary>
        public sealed class DecorSprites : IDisposable
        {
            public NativeArray<Color32> Pixels;
            public NativeArray<int4> Rects;

            public static DecorSprites FromAtlas(DecorAtlas atlas)
            {
                if (atlas == null || atlas.Texture == null) throw new ArgumentException("decor atlas is missing");
                if (atlas.Sprites == null || atlas.Sprites.Length < DecorCount) throw new ArgumentException("decor atlas has too few sprites");
                var tex = atlas.Texture.GetPixels32(); // снизу вверх
                int tw = atlas.Texture.width, th = atlas.Texture.height;
                int total = 0;
                for (int k = 0; k < DecorCount; k++)
                {
                    var e = atlas.Sprites[k];
                    if (e.Name != ((Decor)k).ToString()) throw new ArgumentException($"decor atlas: sprite {k} is {e.Name}, expected {(Decor)k}");
                    total += e.Rect.width * e.Rect.height;
                }
                var d = new DecorSprites
                {
                    Pixels = new NativeArray<Color32>(total, Allocator.Persistent),
                    Rects = new NativeArray<int4>(DecorCount, Allocator.Persistent),
                };
                int off = 0;
                for (int k = 0; k < DecorCount; k++)
                {
                    var r = atlas.Sprites[k].Rect; // y — от верха картинки атласа
                    d.Rects[k] = new int4(off, r.width, r.height, 0);
                    for (int j = 0; j < r.height; j++)
                    for (int i = 0; i < r.width; i++)
                    {
                        int ty = th - 1 - (r.y + j);
                        d.Pixels[off + j * r.width + i] = tex[ty * tw + r.x + i];
                    }
                    off += r.width * r.height;
                }
                return d;
            }

            public void Dispose()
            {
                if (Pixels.IsCreated) Pixels.Dispose();
                if (Rects.IsCreated) Rects.Dispose();
            }
        }

        public sealed class Result : IDisposable
        {
            public int MapWidth, MapHeight, CellPx, Pad, Width, Height;
            /// <summary>RGBA32, в альфе вид пикселя (Kind*).</summary>
            public NativeArray<Color32> Color;
            /// <summary>Мип 1 (усреднение 2×2) для зума ½: Width/2 × Height/2.</summary>
            public NativeArray<Color32> ColorMip1;
            /// <summary>RG32: (x, y) клетки, 2 ushort на пиксель; NoCell над картой.</summary>
            public NativeArray<ushort> CellId;
            /// <summary>Уровень террасы 0..6 на клетку карты.</summary>
            public NativeArray<byte> Level;
            public NativeArray<int> Offsets;
            public int DecorCount;
            public double Ms;

            public int TexIndex(int bx, int by) => (Height - 1 - by) * Width + bx;

            /// <summary>Верх грани клетки: строка картинки (сверху) первой строки верхней грани.</summary>
            public int TopRow(int x, int y) => y * CellPx + Pad - Offsets[Level[y * MapWidth + x]];

            public void Dispose()
            {
                if (Color.IsCreated) Color.Dispose();
                if (ColorMip1.IsCreated) ColorMip1.Dispose();
                if (CellId.IsCreated) CellId.Dispose();
                if (Level.IsCreated) Level.Dispose();
                if (Offsets.IsCreated) Offsets.Dispose();
            }
        }

        /// <summary>Печёт карту мира. seed — младшие 32 бита сида партии.</summary>
        public static Result Bake(World world, PaletteData pal, DecorSprites decor, uint seed)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int w = world.Width, h = world.Height, s = pal.CellPx, pad = pal.MaxOffset;
            int bw = w * s, bh = h * s + pad;
            var r = new Result
            {
                MapWidth = w, MapHeight = h, CellPx = s, Pad = pad, Width = bw, Height = bh,
                Color = new NativeArray<Color32>(bw * bh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                ColorMip1 = new NativeArray<Color32>((bw / 2) * (bh / 2), Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                CellId = new NativeArray<ushort>(bw * bh * 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Level = new NativeArray<byte>(w * h, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Offsets = new NativeArray<int>(pal.TerraceOffsets, Allocator.Persistent),
            };

            var topCol = new NativeArray<int3>(10, Allocator.TempJob);
            var wallCol = new NativeArray<int3>(10, Allocator.TempJob);
            var density = new NativeArray<int>(10, Allocator.TempJob);
            var water = new NativeArray<int3>(5, Allocator.TempJob);
            var waterDist = new NativeArray<byte>(w * h, Allocator.TempJob);
            var temp = new NativeArray<int>(w * h, Allocator.TempJob);
            var decorCount = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                for (int b = 1; b <= 9; b++)
                {
                    topCol[b] = C(PaletteData.Hex(pal.BiomeTop[((Biome)b).ToString()]));
                    wallCol[b] = C(PaletteData.Hex(pal.BiomeWall[((Biome)b).ToString()]));
                    density[b] = pal.Density((Biome)b);
                }
                for (int k = 0; k < 4; k++) water[k] = C(PaletteData.Hex(pal.Water.Tones[k]));
                water[4] = C(PaletteData.Hex(pal.Water.Foam));

                var prep = new PrepJob
                {
                    W = w, H = h,
                    Terrain = world.Terrain, Elevation = world.Elevation,
                    Plains = new int2(pal.Bands.Plains[0], pal.Bands.Plains[1]),
                    Hills = new int2(pal.Bands.Hills[0], pal.Bands.Hills[1]),
                    Mountains = new int2(pal.Bands.Mountains[0], pal.Bands.Mountains[1]),
                    Level = r.Level, WaterDist = waterDist, Temp = temp,
                }.Schedule();

                var ground = new GroundJob
                {
                    W = w, H = h, S = s, Pad = pad, BW = bw, BH = bh,
                    Terrain = world.Terrain, Biome = world.Biome, Level = r.Level, Offsets = r.Offsets,
                    WaterDist = waterDist, TopCol = topCol, WallCol = wallCol, Water = water,
                    ToneDist = new int3(pal.Water.ToneDist[0], pal.Water.ToneDist[1], pal.Water.ToneDist[2]),
                    Sky = C(PaletteData.Hex(pal.Sky)),
                    TopNoise = pal.TopNoise_bp, LevelLight = pal.LevelLight_bp, RimLight = pal.RimLight_bp, AoDark = pal.AoDark_bp,
                    WallDark = pal.WallDarkToBottom_bp, StrataEvery = pal.StrataEvery, StrataDark = pal.StrataDark_bp,
                    Color = r.Color, CellId = r.CellId,
                }.Schedule(w, 8, prep);

                var dec = new DecorJob
                {
                    W = w, H = h, S = s, Pad = pad, BW = bw, BH = bh, Seed = seed,
                    Terrain = world.Terrain, Biome = world.Biome, Level = r.Level, Offsets = r.Offsets, Temp = temp,
                    Density = density, Pixels = decor.Pixels, Rects = decor.Rects,
                    Color = r.Color, CellId = r.CellId, Count = decorCount,
                }.Schedule(ground);

                new Mip1Job { BW = bw, BH = bh, Src = r.Color, Dst = r.ColorMip1 }.Schedule(bh / 2, 16, dec).Complete();
                r.DecorCount = decorCount[0];
            }
            finally
            {
                topCol.Dispose();
                wallCol.Dispose();
                density.Dispose();
                water.Dispose();
                waterDist.Dispose();
                temp.Dispose();
                decorCount.Dispose();
            }
            sw.Stop();
            r.Ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        static int3 C(Color32 c) => new int3(c.r, c.g, c.b);

        static int3 Mul(int3 c, int bp) => c * bp / 10000;

        static Color32 Px(int3 c, byte kind)
        {
            c = math.clamp(c, 0, 255);
            return new Color32((byte)c.x, (byte)c.y, (byte)c.z, kind);
        }

        // ---------- Подготовка: уровни, расстояние до суши, температура ----------

        [BurstCompile(CompileSynchronously = true)]
        struct PrepJob : IJob
        {
            public int W, H;
            [ReadOnly] public NativeArray<byte>.ReadOnly Terrain;
            [ReadOnly] public NativeArray<byte>.ReadOnly Elevation;
            public int2 Plains, Hills, Mountains;
            public NativeArray<byte> Level;
            public NativeArray<byte> WaterDist;
            public NativeArray<int> Temp;

            public void Execute()
            {
                int n = W * H;
                var eMin = new int4(256);
                var eMax = new int4(-1);
                int landMin = 256, landMax = -1;
                for (int i = 0; i < n; i++)
                {
                    int t = Terrain[i], e = Elevation[i];
                    eMin[t] = math.min(eMin[t], e);
                    eMax[t] = math.max(eMax[t], e);
                    if (t != 0)
                    {
                        landMin = math.min(landMin, e);
                        landMax = math.max(landMax, e);
                    }
                }
                // L = a + (E − eMin) · (b − a + 1) / (eMax − eMin + 1): каждая полоса — свой диапазон уровней.
                for (int i = 0; i < n; i++)
                {
                    int t = Terrain[i];
                    if (t == 0) { Level[i] = 0; continue; }
                    int2 band = t == 2 ? Hills : t == 3 ? Mountains : Plains;
                    int lv = band.x + (Elevation[i] - eMin[t]) * (band.y - band.x + 1) / (eMax[t] - eMin[t] + 1);
                    Level[i] = (byte)lv;
                }

                // Расстояние от воды до суши по Чебышёву (BFS по 8 соседям), не больше 255.
                var queue = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                int head = 0, tail = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Terrain[i] != 0) { WaterDist[i] = 0; queue[tail++] = i; }
                    else WaterDist[i] = 255;
                }
                while (head < tail)
                {
                    int p = queue[head++];
                    int px = p % W, py = p / W, nd = WaterDist[p] + 1;
                    if (nd > 254) continue;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int qx = px + dx, qy = py + dy;
                        if (qx < 0 || qy < 0 || qx >= W || qy >= H) continue;
                        int q = qy * W + qx;
                        if (WaterDist[q] <= nd) continue;
                        WaterDist[q] = (byte)nd;
                        queue[tail++] = q;
                    }
                }
                queue.Dispose();

                // Температура как в ядре (спека 02a §5.1), по Elevation вместо корзин: для выбора ели или лиственного.
                int range = math.max(1, landMax - landMin);
                for (int i = 0; i < n; i++)
                {
                    int y = i / W;
                    int hl = Terrain[i] != 0 ? (Elevation[i] - landMin) * 10000 / range : 0;
                    Temp[i] = (H > 1 ? y * 10000 / (H - 1) : 0) - hl / 2;
                }
            }
        }

        // ---------- Проход 1: земля, по столбцам клеток ----------

        [BurstCompile(CompileSynchronously = true)]
        struct GroundJob : IJobParallelFor
        {
            public int W, H, S, Pad, BW, BH;
            [ReadOnly] public NativeArray<byte>.ReadOnly Terrain;
            [ReadOnly] public NativeArray<byte>.ReadOnly Biome;
            [ReadOnly] public NativeArray<byte> Level;
            [ReadOnly] public NativeArray<int> Offsets;
            [ReadOnly] public NativeArray<byte> WaterDist;
            [ReadOnly] public NativeArray<int3> TopCol, WallCol, Water;
            public int3 ToneDist, Sky;
            public int TopNoise, LevelLight, RimLight, AoDark, WallDark, StrataEvery, StrataDark;

            // Столбец x пишет только в свои S столбцов текстуры: записи не пересекаются.
            [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<Color32> Color;
            [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<ushort> CellId;

            void Put(int bx, int by, Color32 c, int x, int y)
            {
                int i = (BH - 1 - by) * BW + bx;
                Color[i] = c;
                CellId[i * 2] = (ushort)x;
                CellId[i * 2 + 1] = (ushort)y;
            }

            public void Execute(int x)
            {
                var sky = Px(Sky, KindWater);
                for (int by = 0; by < BH; by++)
                for (int sx = 0; sx < S; sx++)
                {
                    int i = (BH - 1 - by) * BW + x * S + sx;
                    Color[i] = sky;
                    CellId[i * 2] = NoCell;
                    CellId[i * 2 + 1] = NoCell;
                }

                for (int y = 0; y < H; y++)
                {
                    int c = y * W + x;
                    bool land = Terrain[c] != 0;
                    int lv = Level[c];
                    int off = Offsets[lv];
                    int offS = y < H - 1 ? Offsets[Level[c + W]] : 0;
                    int offN = y > 0 ? Offsets[Level[c - W]] : 0;
                    bool landN = y > 0 && Terrain[c - W] != 0;
                    int top = y * S + Pad - off;
                    int3 tc = TopCol[Biome[c]];

                    for (int k = 0; k < S; k++)
                    for (int sx = 0; sx < S; sx++)
                    {
                        int bx = x * S + sx, by = top + k;
                        int3 col;
                        byte kind;
                        if (land)
                        {
                            uint h1 = Hash(bx, by, 1), h2 = Hash(bx >> 1, by >> 1, 2);
                            int noise = (int)(h1 % 7) - 3, noise2 = (int)(h2 % 5) - 2;
                            int f = 10000 + noise * TopNoise * 4 / 30 + noise2 * TopNoise * 6 / 20;
                            col = Mul(Mul(tc, f), 10000 + LevelLight * lv);
                            if (k == S - 1 && offS < off) col = Mul(col, 10000 + RimLight);
                            if (offN > off && k < math.min(offN - off, 4)) col = Mul(col, 10000 - AoDark);
                            kind = KindTop;
                        }
                        else
                        {
                            int d = WaterDist[c];
                            int tone = d <= ToneDist.x ? 0 : d <= ToneDist.y ? 1 : d <= ToneDist.z ? 2 : 3;
                            col = Water[tone];
                            uint h1 = Hash(bx, by, 3);
                            if (Hash(bx >> 1, by >> 1, 4) % 13 == 0) col = Mul(col, 10700);
                            if (d == 1 && h1 % 3 == 0) col = Water[4];
                            if (landN && k < 3) col = Mul(col, 7800); // тень берегового обрыва
                            kind = KindWater;
                        }
                        Put(bx, by, Px(col, kind), x, y);
                    }

                    if (!land) continue;
                    int wallFrom = top + S;
                    int wallTo = y < H - 1 ? (y + 1) * S + Pad - offS - 1 : H * S + Pad - 1;
                    int height = wallTo - wallFrom + 1;
                    if (height <= 0) continue;
                    int3 wc = WallCol[Biome[c]];
                    for (int by = wallFrom; by <= wallTo; by++)
                    {
                        int depth = by - wallFrom;
                        int3 col0 = Mul(wc, 10000 - WallDark * depth / math.max(height, 1));
                        if (depth % StrataEvery == StrataEvery - 1) col0 = Mul(col0, 10000 - StrataDark);
                        for (int sx = 0; sx < S; sx++)
                        {
                            int bx = x * S + sx;
                            int3 col = Mul(col0, 10000 + ((int)(Hash(bx, by, 5) % 5) - 2) * 200);
                            Put(bx, by, Px(col, KindWall), x, y);
                        }
                    }
                }
            }
        }

        // ---------- Проход 2: декор, один поток, ряды с севера на юг ----------

        [BurstCompile(CompileSynchronously = true)]
        struct DecorJob : IJob
        {
            public int W, H, S, Pad, BW, BH;
            public uint Seed;
            [ReadOnly] public NativeArray<byte>.ReadOnly Terrain;
            [ReadOnly] public NativeArray<byte>.ReadOnly Biome;
            [ReadOnly] public NativeArray<byte> Level;
            [ReadOnly] public NativeArray<int> Offsets;
            [ReadOnly] public NativeArray<int> Temp;
            [ReadOnly] public NativeArray<int> Density;
            [ReadOnly] public NativeArray<Color32> Pixels;
            [ReadOnly] public NativeArray<int4> Rects;
            public NativeArray<Color32> Color;
            [ReadOnly] public NativeArray<ushort> CellId;
            public NativeArray<int> Count;

            int Pick(byte biome, uint r, int temp, int density)
            {
                switch ((Core.Biome)biome)
                {
                    case Core.Biome.Forest:
                        if (temp < 3200) return r % 2 == 1 ? (int)Decor.PineL : (int)Decor.Pine;
                        return r % 3 == 0 ? (int)Decor.TreeL : r % 3 == 1 ? (int)Decor.Tree : (int)Decor.Tree2;
                    case Core.Biome.Grass: return r % 2 == 1 ? (int)Decor.Tree : (int)Decor.Bush;
                    case Core.Biome.Tundra: return r % 3 == 0 ? (int)Decor.PineL : (int)Decor.Pine;
                    case Core.Biome.Snow: return (int)Decor.SPine;
                    case Core.Biome.Desert: return r < (uint)(density * 2 / 3) ? (int)Decor.Cact : (int)Decor.Rock;
                    case Core.Biome.Hills: return r % 2 == 1 ? (int)Decor.Rock : (int)Decor.Tree2;
                    case Core.Biome.Mountain: return (int)Decor.Rock;
                    case Core.Biome.Steppe: return (int)Decor.Bush;
                    default: return -1;
                }
            }

            public void Execute()
            {
                int count = 0;
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int c = y * W + x;
                    if (Terrain[c] == 0) continue;
                    byte b = Biome[c];
                    uint r = Hash(x, y, Seed) % 10000;
                    int dens = Density[b];
                    if (r >= (uint)dens) continue;
                    int sp = Pick(b, r, Temp[c], dens);
                    if (sp < 0) continue;
                    count++;
                    int4 rect = Rects[sp];
                    int sw = rect.y, sh = rect.z;
                    int jx = (int)(r / 7 % 3) - 1;
                    int ax = x * S + S / 2 + jx;
                    int ay = y * S + Pad - Offsets[Level[c]] + S - 1;
                    int x0 = ax - sw / 2, y0 = ay - sh + 1;

                    // Тень: 2 строки (3 у высоких) от низа спрайта, на 1 px правее, только по верху земли.
                    int rows = sh >= 10 ? 3 : 2;
                    for (int j = 0; j < rows; j++)
                    for (int i = 0; i <= sw; i++)
                    {
                        int bx = x0 + i + 1, by = ay + j;
                        if (bx < 0 || bx >= BW || by < 0 || by >= BH) continue;
                        int t = (BH - 1 - by) * BW + bx;
                        var px = Color[t];
                        if (px.a != KindTop) continue;
                        Color[t] = new Color32((byte)(px.r * 72 / 100), (byte)(px.g * 72 / 100), (byte)(px.b * 72 / 100), px.a);
                    }

                    // Спрайт: пиксель пишется, только если земля под ним не южнее самого дерева.
                    for (int j = 0; j < sh; j++)
                    for (int i = 0; i < sw; i++)
                    {
                        var sc = Pixels[rect.x + j * sw + i];
                        if (sc.a < 128) continue;
                        int bx = x0 + i, by = y0 + j;
                        if (bx < 0 || bx >= BW || by < 0 || by >= BH) continue;
                        int t = (BH - 1 - by) * BW + bx;
                        int cy = CellId[t * 2 + 1];
                        if (cy == NoCell || cy > y) continue;
                        Color[t] = new Color32(sc.r, sc.g, sc.b, KindDecor);
                    }
                }
                Count[0] = count;
            }
        }

        // ---------- Мип 1 для зума ½ ----------

        [BurstCompile(CompileSynchronously = true)]
        struct Mip1Job : IJobParallelFor
        {
            public int BW, BH;
            [ReadOnly] public NativeArray<Color32> Src;
            [NativeDisableParallelForRestriction, WriteOnly] public NativeArray<Color32> Dst;

            public void Execute(int row)
            {
                int dw = BW / 2;
                for (int x = 0; x < dw; x++)
                {
                    int i0 = (row * 2) * BW + x * 2, i1 = i0 + BW;
                    Color32 a = Src[i0], b = Src[i0 + 1], c = Src[i1], d = Src[i1 + 1];
                    Dst[row * dw + x] = new Color32(
                        (byte)((a.r + b.r + c.r + d.r + 2) / 4),
                        (byte)((a.g + b.g + c.g + d.g + 2) / 4),
                        (byte)((a.b + b.b + c.b + d.b + 2) / 4),
                        a.a);
                }
            }
        }
    }
}
