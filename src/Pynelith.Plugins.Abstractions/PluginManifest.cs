using System;
using System.Collections.Generic;

namespace Pynelith.Plugins
{
    /// <summary>
    /// 插件清单（plugin.json）。所有字段都有默认值，新增字段不会破坏老插件。
    /// </summary>
    public sealed class PluginManifest
    {
        /// <summary>全局唯一标识，建议小写字母 + 连字符，例如 weather。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>显示名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>插件版本，语义化版本字符串。</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>作者。</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>一句话说明，展示在设置页。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>插件类型，决定宿主调用哪个接口。</summary>
        public PluginKind Kind { get; set; } = PluginKind.MonitorSource;

        /// <summary>相对插件目录的入口程序集文件名，例如 Pynelith.Plugin.Weather.dll。</summary>
        public string EntryAssembly { get; set; } = string.Empty;

        /// <summary>入口类型的完整名称（含命名空间）。</summary>
        public string EntryType { get; set; } = string.Empty;

        /// <summary>要求的最低宿主版本，低于此版本拒绝加载。</summary>
        public string MinHostVersion { get; set; } = "0.3.0";

        /// <summary>插件自定义配置项，宿主通过 <see cref="IPluginHost.GetSetting"/> 读取。</summary>
        public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 校验清单是否可用。不合法时抛出 <see cref="PluginException"/>，消息可直接展示给用户。
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                throw new PluginException("清单缺少 id 字段");
            }

            if (!IsValidId(Id))
            {
                throw new PluginException("id 只能包含小写字母、数字与连字符：" + Id);
            }

            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new PluginException("清单缺少 name 字段");
            }

            if (!Enum.IsDefined(typeof(PluginKind), Kind))
            {
                throw new PluginException("未知的插件类型：" + (int)Kind);
            }

            if (string.IsNullOrWhiteSpace(EntryAssembly))
            {
                throw new PluginException("清单缺少 entryAssembly 字段");
            }

            if (string.IsNullOrWhiteSpace(EntryType))
            {
                throw new PluginException("清单缺少 entryType 字段");
            }
        }

        private static bool IsValidId(string id)
        {
            if (id.Length == 0 || id.Length > 64)
            {
                return false;
            }

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 插件市场清单条目（plugin-index.json）。
    /// </summary>
    public sealed class PluginCatalogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public PluginKind Kind { get; set; } = PluginKind.MonitorSource;

        /// <summary>zip 包下载地址。</summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>zip 包的 SHA-256（十六进制，大小写不敏感）。为空表示不校验。</summary>
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>插件市场索引文件根对象。</summary>
    public sealed class PluginCatalog
    {
        public int Version { get; set; } = 1;
        public List<PluginCatalogEntry> Plugins { get; set; } = new List<PluginCatalogEntry>();
    }
}
