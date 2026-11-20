using BattlePix.Core;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>
    /// Рисует карту одним квадом: текстура владельцев R8 + палитра 256×1 в шейдере OwnerPalette.
    /// Только читает мир и только между тиками.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public sealed class MapView : MonoBehaviour
    {
        static readonly int OwnerTexId = Shader.PropertyToID("_OwnerTex");
        static readonly int PaletteTexId = Shader.PropertyToID("_PaletteTex");

        Texture2D _ownerTex;
        Texture2D _paletteTex;
        MeshRenderer _renderer;
        Material _material;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public void Init(int width, int height, Color32[] palette)
        {
            Release();
            Width = width;
            Height = height;

            _ownerTex = new Texture2D(width, height, TextureFormat.R8, false, true)
            {
                name = "OwnerTex",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
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

            // Квад 1×1 растягиваем на карту: 1 пиксель карты = 1 юнит мира, центр в нуле.
            transform.localPosition = Vector3.zero;
            transform.localScale = new Vector3(width, height, 1f);
        }

        /// <summary>Если мир помечен изменённым, копирует Owner в текстуру и сбрасывает dirty-диапазон.</summary>
        public void UploadIfDirty(World world)
        {
            if (_ownerTex == null || world.DirtyRowMin < 0) return;
            var dst = _ownerTex.GetPixelData<byte>(0);
            world.Owner.CopyTo(dst);
            _ownerTex.Apply(false);
            world.ClearDirtyRows();
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
            if (_ownerTex != null) Destroy(_ownerTex);
            if (_paletteTex != null) Destroy(_paletteTex);
            if (_material != null) Destroy(_material);
            _ownerTex = null;
            _paletteTex = null;
            _material = null;
        }

        void OnDestroy() => Release();
    }
}
