using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Plugins;

namespace Pynelith.Island.Services.Plugins
{
    /// <summary>插件事件参数（通知）。</summary>
    public sealed class PluginNotificationEventArgs : EventArgs
    {
        public PluginNotificationEventArgs(PluginDescriptor plugin, PluginNotification notification)
        {
            Plugin = plugin;
            Notification = notification;
        }

        public PluginDescriptor Plugin { get; }

        public PluginNotification Notification { get; }
    }

    /// <summary>插件事件参数（监控指标）。</summary>
    public sealed class PluginMetricsEventArgs : EventArgs
    {
        public PluginMetricsEventArgs(PluginDescriptor plugin, IReadOnlyList<PluginMetric> metrics)
        {
            Plugin = plugin;
            Metrics = metrics;
        }

        public PluginDescriptor Plugin { get; }

        public IReadOnlyList<PluginMetric> Metrics { get; }
    }

    /// <summary>
    /// 轻量插件宿主。
    ///
    /// 约定：
    /// - 插件目录：程序目录 <c>Plugins\&lt;id&gt;\</c>（随包分发）与
    ///   <c>%AppData%\Pynelith\plugins\&lt;id&gt;\</c>（用户导入，同 id 覆盖内置版本）；
    /// - 每个插件一个可回收 ALC，启用 / 禁用 / 升级都不需要重启主程序；
    /// - 加载失败只影响该插件（状态标为 Failed，原因展示在设置页），不连累宿主。
    /// </summary>
    public sealed class PluginManager : IDisposable
    {
        private const string ManifestFileName = "plugin.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _bundledRoot;
        private readonly string _userRoot;
        private readonly string _statePath;
        private readonly object _gate = new object();
        private readonly List<LoadedPlugin> _loaded = new List<LoadedPlugin>();
        private readonly List<PluginDescriptor> _descriptors = new List<PluginDescriptor>();
        private readonly Dictionary<string, string> _metricCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<PluginMetric>> _metricsByPlugin =
            new Dictionary<string, List<PluginMetric>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <param name="appDirectory">主程序目录，内置插件在其中的 <c>Plugins\</c> 下。</param>
        /// <param name="userPluginRoot">
        /// 用户插件目录；为空时用 <c>%AppData%\Pynelith\plugins</c>。
        /// 测试可传临时目录，避免污染真实配置。
        /// </param>
        /// <param name="statePath">
        /// 启用状态文件；为空时用 <c>%AppData%\Pynelith\plugins.json</c>。
        /// </param>
        public PluginManager(string appDirectory, string? userPluginRoot = null, string? statePath = null)
        {
            _bundledRoot = Path.Combine(appDirectory, "Plugins");
            _userRoot = string.IsNullOrWhiteSpace(userPluginRoot)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Pynelith",
                    "plugins")
                : userPluginRoot!;
            _statePath = string.IsNullOrWhiteSpace(statePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Pynelith",
                    "plugins.json")
                : statePath!;
        }

        /// <summary>插件列表发生变化（启用、禁用、导入、卸载）。</summary>
        public event EventHandler? Changed;

        /// <summary>插件推送通知（可能来自后台线程，订阅方自行切回 UI 线程）。</summary>
        public event EventHandler<PluginNotificationEventArgs>? NotificationReceived;

        /// <summary>插件指标更新（可能来自后台线程）。</summary>
        public event EventHandler<PluginMetricsEventArgs>? MetricsUpdated;

        /// <summary>日志回调。</summary>
        public event Action<string>? Log;

        /// <summary>用户插件目录（可写入）。</summary>
        public string UserPluginRoot => _userRoot;

        /// <summary>随包分发的插件目录（只读）。</summary>
        public string BundledPluginRoot => _bundledRoot;

        /// <summary>当前插件快照。</summary>
        public IReadOnlyList<PluginDescriptor> Plugins
        {
            get
            {
                lock (_gate)
                {
                    return _descriptors.ToArray();
                }
            }
        }

        /// <summary>
        /// 带输入框的通知处理器（由主窗口注入）。
        /// 参数：标题、正文、超时秒数、取消令牌；返回用户输入，取消或超时返回 null。
        /// </summary>
        public Func<string, string, int, CancellationToken, Task<string?>>? AskHandler { get; set; }

