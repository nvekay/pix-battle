using System.Collections.Generic;

namespace BattlePix.Core
{
    /// <summary>
    /// Генерация континента и столиц (спека 5.1). Один раз в конструкторе Simulation, поток MapGen.
    /// Работает на managed-массивах: это одноразовый проход до тика 0, результат копируется в World.
    /// </summary>
    internal static partial class MapGenerator
    {
        /// <summary>Результат генерации без World: удобно для тестов и раннера.</summary>
        internal sealed class Result
        {
            public byte[] Terrain;
            public int[] Capitals;
            public int LandCount;
            public MapGenStats Stats;
        }

        /// <summary>
        /// Карта и столицы. Карта перегенерируется (поток MapGen продолжается), если континент не попал в окно суши
        /// или если за capitalRetries расстановок старт так и не стал честным: на некоторых картах
        /// узкий полуостров делает честный старт невозможным при любой расстановке. Всего не больше mapRetries карт.
        /// Если честной карты нет, берётся лучшая по честности среди карт с сушей в окне (иначе — с сушей ближе к цели).
        /// </summary>
        internal static Result Run(int w, int h, int nationCount, MapGenRules mg, ref Pcg32 rng)
        {
            int n = w * h;
            var heights = new long[n];
            var land = new byte[n];
            var queue = new int[n];
            var label = new int[n];
            var owner = new int[n];
            var sizes = new int[nationCount];
            var bin = new int[n];

            Result best = null;
            bool bestLandOk = false;
            int mapAttempts = 0, capAttemptsTotal = 0;
            for (int attempt = 0; attempt < mg.MapRetries; attempt++)
            {
                mapAttempts++;
                Noise(w, h, mg, ref rng, heights);
                EdgeFalloff(w, h, mg, heights);
                int count = ThresholdContinent(w, h, mg, heights, bin, land, queue, label, out bool landOk);
                Relief(mg, bin, land, count);
                if (!landOk)
                {
                    long target = (long)mg.LandShare_bp * n / 10000;
                    if (!bestLandOk && (best == null || System.Math.Abs(count - target) < System.Math.Abs(best.LandCount - target)))
                        best = new Result { Terrain = (byte[])land.Clone(), LandCount = count };
                    continue;
                }

                var coast = CoastDistance(w, h, land, queue);
                var candidates = Candidates(n, land, coast, mg.CapitalCoastDist, nationCount);
                int[] caps = null;
                int ratio = int.MaxValue;
                for (int c = 0; c < mg.CapitalRetries; c++)
                {
                    capAttemptsTotal++;
                    var tryCaps = PlaceCapitals(w, nationCount, candidates, ref rng);
                    int r = Fairness(w, h, land, tryCaps, owner, queue, sizes);
                    if (r < ratio)
                    {
                        ratio = r;
                        caps = tryCaps;
                    }
                    if (r <= mg.StartFairnessMax_bp) break;
                }
                if (!bestLandOk || ratio < best.Stats.Fairness_bp)
                {
                    best = new Result
                    {
                        Terrain = (byte[])land.Clone(),
                        Capitals = caps,
                        LandCount = count,
                        Stats = new MapGenStats { Fairness_bp = ratio },
                    };
                    bestLandOk = true;
                }
                if (ratio <= mg.StartFairnessMax_bp) break;
            }

            if (best.Capitals == null)
            {
                // Ни одна карта не набрала суши: столицы на лучшей из них.
                var coast = CoastDistance(w, h, best.Terrain, queue);
                var candidates = Candidates(n, best.Terrain, coast, mg.CapitalCoastDist, nationCount);
                best.Capitals = PlaceCapitals(w, nationCount, candidates, ref rng);
                capAttemptsTotal++;
                best.Stats = new MapGenStats { Fairness_bp = Fairness(w, h, best.Terrain, best.Capitals, owner, queue, sizes) };
            }

            // 6½. Какая нация какую столицу получает — перестановка из потока MapGen (Фишер — Йетс).
            // Иначе id совпадает с порядком расстановки: нация 1 — случайная, часто внутренняя, последние — втиснутые между.
            for (int k = best.Capitals.Length - 1; k > 0; k--)
            {
                int j = rng.NextInt(k + 1);
                int t = best.Capitals[k];
                best.Capitals[k] = best.Capitals[j];
                best.Capitals[j] = t;
            }

            var stats = best.Stats;
            stats.MapAttempts = mapAttempts;
            stats.CapitalAttempts = capAttemptsTotal;
            stats.MapRetryFail = !bestLandOk;
            stats.UnfairStart = stats.Fairness_bp > mg.StartFairnessMax_bp;
            stats.LandShare_bp = (int)((long)best.LandCount * 10000L / n);
            best.Stats = stats;
            return best;
        }

