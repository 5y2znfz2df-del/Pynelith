using System;
using System.IO;
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
                    return new IslandConfig();
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<IslandConfig>(json, Options) ?? new IslandConfig();
            }
            catch (Exception)
            {
                return new IslandConfig();
            }
        }

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