        /// <summary>把所有监控插件的最新指标拼成一行（最多两条），供空闲态展示。</summary>
        public string MetricSummary
        {
            get
            {
                lock (_gate)
                {
                    if (_metricCache.Count == 0)
                    {
                        return string.Empty;
                    }

                    return string.Join(" · ", _metricCache.Values.Take(2));
                }
            }
        }

        /// <summary>
        /// 已加载插件里声明的最低动画档位（数值越小画质越高）。
        /// 动画皮肤通过 <see cref="IAnimationQualityAware"/> 声明；没有声明返回 null。
        /// </summary>
        public PluginAnimationQuality? PreferredAnimationQuality
        {
            get
            {
                lock (_gate)
                {
                    PluginAnimationQuality? best = null;

                    foreach (LoadedPlugin plugin in _loaded)
                    {
                        if (plugin.Instance is IAnimationQualityAware aware)
                        {
                            PluginAnimationQuality value = aware.PreferredAnimationQuality;
                            if (best == null || value < best.Value)
                            {
                                best = value;
                            }
                        }
                    }

                    return best;
                }
            }
        }

        /// <summary>
        /// 扫描并加载全部插件。单次加载耗时写入 <see cref="PluginDescriptor.LoadMilliseconds"/>。
        /// </summary>
        public void LoadAll()
        {
            lock (_gate)
            {
                UnloadAllLocked();
                _descriptors.Clear();
            }

            _enabled = ReadState();

            foreach (string directory in DiscoverDirectories())
            {
                PluginDescriptor? descriptor = ReadDescriptor(directory);
                if (descriptor is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    // 用户目录的同 id 插件优先于内置版本
                    int existing = _descriptors.FindIndex(
                        d => string.Equals(d.Manifest.Id, descriptor.Manifest.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                    {
                        _descriptors.RemoveAt(existing);
                    }

                    _descriptors.Add(descriptor);
                }
            }

            PluginDescriptor[] snapshot;
            lock (_gate)
            {
                snapshot = _descriptors.ToArray();
            }

            foreach (PluginDescriptor descriptor in snapshot)
            {
                // 清单本身不合法（id / 字段缺失）时不要尝试加载，保持 Invalid 展示给用户
                if (descriptor.State == PluginState.Invalid)
                {
                    descriptor.IsEnabled = false;
                    continue;
                }

                bool enabled = !_enabled.TryGetValue(descriptor.Manifest.Id, out bool value) || value;
                if (!enabled)
                {
                    descriptor.IsEnabled = false;
                    descriptor.State = PluginState.Disabled;
                    continue;
                }

                Load(descriptor);
            }

            WriteState();
            RaiseChanged();
        }

        /// <summary>启用 / 禁用插件（立即生效，不需要重启）。</summary>
        public bool SetEnabled(string id, bool enabled)
        {
            PluginDescriptor? descriptor = Find(id);
            if (descriptor == null)
            {
                return false;
            }

            _enabled[id] = enabled;
            WriteState();

            if (enabled)
            {
                if (descriptor.State != PluginState.Loaded)
                {
                    Load(descriptor);
                }
            }
            else
            {
                Unload(descriptor);
                descriptor.IsEnabled = false;
                descriptor.State = PluginState.Disabled;
                descriptor.Error = string.Empty;
            }

            RaiseChanged();
            return true;
        }

        /// <summary>重新加载单个插件（升级/替换文件后调用）。</summary>
        public bool Reload(string id)
        {
            PluginDescriptor? descriptor = Find(id);
            if (descriptor == null)
            {
                return false;
            }

            Unload(descriptor);
            descriptor.Error = string.Empty;

            bool enabled = !_enabled.TryGetValue(id, out bool value) || value;
            if (enabled)
            {
                Load(descriptor);
            }
            else
            {
                descriptor.IsEnabled = false;
                descriptor.State = PluginState.Disabled;
            }

            RaiseChanged();
            return true;
        }

        /// <summary>
        /// 卸载并删除用户目录下的插件。内置插件不可删除，只能禁用。
        /// </summary>
        public bool Uninstall(string id)
        {
            PluginDescriptor? descriptor = Find(id);
            if (descriptor == null)
            {
                return false;
            }

            if (!IsUnderUserRoot(descriptor.Directory))
            {
                return SetEnabled(id, false);
            }

            Unload(descriptor);

            try
            {
                if (Directory.Exists(descriptor.Directory))
                {
                    DeleteDirectoryWithRetry(descriptor.Directory);
                }
            }
            catch (Exception ex)
            {
                descriptor.Error = "删除插件目录失败：" + ex.Message;
                RaiseChanged();
                return false;
            }

            lock (_gate)
            {
                _descriptors.Remove(descriptor);
            }

            _enabled.Remove(id);
            WriteState();
            RaiseChanged();
            return true;
        }

        /// <summary>由宿主定时调用：按各插件声明的间隔在后台轮询监控源。</summary>
        public void PollMonitors()
        {
            LoadedPlugin[] snapshot;
            lock (_gate)
            {
                snapshot = _loaded.ToArray();
            }

            long now = Environment.TickCount64;
            foreach (LoadedPlugin plugin in snapshot)
            {
                if (plugin.Instance is not IMonitorSourcePlugin monitor)
                {
                    continue;
                }

                int interval = Math.Max(1000, monitor.PollIntervalMs);
                if (now - plugin.LastPollTicks < interval)
                {
                    continue;
                }

                // 上一轮还没结束就跳过，避免慢插件堆积
                if (Interlocked.Exchange(ref plugin.Polling, 1) == 1)
                {
                    continue;
                }

                plugin.LastPollTicks = now;
                Task.Run(() =>
                {
                    try
                    {
                        IReadOnlyList<PluginMetric> metrics = monitor.Poll();
                        if (metrics != null && metrics.Count > 0)
                        {
                            RaiseMetrics(plugin.Descriptor, metrics);
                        }
                    }
                    catch (Exception ex)
                    {
                        plugin.Descriptor.Error = ex.Message;
                    }
                    finally
                    {
                        Volatile.Write(ref plugin.Polling, 0);
                    }
                });
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_gate)
            {
                UnloadAllLocked();
                _descriptors.Clear();
            }

            // 卸载是异步的，等 ALC 真正回收后再让调用方继续（进程退出 / 测试清理目录）
            WaitForUnload();
        }

        private IEnumerable<string> DiscoverDirectories()
        {
            foreach (string root in new[] { _bundledRoot, _userRoot })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(root);
                }
                catch (Exception)
                {
                    continue;
                }

                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                {
                    if (File.Exists(Path.Combine(directory, ManifestFileName)))
                    {
                        yield return directory;
                    }
                }
            }
        }