        // ---------- 1. Шум ----------

        /// <summary>smoothstep в фиксированной точке: t в 0..1024 → s в 0..1024.</summary>
        internal static long Smooth(long t) => t * t * (3072 - 2 * t) / 1048576;

        static void Noise(int w, int h, MapGenRules mg, ref Pcg32 rng, long[] heights)
        {
            System.Array.Clear(heights, 0, heights.Length);
            long amp = 65536;
            for (int o = 0; o < mg.Octaves; o++)
            {
                int cell = mg.BaseCellPx >> o;
                int gw = (w + cell - 1) / cell + 1;
                int gh = (h + cell - 1) / cell + 1;
                var nodes = new int[gw * gh];
                for (int i = 0; i < nodes.Length; i++) nodes[i] = (int)(rng.Next() & 0xFFFF); // построчно

                // smoothstep по координате внутри клетки одинаков для всех клеток — считаем заранее.
                var s = new long[cell];
                for (int f = 0; f < cell; f++) s[f] = Smooth((long)f * 1024 / cell);

                for (int y = 0; y < h; y++)
                {
                    int cy = y / cell;
                    long sy = s[y % cell];
                    int row0 = cy * gw, row1 = (cy + 1) * gw;
                    for (int x = 0; x < w; x++)
                    {
                        int cx = x / cell;
                        long sx = s[x % cell];
                        long v00 = nodes[row0 + cx], v10 = nodes[row0 + cx + 1];
                        long v01 = nodes[row1 + cx], v11 = nodes[row1 + cx + 1];
                        long top = v00 * 1024 + (v10 - v00) * sx;      // ×1024
                        long bot = v01 * 1024 + (v11 - v01) * sx;      // ×1024
                        long val = (top * 1024 + (bot - top) * sy) / 1048576; // 0..65535
                        heights[y * w + x] += val * amp / 65536;
                    }
                }
                amp = amp * mg.Persistence_bp / 10000;
            }
        }

        // ---------- 2. Спад к краям ----------

        static void EdgeFalloff(int w, int h, MapGenRules mg, long[] heights)
        {
            if (mg.EdgeFalloffPx <= 0 || mg.EdgePenaltyPerPx <= 0) return;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int d = x;
                if (y < d) d = y;
                if (w - 1 - x < d) d = w - 1 - x;
                if (h - 1 - y < d) d = h - 1 - y;
                if (d < mg.EdgeFalloffPx) heights[y * w + x] -= (long)(mg.EdgeFalloffPx - d) * mg.EdgePenaltyPerPx;
            }
        }

        // ---------- 3–4. Порог и один континент ----------

        const int Bins = 1024;

