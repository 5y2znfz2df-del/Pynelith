using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pynelith.Island.Configuration
{
    /// <summary>
    /// config.json 的读写。文件固定位于 <c>%AppData%\Pynelith\config.json</c>。
    /// 读取失败（首启动 / 文件损坏 / 无权限）时回退到默认配置，绝不让 UI 起不来。
    /// </summary>
    public static class ConfigStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            // 让中文应用名在文件里保持可读，而不是 \uXXXX
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>配置目录：%AppData%\Pynelith。</summary>
        public static string Directory
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pynelith");

        /// <summary>配置文件完整路径。</summary>
        public static string FilePath => Path.Combine(Directory, "config.json");

        /// <summary>读取配置；任何异常都返回默认配置。</summary>
        public static IslandConfig Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    var fresh = new IslandConfig();
                    ApplyScreenDefaults(fresh);
                    return fresh;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<IslandConfig>(json, Options) ?? new IslandConfig();
                Migrate(config);
                return config;
            }
            catch (Exception)
            {
                return new IslandConfig();
            }
        }

        /// <summary>
        /// 旧版默认值迁移到新规范：高 26→36、宽 200→180、圆角 26→24。
        /// 只在这些值仍是旧默认时才改，用户自己调过的数值保持不动。
        /// </summary>
        private static void Migrate(IslandConfig config)
        {
            if (Math.Abs(config.CollapsedHeight - 26) < 0.01)
            {
                config.CollapsedHeight = 36;
            }

            if (Math.Abs(config.ExpandedWidth - 200) < 0.01)
            {
                config.ExpandedWidth = 180;
            }

            if (Math.Abs(config.CornerRadius - 26) < 0.01)
            {
                config.CornerRadius = 24;
            }
        }

        /// <summary>
        /// 基准高度 = 主屏高度 × 0.033（1080p ≈ 36，1440p ≈ 48）。
        /// 只用于首启动默认值，之后由设置里的滑块接管。
        /// </summary>
        private static void ApplyScreenDefaults(IslandConfig config)
        {
            int screenHeight = GetSystemMetrics(1 /* SM_CYSCREEN */);
            if (screenHeight > 0)
            {
                config.CollapsedHeight = Math.Clamp(Math.Round(screenHeight * 0.033), 32, 52);
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        /// <summary>写入配置（先写临时文件再替换，避免半截文件）。</summary>
        public static void Save(IslandConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            string directory = Directory;
            System.IO.Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(config, Options);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, FilePath, true);
        }
    }
}