        private PluginDescriptor? ReadDescriptor(string directory)
        {
            var descriptor = new PluginDescriptor { Directory = directory, IsEnabled = false };
            try
            {
                string json = File.ReadAllText(Path.Combine(directory, ManifestFileName));
                PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
                if (manifest == null)
                {
                    throw new PluginException("plugin.json 解析结果为空");
                }

                manifest.Validate();
                descriptor.Manifest = manifest;

                if (Version.TryParse(manifest.MinHostVersion, out Version? min)
                    && min != null
                    && typeof(PluginManager).Assembly.GetName().Version is Version hostVersion
                    && min > hostVersion)
                {
                    throw new PluginException("需要宿主 " + manifest.MinHostVersion + " 或更高版本");
                }

                return descriptor;
            }
            catch (Exception ex)
            {
                Log?.Invoke("[plugins] invalid manifest in " + directory + ": " + ex.Message);

                // 清单坏掉的目录也展示出来（带原因），方便用户删除或替换
                descriptor.State = PluginState.Invalid;
                descriptor.Error = ex.Message;
                if (string.IsNullOrEmpty(descriptor.Manifest.Id))
                {
                    descriptor.Manifest.Id = Path.GetFileName(directory);
                    descriptor.Manifest.Name = Path.GetFileName(directory);
                }

                return descriptor;
            }
        }