        /// <summary>
        /// Высоты нормируются в корзины 0..1023. Порог — корзина: выше неё суша. Остаётся только самый большой
        /// компонент суши, и порог выбирается так, чтобы уже сам континент (без островов) попал в окно
        /// [minLandShare, maxLandShare] как можно ближе к landShare. Размер континента для всех порогов сразу
        /// считается одним проходом объединения (union-find) от высоких корзин к низким.
        /// Возвращает размер континента; в land остаётся только он. inWindow — попал ли континент в окно.
        /// </summary>
        static int ThresholdContinent(int w, int h, MapGenRules mg, long[] heights, int[] bin, byte[] land, int[] queue, int[] label,
            out bool inWindow)
        {
            int n = w * h;
            long min = long.MaxValue, max = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (heights[i] < min) min = heights[i];
                if (heights[i] > max) max = heights[i];
            }
            long range = max - min;
            var start = new int[Bins + 1];
            for (int i = 0; i < n; i++)
            {
                int b = range > 0 ? (int)((heights[i] - min) * (Bins - 1) / range) : 0;
                bin[i] = b;
                start[b + 1]++;
            }
            for (int b = 0; b < Bins; b++) start[b + 1] += start[b];
            // Пиксели по корзинам, внутри корзины по возрастанию индекса (сортировка подсчётом).
            var order = queue;
            var fill = (int[])start.Clone();
            for (int i = 0; i < n; i++) order[fill[bin[i]]++] = i;

            // contAt[b] — размер самого большого компонента при пороге b.
            var contAt = new int[Bins];
            var parent = label;
            for (int i = 0; i < n; i++) parent[i] = -1;
            var size = new int[n];
            int largest = 0;
            for (int b = Bins - 1; b >= 0; b--)
            {
                for (int k = start[b]; k < start[b + 1]; k++)
                {
                    int p = order[k];
                    parent[p] = p;
                    size[p] = 1;
                    int x = p % w, y = p / w;
                    if (y > 0) Union(p, p - w);
                    if (x < w - 1) Union(p, p + 1);
                    if (y < h - 1) Union(p, p + w);
                    if (x > 0) Union(p, p - 1);
                    int r = Find(p);
                    if (size[r] > largest) largest = size[r];
                }
                contAt[b] = largest;
            }

            long target = (long)mg.LandShare_bp * n / 10000;
            long lowBound = (long)mg.MinLandShare_bp * n / 10000;
            long highBound = (long)mg.MaxLandShare_bp * n / 10000;
            int bTarget = HighestAtLeast(contAt, target);
            int chosen = bTarget;
            if (contAt[bTarget] > highBound)
            {
                // Слияние с большим островом перепрыгнуло окно: пробуем порог повыше, с континентом ≥ min.
                int bLow = HighestAtLeast(contAt, lowBound);
                if (contAt[bLow] <= highBound) chosen = bLow;
            }
            inWindow = contAt[chosen] >= lowBound && contAt[chosen] <= highBound;
            return Continent(w, h, bin, chosen, land, queue, label);

            int Find(int v)
            {
                while (parent[v] != v)
                {
                    parent[v] = parent[parent[v]];
                    v = parent[v];
                }
                return v;
            }

