using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pynelith.Plugins;

namespace Pynelith.Island.Services.Plugins
{
    /// <summary>插件导入结果。</summary>
    public sealed class PluginImportResult
    {
        public PluginImportResult(bool success, string pluginId, string error)
        {
            Success = success;
            PluginId = pluginId;
            Error = error;
        }

        public bool Success { get; }

        public string PluginId { get; }

        public string Error { get; }

        public static PluginImportResult Ok(string pluginId) => new PluginImportResult(true, pluginId, string.Empty);

        public static PluginImportResult Fail(string error) => new PluginImportResult(false, string.Empty, error);
    }

    /// <summary>
    /// 插件导入：zip 包或文件夹 → 用户插件目录 <c>%AppData%\Pynelith\plugins\&lt;id&gt;\</c>。
    /// 只做文件落地与清单校验，安装完成后由宿主调用 Reload / LoadAll 使其生效。
    /// </summary>
    public static class PluginInstaller
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        /// <summary>导入 zip 包（要求包内（可多一层目录）存在 plugin.json）。</summary>
        public static PluginImportResult ImportZip(string zipPath, string userPluginRoot)
        {
            string temp = Path.Combine(
                Path.GetTempPath(),
                "pynelith-plugin-" + Guid.NewGuid().ToString("N"));

            try
            {
                ZipFile.ExtractToDirectory(zipPath, temp);
                string? root = FindPluginRoot(temp);
                if (root == null)
                {
                    return PluginImportResult.Fail("压缩包内没有 plugin.json");
                }

                return ImportDirectory(root, userPluginRoot);
            }
            catch (Exception ex)
            {
                return PluginImportResult.Fail("解压失败：" + ex.Message);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>导入一个已解压的插件目录。</summary>
        public static PluginImportResult ImportDirectory(string sourceDirectory, string userPluginRoot)
        {
            try
            {
                string manifestPath = Path.Combine(sourceDirectory, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    return PluginImportResult.Fail("目录内没有 plugin.json");
                }

                PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath), JsonOptions);
                if (manifest == null)
                {
                    return PluginImportResult.Fail("plugin.json 解析结果为空");
                }

                manifest.Validate();

                string target = Path.Combine(userPluginRoot, manifest.Id);
                Directory.CreateDirectory(userPluginRoot);

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                CopyDirectory(sourceDirectory, target);
                return PluginImportResult.Ok(manifest.Id);
            }
            catch (PluginException ex)
            {
                return PluginImportResult.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return PluginImportResult.Fail("导入失败：" + ex.Message);
            }
        }

        /// <summary>校验文件的 SHA-256（十六进制，大小写不敏感；空值直接通过）。</summary>
        public static bool VerifySha256(string filePath, string expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex))
            {
                return true;
            }

            try
            {
                using var sha = SHA256.Create();
                using FileStream stream = File.OpenRead(filePath);
                string actual = Convert.ToHexString(sha.ComputeHash(stream));
                return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>在解压目录里寻找包含 plugin.json 的根目录（最多向下两层）。</summary>
        private static string? FindPluginRoot(string directory)
        {
            if (File.Exists(Path.Combine(directory, "plugin.json")))
            {
                return directory;
            }

            foreach (string child in Directory.GetDirectories(directory))
            {
                if (File.Exists(Path.Combine(child, "plugin.json")))
                {
                    return child;
                }
            }

            return null;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // 临时目录清理失败无所谓，系统会回收
            }
        }
    }
}