        private LoadedPlugin? Load(PluginDescriptor descriptor)
        {
            var watch = Stopwatch.StartNew();
            descriptor.Error = string.Empty;
            PluginLoadContext? context = null;

            try
            {
                context = new PluginLoadContext(descriptor.Directory);
                string assemblyPath = Path.Combine(descriptor.Directory, descriptor.Manifest.EntryAssembly);
                if (!File.Exists(assemblyPath))
                {
                    throw new PluginException("找不到入口程序集：" + descriptor.Manifest.EntryAssembly);
                }

                var assembly = context.LoadFromAssemblyPath(assemblyPath);
                Type? type = assembly.GetType(descriptor.Manifest.EntryType, throwOnError: false);
                if (type == null)
                {
                    throw new PluginException("找不到入口类型：" + descriptor.Manifest.EntryType);
                }

                if (Activator.CreateInstance(type) is not IPynelithPlugin instance)
                {
                    throw new PluginException("入口类型没有实现 IPynelithPlugin");
                }

                if (!KindMatches(instance, descriptor.Manifest.Kind))
                {
                    throw new PluginException(
                        "清单声明类型 " + descriptor.Manifest.Kind + "，但入口类型未实现对应接口");
                }

                instance.Initialize(new PluginHostContext(this, descriptor));

                if (instance is INotificationSourcePlugin notificationSource)
                {
                    notificationSource.Received += (_, notification) => RaiseNotification(descriptor, notification);
                }

                descriptor.State = PluginState.Loaded;
                descriptor.IsEnabled = true;
                descriptor.LoadMilliseconds = watch.Elapsed.TotalMilliseconds;
                Log?.Invoke(string.Format(
                    "[plugins] loaded {0} in {1:0.0} ms", descriptor.Manifest.Id, descriptor.LoadMilliseconds));

                var loaded = new LoadedPlugin(descriptor, context, instance);
                lock (_gate)
                {
                    _loaded.Add(loaded);
                }

                return loaded;
            }
            catch (Exception ex)
            {
                // 加载失败也要回收 ALC，否则每次失败重试都残留一份程序集
                context?.Unload();
                descriptor.State = PluginState.Failed;
                descriptor.IsEnabled = false;
                descriptor.LoadMilliseconds = watch.Elapsed.TotalMilliseconds;
                descriptor.Error = ex.Message;
                Log?.Invoke(string.Format(
                    "[plugins] failed {0} in {1:0.0} ms: {2}",
                    descriptor.Manifest.Id,
                    descriptor.LoadMilliseconds,
                    ex.Message));
                return null;
            }
        }

        private static bool KindMatches(IPynelithPlugin instance, PluginKind kind) => kind switch
        {
            PluginKind.NotificationSource => instance is INotificationSourcePlugin,
            PluginKind.MonitorSource => instance is IMonitorSourcePlugin,
            PluginKind.AiBackend => instance is IAiBackendPlugin,
            PluginKind.Skin => instance is ISkinPlugin,
            _ => false,
        };

        private void Unload(PluginDescriptor descriptor)
        {
            LoadedPlugin? loaded = null;
            lock (_gate)
            {
                loaded = _loaded.FirstOrDefault(
                    p => string.Equals(p.Descriptor.Manifest.Id, descriptor.Manifest.Id, StringComparison.OrdinalIgnoreCase));
                if (loaded != null)
                {
                    _loaded.Remove(loaded);
                }
            }

            UnloadInstance(loaded);
        }

        private void UnloadAllLocked()
        {
            LoadedPlugin[] loaded = _loaded.ToArray();
            _loaded.Clear();

            foreach (LoadedPlugin plugin in loaded)
            {
                UnloadInstance(plugin);
            }
        }

        private void UnloadInstance(LoadedPlugin? plugin)
        {
            if (plugin == null)
            {
                return;
            }

            try
            {
                plugin.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Invoke("[plugins] dispose failed " + plugin.Descriptor.Manifest.Id + ": " + ex.Message);
            }

            try
            {
                plugin.Context.Unload();
            }
            catch (Exception)
            {
                // ALC 卸载失败不影响主流程；进程退出时会一并回收
            }
        }

