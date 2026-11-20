using Newtonsoft.Json;

namespace BattlePix.Core
{
    /// <summary>DTO для sim_config.json. Только целые поля.</summary>
    public sealed class SimConfig
    {
        public const int SupportedFormat = 1;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("width")] public int Width = 512;
        [JsonProperty("height")] public int Height = 256;
        [JsonProperty("ticksPerSecond")] public int TicksPerSecond = 10;
        [JsonProperty("maxTicksPerFrame")] public int MaxTicksPerFrame = 8;
        [JsonProperty("checkpointInterval")] public int CheckpointInterval = 600;
        [JsonProperty("commandHorizonTicks")] public int CommandHorizonTicks = 100;
        [JsonProperty("debugPaint")] public DebugPaintConfig DebugPaint = new DebugPaintConfig();

        /// <summary>Число наций в мире Фундамента (без правил). В партии с правилами его задаёт nations.json.</summary>
        [JsonIgnore] public int NationCount => DebugPaint.Nations;

        public static SimConfig FromJson(string json)
        {
            var settings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Error,
            };
            var cfg = JsonConvert.DeserializeObject<SimConfig>(json, settings)
                      ?? throw new System.FormatException("sim_config: empty");
            cfg.Validate();
            return cfg;
        }

        public void Validate()
        {
            Require(Format == SupportedFormat, $"format {Format} is not supported, expected {SupportedFormat}");
            Require(Width > 0 && Height > 0, "width and height must be > 0");
            Require((long)Width * Height <= int.MaxValue, "map is too large");
            Require(TicksPerSecond > 0, "ticksPerSecond must be > 0");
            Require(MaxTicksPerFrame > 0, "maxTicksPerFrame must be > 0");
            Require(CheckpointInterval > 0, "checkpointInterval must be > 0");
            Require(CommandHorizonTicks >= 1, "commandHorizonTicks must be >= 1");
            Require(DebugPaint != null, "debugPaint is required");
            Require(DebugPaint.Nations >= 1 && DebugPaint.Nations <= 255, "debugPaint.nations must be in 1..255");
            Require(DebugPaint.PixelsPerTick >= 0, "debugPaint.pixelsPerTick must be >= 0");
        }

        static void Require(bool ok, string message)
        {
            if (!ok) throw new System.FormatException("sim_config: " + message);
        }

        public SimConfig Clone()
        {
            var c = (SimConfig)MemberwiseClone();
            c.DebugPaint = new DebugPaintConfig
            {
                Enabled = DebugPaint.Enabled,
                PixelsPerTick = DebugPaint.PixelsPerTick,
                Nations = DebugPaint.Nations,
            };
            return c;
        }
    }

    public sealed class DebugPaintConfig
    {
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("pixelsPerTick")] public int PixelsPerTick = 64;
        [JsonProperty("nations")] public int Nations = 8;
    }
}
