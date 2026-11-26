using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Pynelith.Island.Services.Plugins;
using Pynelith.Plugins;
using Xunit;

namespace Pynelith.Tests
{
    /// <summary>
    /// 插件宿主端到端：发现 → 加载 → 轮询 → 禁用 / 卸载。
    /// 用真实插件工程（GESP 倒计时，纯算术、无网络、无系统依赖）验证，不碰真实 %AppData%。
    /// </summary>
    public sealed class PluginLoaderTests : IDisposable
    {
        private const string PluginId = "test-gesp";
        private const string EntryType = "Pynelith.Plugin.GespCountdown.GespCountdownPlugin";

        private readonly string _root;
        private readonly string _appDirectory;
        private readonly string _userRoot;
        private readonly string _statePath;

        public PluginLoaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pynelith-tests-" + Guid.NewGuid().ToString("N"));
            _appDirectory = Path.Combine(_root, "app");
            _userRoot = Path.Combine(_root, "user-plugins");
            _statePath = Path.Combine(_root, "plugins.json");

            Directory.CreateDirectory(Path.Combine(_appDirectory, "Plugins"));
            Directory.CreateDirectory(_userRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 插件 ALC 尚未完全回收时文件可能仍被占用，测试目录留给系统回收
            }
        }

        [Fact]
        public void LoadAll_LoadsBundledPlugin_Under100ms()
        {
            InstallBundledPlugin("MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();

            PluginDescriptor plugin = Single(manager);
            Assert.Equal(PluginState.Loaded, plugin.State);
            Assert.True(plugin.IsEnabled);
            Assert.Equal(string.Empty, plugin.Error);

            // 预热一次（首个 ALC 会带 JIT 开销），再用新实例测量冷启动时间
            manager.Dispose();

            using var warm = new PluginManager(_appDirectory, _userRoot, _statePath);
            warm.LoadAll();
            PluginDescriptor warmPlugin = Single(warm);

            Assert.Equal(PluginState.Loaded, warmPlugin.State);
            Assert.True(
                warmPlugin.LoadMilliseconds < 100,
                "插件加载耗时应在 100ms 以内，实际 " + warmPlugin.LoadMilliseconds.ToString("0.0") + " ms");
        }

        [Fact]
        public void LoadAll_KindMismatch_IsFailedWithoutBreakingHost()
        {
            // 清单声明 AI 后端，但入口类型是监控源
            InstallBundledPlugin("AiBackend", EntryType);

            using var manager = NewManager();
            manager.LoadAll();

            PluginDescriptor plugin = Single(manager);
            Assert.Equal(PluginState.Failed, plugin.State);
            Assert.False(plugin.IsEnabled);
            Assert.Contains("接口", plugin.Error);

            // 失败插件不影响管理器自身状态
            Assert.Empty(manager.MetricSummary);
        }

        [Fact]
        public void LoadAll_BrokenManifest_IsInvalidButListed()
        {
            string directory = InstallBundledPlugin("MonitorSource", EntryType, id: "Broken.Id");
            // id 含大写字母 → 校验失败
            File.WriteAllText(
                Path.Combine(directory, "plugin.json"),
                "{ \"id\": \"Broken.Id\", \"name\": \"坏清单\", \"entryAssembly\": \"a.dll\", \"entryType\": \"A.B\", \"kind\": \"MonitorSource\" }");

            using var manager = NewManager();
            manager.LoadAll();

            PluginDescriptor plugin = Single(manager);
            Assert.Equal(PluginState.Invalid, plugin.State);
            Assert.False(string.IsNullOrEmpty(plugin.Error));
        }

        [Fact]
        public void SetEnabled_TogglesWithoutReloadingHost()
        {
            InstallBundledPlugin("MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();
            Assert.Equal(PluginState.Loaded, Single(manager).State);

            Assert.True(manager.SetEnabled(PluginId, false));
            Assert.Equal(PluginState.Disabled, Single(manager).State);
            Assert.False(Single(manager).IsEnabled);

            Assert.True(manager.SetEnabled(PluginId, true));
            Assert.Equal(PluginState.Loaded, Single(manager).State);
        }

        [Fact]
        public void SetEnabled_PersistsAcrossManagerInstances()
        {
            InstallBundledPlugin("MonitorSource", EntryType);

            using (var first = NewManager())
            {
                first.LoadAll();
                first.SetEnabled(PluginId, false);
            }

            using var second = new PluginManager(_appDirectory, _userRoot, _statePath);
            second.LoadAll();

            Assert.Equal(PluginState.Disabled, Single(second).State);
        }

        [Fact]
        public void PollMonitors_RaisesMetrics()
        {
            InstallBundledPlugin("MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();

            IReadOnlyList<PluginMetric>? received = null;
            using var signal = new ManualResetEventSlim(false);
            manager.MetricsUpdated += (_, args) =>
            {
                received = args.Metrics;
                signal.Set();
            };

            manager.PollMonitors();

            Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "监控源应在 5 秒内产出一次指标");
            Assert.NotNull(received);
            Assert.Contains(received!, metric => metric.Key == "gesp");
            Assert.Contains("GESP", manager.MetricSummary);
        }

        [Fact]
        public void Uninstall_UserPlugin_DeletesDirectory()
        {
            string directory = Path.Combine(_userRoot, PluginId);
            InstallPluginAt(directory, "MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();
            Assert.Equal(PluginState.Loaded, Single(manager).State);

            Assert.True(manager.Uninstall(PluginId));

            Assert.False(Directory.Exists(directory));
            Assert.Empty(manager.Plugins);
        }

        [Fact]
        public void Uninstall_BundledPlugin_FallsBackToDisable()
        {
            string directory = InstallBundledPlugin("MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();

            Assert.True(manager.Uninstall(PluginId));

            // 内置插件不能删，只能禁用
            Assert.True(Directory.Exists(directory));
            Assert.Equal(PluginState.Disabled, Single(manager).State);
        }

        [Fact]
        public void UserPlugin_OverridesBundledPluginWithSameId()
        {
            InstallBundledPlugin("MonitorSource", EntryType);
            InstallPluginAt(Path.Combine(_userRoot, PluginId), "MonitorSource", EntryType);

            using var manager = NewManager();
            manager.LoadAll();

            PluginDescriptor plugin = Single(manager);
            Assert.Equal(_userRoot, Path.GetDirectoryName(plugin.Directory));
        }

        private PluginManager NewManager() => new PluginManager(_appDirectory, _userRoot, _statePath);

        private static PluginDescriptor Single(PluginManager manager)
        {
            Assert.Single(manager.Plugins);
            return manager.Plugins[0];
        }

        private string InstallBundledPlugin(string kind, string entryType, string id = PluginId)
        {
            string directory = Path.Combine(_appDirectory, "Plugins", id);
            InstallPluginAt(directory, kind, entryType);
            return directory;
        }

        private static void InstallPluginAt(string directory, string kind, string entryType)
        {
            Directory.CreateDirectory(directory);

            string source = typeof(Pynelith.Plugin.GespCountdown.GespCountdownPlugin).Assembly.Location;
            string fileName = Path.GetFileName(source);
            File.Copy(source, Path.Combine(directory, fileName), overwrite: true);

            File.WriteAllText(
                Path.Combine(directory, "plugin.json"),
                "{ \"id\": \"" + PluginId + "\","
                + " \"name\": \"测试倒计时\","
                + " \"version\": \"1.0.0\","
                + " \"author\": \"tests\","
                + " \"description\": \"仅用于测试\","
                + " \"kind\": \"" + kind + "\","
                + " \"entryAssembly\": \"" + fileName + "\","
                + " \"entryType\": \"" + entryType + "\","
                + " \"minHostVersion\": \"0.3.0\","
                + " \"settings\": { \"label\": \"GESP\", \"targetDate\": \"2099-03-21\", \"targetTime\": \"09:00\" } }");
        }

        [Fact]
        public void LoadAll_DoesNotTouchRealPluginDirectory()
        {
            // 防护性断言：本测试类的所有路径都必须落在临时目录内
            Assert.StartsWith(Path.GetTempPath(), _root, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                _root,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