        /// <summary>
        /// 可回收 ALC 的卸载是异步的：程序集文件要等 GC 回收后才真正解锁。
        /// 删除插件目录前必须等待，否则会报「文件被占用 / 拒绝访问」。
        /// </summary>
        private static void WaitForUnload()
        {
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static void DeleteDirectoryWithRetry(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
                {
                    // 句柄还没释放，回收一轮再试
                }

                WaitForUnload();
                Thread.Sleep(50);
            }
        }

        /// <summary>供 <see cref="PluginHostContext"/> 调用：写带插件 id 的日志。</summary>
        internal void RaisePluginLog(string pluginId, string message)
        {
            Log?.Invoke("[plugins] " + pluginId + ": " + message);
        }

        /// <summary>供 <see cref="PluginHostContext"/> 调用：插件主动推送通知。</summary>
        internal void RaisePluginNotification(PluginDescriptor descriptor, PluginNotification notification)
            => RaiseNotification(descriptor, notification);

        /// <summary>供 <see cref="PluginHostContext"/> 调用：插件主动上报指标。</summary>
        internal void RaisePluginMetrics(PluginDescriptor descriptor, IReadOnlyList<PluginMetric> metrics)
            => RaiseMetrics(descriptor, metrics);

        private void RaiseNotification(PluginDescriptor descriptor, PluginNotification notification)
        {
            if (notification == null || _disposed)
            {
                return;
            }

            NotificationReceived?.Invoke(this, new PluginNotificationEventArgs(descriptor, notification));
        }

        private void RaiseMetrics(PluginDescriptor descriptor, IReadOnlyList<PluginMetric> metrics)
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (!_metricsByPlugin.TryGetValue(descriptor.Manifest.Id, out List<PluginMetric>? list))
                {
                    list = new List<PluginMetric>();
                    _metricsByPlugin[descriptor.Manifest.Id] = list;
                }

                foreach (PluginMetric metric in metrics)
                {
                    if (string.IsNullOrWhiteSpace(metric.Key))
                    {
                        continue;
                    }

                    _metricCache[descriptor.Manifest.Id + "." + metric.Key] = metric.ToDisplayText();

                    int index = list.FindIndex(m => string.Equals(m.Key, metric.Key, StringComparison.Ordinal));
                    if (index >= 0)
                    {
                        list[index] = metric;
                    }
                    else
                    {
                        list.Add(metric);
                    }
                }
            }

            MetricsUpdated?.Invoke(this, new PluginMetricsEventArgs(descriptor, metrics));
        }

        /// <summary>某个插件的最新指标快照（插件面板按卡片展示）。</summary>
        public IReadOnlyList<PluginMetric> GetMetrics(string id)
        {
            lock (_gate)
            {
                return _metricsByPlugin.TryGetValue(id, out List<PluginMetric>? list)
                    ? list.ToArray()
                    : Array.Empty<PluginMetric>();
            }
        }

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private PluginDescriptor? Find(string id)
        {
            lock (_gate)
            {
                return _descriptors.FirstOrDefault(
                    d => string.Equals(d.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        private bool IsUnderUserRoot(string directory)
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetFullPath(_userRoot);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private Dictionary<string, bool> ReadState()
        {
            try
            {
                if (File.Exists(_statePath))
                {
                    var state = JsonSerializer.Deserialize<Dictionary<string, bool>>(
                        File.ReadAllText(_statePath), JsonOptions);
                    if (state != null)
                    {
                        return new Dictionary<string, bool>(state, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception)
            {
                // 状态文件损坏：退回全启用
            }

            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        private void WriteState()
        {
            try
            {
                string? directory = Path.GetDirectoryName(_statePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_statePath, JsonSerializer.Serialize(_enabled, JsonOptions));
            }
            catch (Exception)
            {
                // 状态落盘失败不影响本次会话
            }
        }

        private sealed class LoadedPlugin
        {
            public LoadedPlugin(PluginDescriptor descriptor, PluginLoadContext context, IPynelithPlugin instance)
            {
                Descriptor = descriptor;
                Context = context;
                Instance = instance;
            }

            public PluginDescriptor Descriptor { get; }

            public PluginLoadContext Context { get; }

            public IPynelithPlugin Instance { get; }

            public long LastPollTicks;

            public int Polling;
        }
    }
}
