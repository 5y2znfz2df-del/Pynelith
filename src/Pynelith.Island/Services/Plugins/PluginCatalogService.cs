using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Plugins;

namespace Pynelith.Island.Services.Plugins
{
    /// <summary>插件市场拉取结果。</summary>
    public sealed class PluginCatalogResult
    {
        public PluginCatalogResult(IReadOnlyList<PluginCatalogEntry> entries, string message)
        {
            Entries = entries;
            Message = message;
        }

        public IReadOnlyList<PluginCatalogEntry> Entries { get; }

        /// <summary>提示信息（例如"使用本地缓存"或错误原因），空表示成功直连。</summary>
        public string Message { get; }
    }

    /// <summary>
    /// 插件市场：从清单地址（plugin-index.json）拉取插件列表。
    /// 网络失败时退回本地缓存，保证离线也能看到上次的列表。
    /// </summary>
    public sealed class PluginCatalogService
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private readonly string _cachePath;
        private readonly string _downloadRoot;

        public PluginCatalogService()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pynelith");
            _cachePath = Path.Combine(directory, "plugin-index.json");
            _downloadRoot = Path.Combine(Path.GetTempPath(), "pynelith-plugin-cache");
        }

        /// <summary>拉取插件列表（带缓存兜底）。</summary>
        public async Task<PluginCatalogResult> GetCatalogAsync(string url, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    string json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    PluginCatalog? catalog = JsonSerializer.Deserialize<PluginCatalog>(json, JsonOptions);
                    if (catalog == null)
                    {
                        throw new PluginException("插件市场清单解析为空");
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                        File.WriteAllText(_cachePath, json);
                    }
                    catch (Exception)
                    {
                        // 缓存写失败不影响本次结果
                    }

                    return new PluginCatalogResult(catalog.Plugins, string.Empty);
                }
                catch (Exception ex)
                {
                    PluginCatalogResult? cached = ReadCache();
                    return cached != null
                        ? new PluginCatalogResult(cached.Entries, "网络不可用，已显示本地缓存（" + ex.Message + "）")
                        : new PluginCatalogResult(Array.Empty<PluginCatalogEntry>(), "拉取插件市场失败：" + ex.Message);
                }
            }

            return ReadCache() ?? new PluginCatalogResult(
                Array.Empty<PluginCatalogEntry>(), "未配置插件市场地址");
        }

        /// <summary>
        /// 下载插件包到临时目录并校验 SHA-256。校验失败返回 null。
        /// </summary>
        public async Task<string?> DownloadAsync(
            PluginCatalogEntry entry,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(_downloadRoot);
                string fileName = entry.Id + "-" + Guid.NewGuid().ToString("N") + ".zip";
                string path = Path.Combine(_downloadRoot, fileName);

                using HttpResponseMessage response = await Http
                    .GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (FileStream stream = File.Create(path))
                {
                    await response.Content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                if (!PluginInstaller.VerifySha256(path, entry.Sha256))
                {
                    TryDelete(path);
                    return null;
                }

                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private PluginCatalogResult? ReadCache()
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return null;
                }

                PluginCatalog? catalog = JsonSerializer.Deserialize<PluginCatalog>(
                    File.ReadAllText(_cachePath), JsonOptions);
                return catalog != null
                    ? new PluginCatalogResult(catalog.Plugins, "使用本地缓存")
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Pynelith/Plugins");
            return client;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }
}