            void Union(int p, int q)
            {
                if (parent[q] < 0) return;
                int a = Find(p), c = Find(q);
                if (a == c) return;
                if (size[a] < size[c] || (size[a] == size[c] && c < a)) { int t = a; a = c; c = t; }
                parent[c] = a;
                size[a] += size[c];
            }
        }

        /// <summary>Наибольший порог, при котором континент не меньше need. contAt не убывает при снижении порога.</summary>
        static int HighestAtLeast(int[] contAt, long need)
        {
            for (int b = Bins - 1; b > 0; b--)
                if (contAt[b] >= need) return b;
            return 0;
        }

        static int Continent(int w, int h, int[] bin, int threshold, byte[] land, int[] queue, int[] label)
        {
            for (int i = 0; i < land.Length; i++) land[i] = bin[i] >= threshold ? (byte)Core.Terrain.Land : (byte)Core.Terrain.Water;
            return KeepLargestComponent(w, h, land, queue, label);
        }

        // ---------- 4. Один континент ----------

        /// <summary>Оставляет самый большой компонент суши по 4-соседству. Возвращает его размер.</summary>
        internal static int KeepLargestComponent(int w, int h, byte[] land, int[] queue, int[] label)
        {
            int n = w * h;
            System.Array.Clear(label, 0, n);
            int next = 0, bestLabel = 0, bestSize = 0;
            for (int start = 0; start < n; start++)
            {
                if (land[start] == 0 || label[start] != 0) continue;
                next++;
                int size = Flood(w, h, land, label, queue, start, next);
                if (size > bestSize)
                {
                    bestSize = size;
                    bestLabel = next;
                }
            }
            for (int i = 0; i < n; i++)
                if (land[i] != 0 && label[i] != bestLabel) land[i] = 0;
            return bestSize;
        }

        static int Flood(int w, int h, byte[] land, int[] label, int[] queue, int start, int id)
        {
            int head = 0, tail = 0;
            queue[tail++] = start;
            label[start] = id;
            while (head < tail)
            {
                int p = queue[head++];
                int x = p % w, y = p / w;
                if (y > 0) Visit(p - w);
                if (x < w - 1) Visit(p + 1);
                if (y < h - 1) Visit(p + w);
                if (x > 0) Visit(p - 1);
            }
            return tail;

            void Visit(int q)
            {
                if (land[q] == 0 || label[q] != 0) return;
                label[q] = id;
                queue[tail++] = q;
            }
        }

        // ---------- 3½. Рельеф суши ----------

        /// <summary>
        /// Полосы рельефа по той же карте высот (спека 01b §5.7), без вызовов генератора: гистограмма корзин только
        /// по суше континента. Верхние mountainsShare суши — горы, следующие hillsShare — холмы, остальное — равнина.
        /// Порог — наибольшая корзина, выше которой набралось не меньше цели, как у порога суши (HighestAtLeast).
        /// </summary>
        internal static void Relief(MapGenRules mg, int[] bin, byte[] land, int landCount)
        {
            var hist = new int[Bins];
            for (int i = 0; i < land.Length; i++)
                if (land[i] != (byte)Core.Terrain.Water) hist[bin[i]]++;
            int bMountains = Threshold((long)landCount * mg.MountainsShare_bp / 10000);
            int bHills = Threshold((long)landCount * (mg.MountainsShare_bp + mg.HillsShare_bp) / 10000);
            for (int i = 0; i < land.Length; i++)
            {
                if (land[i] == (byte)Core.Terrain.Water) continue;
                int b = bin[i];
                land[i] = b >= bMountains ? (byte)Core.Terrain.Mountains
                    : b >= bHills ? (byte)Core.Terrain.Hills
                    : (byte)Core.Terrain.Land;
            }

            // Наибольшая корзина b, при которой суши в корзинах ≥ b не меньше need; need = 0 — полосы нет (Bins).
            int Threshold(long need)
            {
                if (need <= 0) return Bins;
                long cum = 0;
                for (int b = Bins - 1; b >= 0; b--)
                {
                    cum += hist[b];
                    if (cum >= need) return b;
                }
                return 0;
            }
        }

        // ---------- 6. Столицы ----------

        /// <summary>
        /// Расстояние до воды по Чебышёву (8-соседство), край карты считается водой.
        /// Чебышёв ≥ k гарантирует, что и евклидово расстояние до воды ≥ k.
        /// </summary>
        internal static int[] CoastDistance(int w, int h, byte[] terrain, int[] queue)
        {
            int n = w * h;
            var dist = new int[n];
            int head = 0, tail = 0;
            for (int i = 0; i < n; i++)
            {
                if (terrain[i] == 0)
                {
                    dist[i] = 0;
                    queue[tail++] = i;
                }
                else dist[i] = int.MaxValue;
            }
            while (head < tail)
            {
                int p = queue[head++];
                int x = p % w, y = p / w, nd = dist[p] + 1;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int qx = x + dx, qy = y + dy;
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h) continue;
                    int q = qy * w + qx;
                    if (dist[q] <= nd) continue;
                    dist[q] = nd;
                    queue[tail++] = q;
                }
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int border = x;
                if (y < border) border = y;
                if (w - 1 - x < border) border = w - 1 - x;
                if (h - 1 - y < border) border = h - 1 - y;
                int i = y * w + x;
                if (dist[i] > border + 1) dist[i] = border + 1;
            }
            return dist;
        }

        /// <summary>
        /// Кандидаты в столицы по возрастанию индекса: суша не в горах. Если их меньше числа наций, порог берега снижается.
        /// </summary>
        static List<int> Candidates(int n, byte[] terrain, int[] coast, int coastDist, int nationCount)
        {
            var list = new List<int>();
            for (int k = coastDist; k >= 1 && list.Count < nationCount; k--)
            {
                list.Clear();
                for (int i = 0; i < n; i++)
                    if (CapitalTerrain(terrain[i]) && coast[i] >= k) list.Add(i);
            }
            if (list.Count < nationCount)
            {
                list.Clear();
                for (int i = 0; i < n; i++)
                    if (CapitalTerrain(terrain[i])) list.Add(i);
            }
            return list;
        }

        static bool CapitalTerrain(byte t) => t != (byte)Core.Terrain.Water && t != (byte)Core.Terrain.Mountains;

        const int CapitalSamples = 64;

        /// <summary>
        /// Первая столица — случайный кандидат. Каждая следующая — лучший из 64 случайных кандидатов
        /// по максимуму минимального квадрата расстояния до уже поставленных.
        /// </summary>
        static int[] PlaceCapitals(int w, int nationCount, List<int> cand, ref Pcg32 rng)
        {
            var caps = new int[nationCount];
            caps[0] = cand[rng.NextInt(cand.Count)];
            for (int k = 1; k < nationCount; k++)
            {
                int best = -1;
                long bestD = -1;
                // 64 сэмпла; если все совпали с уже поставленными столицами, сэмплируем дальше.
                for (int s = 0; s < CapitalSamples || (bestD <= 0 && s < 1000000); s++)
                {
                    int c = cand[rng.NextInt(cand.Count)];
                    long md = long.MaxValue;
                    int cx = c % w, cy = c / w;
                    for (int j = 0; j < k; j++)
                    {
                        long dx = cx - caps[j] % w, dy = cy - caps[j] / w;
                        long d2 = dx * dx + dy * dy;
                        if (d2 < md) md = d2;
                    }
                    if (md > bestD)
                    {
                        bestD = md;
                        best = c;
                    }
                }
                caps[k] = best;
            }
            return caps;
        }

        // ---------- 7. Честность стартов ----------

        /// <summary>Многоисточниковый BFS по суше от столиц (по id), max/min размеров ячеек в bp.</summary>
        internal static int Fairness(int w, int h, byte[] terrain, int[] caps, int[] owner, int[] queue, int[] sizes)
        {
            int n = w * h;
            System.Array.Clear(owner, 0, n);
            System.Array.Clear(sizes, 0, sizes.Length);
            int head = 0, tail = 0;
            for (int k = 0; k < caps.Length; k++)
            {
                owner[caps[k]] = k + 1;
                sizes[k]++;
                queue[tail++] = caps[k];
            }
            while (head < tail)
            {
                int p = queue[head++];
                int x = p % w, y = p / w, o = owner[p];
                if (y > 0) Visit(p - w);
                if (x < w - 1) Visit(p + 1);
                if (y < h - 1) Visit(p + w);
                if (x > 0) Visit(p - 1);

                void Visit(int q)
                {
                    if (terrain[q] == 0 || owner[q] != 0) return;
                    owner[q] = o;
                    sizes[o - 1]++;
                    queue[tail++] = q;
                }
            }
            int min = int.MaxValue, max = 0;
            foreach (var s in sizes)
            {
                if (s < min) min = s;
                if (s > max) max = s;
            }
            return min > 0 ? (int)((long)max * 10000L / min) : int.MaxValue;
        }
    }
}
