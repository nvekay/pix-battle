using System.Collections.Generic;
using BattlePix.Core;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>
    /// Запечённая 2.5D-карта (спека 02a §5.3–5.6): один квад, шейдер MapBaked. Рельеф, вода и декор печёт TerrainBaker
    /// при старте партии; в кадре меняются только владельцы и исконные владельцы (R8 512×256), вспышки фронта, статусы пар
    /// и районы претензий.
    /// 1 юнит мира = 1 пиксель запечённой текстуры, левый нижний угол карты в (0, 0). Только читает мир между тиками.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public sealed class MapView : MonoBehaviour
    {
        [SerializeField] DecorAtlas decorAtlas;

        static readonly int TerrainColorId = Shader.PropertyToID("_TerrainColor");
        static readonly int CellIdTexId = Shader.PropertyToID("_CellId");
        static readonly int OwnerTexId = Shader.PropertyToID("_OwnerTex");
        static readonly int TerrainTexId = Shader.PropertyToID("_TerrainTex");
        static readonly int PaletteTexId = Shader.PropertyToID("_PaletteTex");
        static readonly int FrontFxTexId = Shader.PropertyToID("_FrontFxTex");
        static readonly int PairStatusId = Shader.PropertyToID("_PairStatus");
        static readonly int BakeSizeId = Shader.PropertyToID("_BakeSize");
        static readonly int MapSizeId = Shader.PropertyToID("_MapSize");
        static readonly int HumanId = Shader.PropertyToID("_HumanId");
        static readonly int FocusId = Shader.PropertyToID("_FocusNation");
        static readonly int ZoomIndexId = Shader.PropertyToID("_ZoomIndex");
        static readonly int TintId = Shader.PropertyToID("_Tint");
        static readonly int KindMultId = Shader.PropertyToID("_KindMult");
        static readonly int LinePxId = Shader.PropertyToID("_LinePx");
        static readonly int FringePxId = Shader.PropertyToID("_FringePx");
        static readonly int OtherFringeLightId = Shader.PropertyToID("_OtherFringeLight");
        static readonly int PeaceAlphaId = Shader.PropertyToID("_PeaceAlpha");
        static readonly int TruceDashId = Shader.PropertyToID("_TruceDash");
        static readonly int GainLightId = Shader.PropertyToID("_GainLight");
        static readonly int LossDarkId = Shader.PropertyToID("_LossDark");
        static readonly int SkyId = Shader.PropertyToID("_SkyColor");
        static readonly int LineColorId = Shader.PropertyToID("_LineColor");
        static readonly int OwnFringeId = Shader.PropertyToID("_OwnFringe");
        static readonly int PeaceLineId = Shader.PropertyToID("_PeaceLine");
        static readonly int CoreTexId = Shader.PropertyToID("_CoreTex");
        static readonly int HatchStepId = Shader.PropertyToID("_HatchStep");
        static readonly int HatchAlphaId = Shader.PropertyToID("_HatchAlpha");
        static readonly int ClaimsId = Shader.PropertyToID("_Claims");
        static readonly int ClaimCountId = Shader.PropertyToID("_ClaimCount");
        static readonly int ClaimPreviewId = Shader.PropertyToID("_ClaimPreview");
        static readonly int PreviewOkId = Shader.PropertyToID("_PreviewOk");
        static readonly int PreviewBadId = Shader.PropertyToID("_PreviewBad");
        const int MaxClaims = 16;
        readonly Vector4[] _claims = new Vector4[MaxClaims];

        Texture2D _terrainColor, _cellIdTex, _ownerTex, _terrainTex, _fxTex, _paletteTex, _pairTex, _coreTex;
        MeshRenderer _renderer;
        Material _material;
        PaletteData _pal;
        TerrainBaker.DecorSprites _decor;

        /// <summary>CPU-копия CellId для пикинга (спека 02a §5.3): 2 ushort на пиксель, порядок Texture2D (строка 0 — низ).</summary>
        ushort[] _cellId;
        byte[] _level;
        int[] _offsets;

        // Вспышки фронта человека: что стало у клетки за последние fx.seconds.
        byte[] _prevOwner;
        float[] _fxUntil;
        bool[] _fxGain;
        readonly List<int> _fxActive = new List<int>();
        bool _fxDirty;
        float _fxSeconds = 1f;
        byte _human;
        int _zoomIndex = 1;

        /// <summary>Размер карты в клетках.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Размер запечённой текстуры в пикселях = размер карты в юнитах мира.</summary>
        public int BakeWidth { get; private set; }
        public int BakeHeight { get; private set; }
        public int CellPx { get; private set; }
        public int Pad { get; private set; }
        public double LastBakeMs { get; private set; }
        public DecorAtlas Atlas => decorAtlas;

        public void Init(int width, int height, Color32[] palette, PaletteData pal)
        {
            Release();
            Width = width;
            Height = height;
            _pal = pal;
            CellPx = pal.CellPx;
            Pad = pal.MaxOffset;
            _fxSeconds = Mathf.Max(0.05f, pal.Fx.Seconds);
            _ownerTex = NewR8("OwnerTex", width, height);
            _terrainTex = NewR8("TerrainTex", width, height);
            _fxTex = NewR8("FrontFxTex", width, height);
            _pairTex = NewR8("PairStatus", 16, 16);
            _coreTex = NewR8("CoreTex", width, height);
            _paletteTex = new Texture2D(256, 1, TextureFormat.RGBA32, false, false)
            {
                name = "PaletteTex",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _paletteTex.SetPixels32(palette);
            _paletteTex.Apply(false);

            _renderer = GetComponent<MeshRenderer>();
            _material = _renderer.material; // экземпляр, чтобы не писать в ассет
            _material.SetTexture(OwnerTexId, _ownerTex);
            _material.SetTexture(TerrainTexId, _terrainTex);
            _material.SetTexture(PaletteTexId, _paletteTex);
            _material.SetTexture(FrontFxTexId, _fxTex);
            _material.SetTexture(PairStatusId, _pairTex);
            _material.SetTexture(CoreTexId, _coreTex);
            _material.SetFloat(HatchStepId, pal.War.HatchStep);
            _material.SetFloat(HatchAlphaId, pal.War.HatchAlpha_bp / 10000f);
            _material.SetVector(PreviewOkId, Srgb(pal.War.ClaimOk));
            _material.SetVector(PreviewBadId, Srgb(pal.War.ClaimBad));
            _material.SetVectorArray(ClaimsId, _claims);
            _material.SetFloat(ClaimCountId, 0);
            _material.SetVector(ClaimPreviewId, Vector4.zero);
            _material.SetVector(MapSizeId, new Vector4(width, height, 0, 0));
            _material.SetVector(KindMultId, new Vector4(pal.TintKindMult_bp.Face, pal.TintKindMult_bp.Wall, pal.TintKindMult_bp.Decor, 0) / 10000f);
            _material.SetFloat(LinePxId, pal.Border.LinePx);
            _material.SetFloat(OtherFringeLightId, pal.Border.OtherFringeLight_bp / 10000f);
            _material.SetFloat(PeaceAlphaId, pal.Border.PeaceAlpha_bp / 10000f);
            _material.SetFloat(TruceDashId, pal.Border.TruceDash);
            _material.SetFloat(GainLightId, pal.Fx.GainLight_bp / 10000f);
            _material.SetFloat(LossDarkId, pal.Fx.LossDark_bp / 10000f);
            // Цвета в шейдере считаются в sRGB: передаём как вектор, без перевода в линейное.
            _material.SetVector(SkyId, Srgb(pal.Sky));
            _material.SetVector(LineColorId, Srgb(pal.Border.Line));
            _material.SetVector(OwnFringeId, Srgb(pal.Border.OwnFringe));
            _material.SetVector(PeaceLineId, Srgb(pal.Border.PeaceLine));
            SetZoomIndex(1);

            if (decorAtlas == null) Debug.LogError("[BattlePix] MapView: decor atlas is not set (BattlePix → Rebuild Main Scene)");
            else _decor = TerrainBaker.DecorSprites.FromAtlas(decorAtlas);
        }

        static Vector4 Srgb(string hex)
        {
            var c = PaletteData.Hex(hex);
            return new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        static Texture2D NewR8(string name, int w, int h) => new Texture2D(w, h, TextureFormat.R8, false, true)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        /// <summary>Новая партия: запекание рельефа (раз за партию), сброс вспышек.</summary>
        public void BeginMatch(World world, byte human, ulong seed)
        {
            _human = human;
            _material.SetFloat(HumanId, human);

            using (var bake = TerrainBaker.Bake(world, _pal, _decor, (uint)(seed & 0xFFFFFFFFUL)))
            {
                LastBakeMs = bake.Ms;
                Debug.Log($"[BattlePix] bake ms {bake.Ms:0.0} ({bake.Width}×{bake.Height}, decor {bake.DecorCount})");
                if (_terrainColor == null || _terrainColor.width != bake.Width || _terrainColor.height != bake.Height)
                {
                    if (_terrainColor != null) Destroy(_terrainColor);
                    if (_cellIdTex != null) Destroy(_cellIdTex);
                    _terrainColor = new Texture2D(bake.Width, bake.Height, TextureFormat.RGBA32, 2, false)
                    {
                        name = "TerrainColor", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    };
                    _cellIdTex = new Texture2D(bake.Width, bake.Height, TextureFormat.RG32, false, true)
                    {
                        name = "CellId", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    };
                }
                _terrainColor.SetPixelData(bake.Color, 0);
                _terrainColor.SetPixelData(bake.ColorMip1, 1);
                _terrainColor.Apply(false, false);
                _cellIdTex.SetPixelData(bake.CellId, 0);
                _cellIdTex.Apply(false, false);
                _cellId = bake.CellId.ToArray();
                _level = bake.Level.ToArray();
                _offsets = bake.Offsets.ToArray();
                BakeWidth = bake.Width;
                BakeHeight = bake.Height;
            }
            _material.SetTexture(TerrainColorId, _terrainColor);
            _material.SetTexture(CellIdTexId, _cellIdTex);
            _material.SetVector(BakeSizeId, new Vector4(BakeWidth, BakeHeight, 0, 0));
            transform.localPosition = new Vector3(BakeWidth * 0.5f, BakeHeight * 0.5f, 0f);
            transform.localScale = new Vector3(BakeWidth, BakeHeight, 1f);

            var t = _terrainTex.GetPixelData<byte>(0);
            world.Terrain.CopyTo(t);
            _terrainTex.Apply(false);

            _prevOwner = world.Owner.ToArray();
            _fxUntil = new float[world.PixelCount];
            _fxGain = new bool[world.PixelCount];
            _fxActive.Clear();
            var fx = _fxTex.GetPixelData<byte>(0);
            for (int i = 0; i < fx.Length; i++) fx[i] = 0;
            _fxTex.Apply(false);
            UpdatePairs(world);
            world.MarkAllDirty();
            UploadIfDirty(world, Time.unscaledTime);
        }

        /// <summary>Если мир помечен изменённым, копирует Owner в текстуру и отмечает вспышки фронта человека.</summary>
        public void UploadIfDirty(World world, float now)
        {
            if (_ownerTex == null || _prevOwner == null) return;
            if (world.DirtyRowMin >= 0)
            {
                var owner = world.Owner;
                int w = world.Width;
                for (int i = world.DirtyRowMin * w, end = (world.DirtyRowMax + 1) * w; i < end; i++)
                {
                    byte o = owner[i];
                    byte was = _prevOwner[i];
                    if (o == was) continue;
                    _prevOwner[i] = o;
                    if (o != _human && was != _human) continue;
                    if (_fxUntil[i] <= now) _fxActive.Add(i);
                    _fxUntil[i] = now + _fxSeconds;
                    _fxGain[i] = o == _human;
                }
                var dst = _ownerTex.GetPixelData<byte>(0);
                owner.CopyTo(dst);
                _ownerTex.Apply(false);
                // Core меняется вместе с Owner (расползание, мир, гибель нации): те же грязные строки.
                if (world.Core.Length == world.PixelCount)
                {
                    var core = _coreTex.GetPixelData<byte>(0);
                    world.Core.CopyTo(core);
                    _coreTex.Apply(false);
                }
                world.ClearDirtyRows();
            }
            UpdateFx(now);
        }

        void UpdateFx(float now)
        {
            if (_fxActive.Count == 0 && !_fxDirty) return;
            var fx = _fxTex.GetPixelData<byte>(0);
            for (int k = _fxActive.Count - 1; k >= 0; k--)
            {
                int i = _fxActive[k];
                float left = _fxUntil[i] - now;
                if (left <= 0)
                {
                    fx[i] = 0;
                    _fxActive[k] = _fxActive[_fxActive.Count - 1];
                    _fxActive.RemoveAt(_fxActive.Count - 1);
                    continue;
                }
                int v = Mathf.Clamp(Mathf.CeilToInt(127 * left / _fxSeconds), 1, 127);
                fx[i] = (byte)(_fxGain[i] ? v : 128 + v);
            }
            _fxDirty = _fxActive.Count > 0;
            _fxTex.Apply(false);
        }

        /// <summary>
        /// Статусы пар в _PairStatus из ядра (02b §9): 0 мир — тонкая светлая линия, 1 война — тёмная с каймой,
        /// 2 перемирие — штрих. Войны между ИИ видны тем же стилем. Там же районы претензий: свои и на нас — всегда,
        /// между ИИ — только на зуме ½ и 1.
        /// </summary>
        public void UpdatePairs(World world)
        {
            if (_pairTex == null) return;
            var px = _pairTex.GetPixelData<byte>(0);
            int n = world.NationCount;
            for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++)
            {
                byte st = 0;
                if (a != b && a >= 1 && b >= 1 && a <= n && b <= n && world.Nation((byte)a).Alive != 0 && world.Nation((byte)b).Alive != 0)
                    st = world.WarOf(a, b).Status;
                px[b * 16 + a] = st;
            }
            _pairTex.Apply(false);

            int count = 0, w = world.Width;
            for (int a = 1; a <= n && count < MaxClaims; a++)
            for (int b = a + 1; b <= n && count < MaxClaims; b++)
            {
                var war = world.WarOf(a, b);
                if (war.Status != WarStatus.War || war.Goal != WarGoals.Claims || war.ClaimCenter < 0) continue;
                bool ours = a == _human || b == _human;
                if (!ours && _zoomIndex > 1) continue;
                _claims[count++] = new Vector4(war.ClaimCenter % w, war.ClaimCenter / w, world.Rules.War.ClaimRadius, war.Attacker);
            }
            _material.SetVectorArray(ClaimsId, _claims);
            _material.SetFloat(ClaimCountId, count);
        }

        /// <summary>Превью района под курсором в режиме выбора: ok — можно (зелёный), иначе красный; center &lt; 0 — скрыть.</summary>
        public void SetClaimPreview(int x, int y, int radius, bool ok, bool show)
        {
            if (_material == null) return;
            _material.SetVector(ClaimPreviewId, show ? new Vector4(x, y, radius, ok ? 1 : 2) : Vector4.zero);
        }

        public void SetFocusNation(int nation)
        {
            if (_material != null) _material.SetFloat(FocusId, nation);
        }

        /// <summary>0 — ½ (вся карта), 1 — 1:1, 2 — ×2. Сила тонировки и ширина каймы зависят от зума.</summary>
        public void SetZoomIndex(int index)
        {
            _zoomIndex = Mathf.Clamp(index, 0, 2);
            if (_material == null || _pal == null) return;
            var tint = _zoomIndex == 0 ? _pal.Tint_bp.Half : _zoomIndex == 1 ? _pal.Tint_bp.One : _pal.Tint_bp.Two;
            int fringe = _zoomIndex == 0 ? _pal.Border.FringePx.Half : _zoomIndex == 1 ? _pal.Border.FringePx.One : _pal.Border.FringePx.Two;
            _material.SetFloat(ZoomIndexId, _zoomIndex);
            _material.SetVector(TintId, new Vector4(tint[0], tint[1], 0, 0) / 10000f);
            _material.SetFloat(FringePxId, fringe);
        }

        // ---------- координаты (спека 02a §5.6) ----------

        /// <summary>Центр верхней грани клетки в координатах мира. Им ставятся значки, подписи, выноски.</summary>
        public Vector3 CellToWorld(int x, int y)
        {
            int off = _level != null ? _offsets[_level[y * Width + x]] : 0;
            float by = y * CellPx + Pad - off + CellPx * 0.5f;
            return transform.position - new Vector3(BakeWidth * 0.5f, BakeHeight * 0.5f, 0)
                   + new Vector3(x * CellPx + CellPx * 0.5f, BakeHeight - by, 0f);
        }

        /// <summary>Точка мира → клетка под ней. false — небо или мимо карты. Вода — клетка без владельца.</summary>
        public bool WorldToCell(Vector3 world, out int x, out int y)
        {
            var origin = transform.position - new Vector3(BakeWidth * 0.5f, BakeHeight * 0.5f, 0);
            return PickCell(_cellId, BakeWidth, BakeHeight, world.x - origin.x, world.y - origin.y, out x, out y);
        }

        /// <summary>Экран (пиксели, y снизу) → клетка.</summary>
        public bool ScreenToCell(Camera cam, Vector2 screen, out int x, out int y)
        {
            var w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
            return WorldToCell(w, out x, out y);
        }

        /// <summary>
        /// Пикинг по CPU-копии CellId: (wx, wy) — в пикселях текстуры от левого нижнего угла.
        /// Строка текстуры tr = floor(wy) — та же, что в массиве (порядок Texture2D).
        /// </summary>
        public static bool PickCell(ushort[] cellId, int bw, int bh, float wx, float wy, out int x, out int y)
        {
            x = y = -1;
            if (cellId == null) return false;
            int bx = Mathf.FloorToInt(wx), tr = Mathf.FloorToInt(wy);
            if (bx < 0 || tr < 0 || bx >= bw || tr >= bh) return false;
            int i = tr * bw + bx;
            int cx = cellId[i * 2], cy = cellId[i * 2 + 1];
            if (cx == TerrainBaker.NoCell) return false;
            x = cx;
            y = cy;
            return true;
        }

        void Release()
        {
            foreach (var t in new[] { _terrainColor, _cellIdTex, _ownerTex, _terrainTex, _fxTex, _paletteTex, _pairTex, _coreTex })
                if (t != null) Destroy(t);
            if (_material != null) Destroy(_material);
            _terrainColor = _cellIdTex = _ownerTex = _terrainTex = _fxTex = _paletteTex = _pairTex = _coreTex = null;
            _material = null;
            _decor?.Dispose();
            _decor = null;
        }

        void OnDestroy() => Release();
    }
}
