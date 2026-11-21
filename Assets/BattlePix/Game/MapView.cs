using System.Collections.Generic;
using BattlePix.Core;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>
    /// Рисует карту одним квадом шейдером OwnerPalette: владельцы — R8-текстура + палитра 256×1,
    /// рельеф — вторая R8 (раз за партию), вспышки своего фронта — третья R8 (FrontFx).
    /// Значки столиц и крепостей — массив uniform. Только читает мир и только между тиками.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public sealed class MapView : MonoBehaviour
    {
        static readonly int OwnerTexId = Shader.PropertyToID("_OwnerTex");
        static readonly int PaletteTexId = Shader.PropertyToID("_PaletteTex");
        static readonly int TerrainTexId = Shader.PropertyToID("_TerrainTex");
        static readonly int FrontFxTexId = Shader.PropertyToID("_FrontFxTex");
        static readonly int MapSizeId = Shader.PropertyToID("_MapSize");
        static readonly int HumanId = Shader.PropertyToID("_HumanId");
        static readonly int StrikeMaskId = Shader.PropertyToID("_StrikeMask");
        static readonly int FocusId = Shader.PropertyToID("_FocusNation");
        static readonly int MarkersId = Shader.PropertyToID("_Markers");
        static readonly int MarkerCountId = Shader.PropertyToID("_MarkerCount");

        public const int MaxMarkers = 96;
        const float FxSeconds = 10f;

        Texture2D _ownerTex, _terrainTex, _fxTex, _paletteTex;
        MeshRenderer _renderer;
        Material _material;

        // Вспышки фронта человека: что стало у пикселя за последние 10 с.
        byte[] _prevOwner;
        float[] _fxUntil;
        bool[] _fxGain;
        readonly List<int> _fxActive = new List<int>();
        readonly Vector4[] _markers = new Vector4[MaxMarkers];
        byte _human;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Init(int width, int height, Color32[] palette)
        {
            Release();
            Width = width;
            Height = height;
            _ownerTex = NewR8("OwnerTex", width, height);
            _terrainTex = NewR8("TerrainTex", width, height);
            _fxTex = NewR8("FrontFxTex", width, height);
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
            _material.SetTexture(PaletteTexId, _paletteTex);
            _material.SetTexture(TerrainTexId, _terrainTex);
            _material.SetTexture(FrontFxTexId, _fxTex);
            _material.SetVector(MapSizeId, new Vector4(width, height, 0, 0));
            _material.SetVectorArray(MarkersId, _markers);

            // Квад 1×1 растягиваем на карту: 1 пиксель карты = 1 юнит мира, центр в нуле.
            transform.localPosition = Vector3.zero;
            transform.localScale = new Vector3(width, height, 1f);
        }

        static Texture2D NewR8(string name, int w, int h) => new Texture2D(w, h, TextureFormat.R8, false, true)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };

        /// <summary>Новая партия: рельеф (не меняется за партию), сброс вспышек.</summary>
        public void BeginMatch(World world, byte human)
        {
            _human = human;
            _material.SetFloat(HumanId, human);
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
            world.MarkAllDirty();
            UploadIfDirty(world, Time.unscaledTime);
        }

        /// <summary>Если мир помечен изменённым, копирует Owner в текстуру, отмечает вспышки фронта человека.</summary>
        public void UploadIfDirty(World world, float now)
        {
            if (_ownerTex == null) return;
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
                    _fxUntil[i] = now + FxSeconds;
                    _fxGain[i] = o == _human;
                }
                var dst = _ownerTex.GetPixelData<byte>(0);
                owner.CopyTo(dst);
                _ownerTex.Apply(false);
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
                int v = Mathf.Clamp(Mathf.CeilToInt(127 * left / FxSeconds), 1, 127);
                fx[i] = (byte)(_fxGain[i] ? v : 128 + v);
            }
            _fxDirty = _fxActive.Count > 0;
            _fxTex.Apply(false);
        }

        bool _fxDirty;

        public void SetStrikeMask(int mask) => _material.SetFloat(StrikeMaskId, mask);
        public void SetFocusNation(int nation) => _material.SetFloat(FocusId, nation);

        /// <summary>Значки: столицы живых наций. Значки штабов на фронтах — PR 4 переделки.</summary>
        public void SetMarkers(World world)
        {
            int n = 0, w = world.Width;
            for (int k = 0; k < world.NationCount && n < MaxMarkers; k++)
            {
                var s = world.Nations[k];
                if (s.Alive == 0 || s.Capital < 0) continue;
                _markers[n++] = new Vector4(s.Capital % w, s.Capital / w, 1, s.Id);
            }
            _material.SetVectorArray(MarkersId, _markers);
            _material.SetFloat(MarkerCountId, n);
        }

        /// <summary>Мировая точка → пиксель карты. false, если мимо карты.</summary>
        public bool WorldToPixel(Vector3 worldPos, out int x, out int y)
        {
            var local = worldPos - transform.position;
            x = Mathf.FloorToInt(local.x + Width * 0.5f);
            y = Mathf.FloorToInt(local.y + Height * 0.5f);
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        void Release()
        {
            foreach (var t in new[] { _ownerTex, _terrainTex, _fxTex, _paletteTex })
                if (t != null) Destroy(t);
            if (_material != null) Destroy(_material);
            _ownerTex = _terrainTex = _fxTex = _paletteTex = null;
            _material = null;
        }

        void OnDestroy() => Release();
    }
}
