using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Pynelith.Island.Configuration;
using Pynelith.Island.Services;
using Pynelith.Island.Services.Lyrics;
using Pynelith.Island.Services.Media;
using Pynelith.Island.Views;

namespace Pynelith.Island
{
    /// <summary>
    /// 灵动岛宿主。只负责装配：配置服务 + 媒体会话 + 歌词服务 + 岛窗口。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>单实例互斥名：安装器自动拉起、开机自启、用户手动启动都只会有一个岛。</summary>
        private const string SingleInstanceMutexName = @"Local\Pynelith.Island.SingleInstance";

        private ConfigService? _configService;
        private MediaSessionService? _media;
        private TrayService? _tray;
        private Mutex? _instanceMutex;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 渲染走 GPU：WPF 默认就是硬件（D3D9Ex）渲染，这里显式声明意图，
            // 只有驱动缺失 / 远程桌面等情况才会由系统退回 CPU 软件渲染。
            RenderOptions.ProcessRenderMode = RenderMode.Default;

            // GPU 偏好写注册表只影响"下一次"启动，放在后台线程做，不占用首屏启动时间。
            _ = Task.Run(PreferHighPerformanceGpu);

            // 单实例：重复启动直接退出，避免托盘/顶栏出现多个灵动岛，也避免安装器
            // 自动拉起时与用户已手动打开的实例相互抢停靠位置。
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            // 崩溃留痕：任何未处理异常都写到 %AppData%\Pynelith\crash.log
            DispatcherUnhandledException += (_, args) =>
            {
                WriteCrashLog(args.Exception);

                // UI 线程异常不再直接终结进程：记录后标记已处理，岛继续运行。
                // 否则一次绘制/绑定异常就会让整个灵动岛消失，用户只会看到"闪退"。
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                WriteCrashLog(args.ExceptionObject as System.Exception);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                WriteCrashLog(args.Exception);
                args.SetObserved();
            };

            _configService = new ConfigService();
            _media = new MediaSessionService();
            var lyrics = new LyricsService(_configService.Config);

            var island = new IslandWindow(_configService, _media, lyrics);
            MainWindow = island;
            island.Show();

            // 托盘常驻：岛没有标题栏，托盘是稳定的设置/退出入口。
            // 托盘图标的创建排在首屏之后，先把岛画出来再挂托盘，缩短"点开机到看见岛"的时间。
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    _tray = new TrayService(island.ShowSettingsFromTray, island.ExitFromTray);
                }));

            // 媒体会话初始化需要跨进程请求 SMTC，同样延后到首屏之后。
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { _ = _media.InitializeAsync(); }));
        }

        /// <summary>
        /// 让 Windows 按「独显 &gt; 核显 &gt; 软件」的顺序为 Pynelith 选择渲染适配器。
        /// 写入的是系统标准的「图形性能首选项」（HKCU\UserGpuPreferences，2 = 高性能）：
        /// 没有独显时自动落到核显；GPU 不可用（如远程桌面 / 无 WDDM 驱动）时由 WPF 退回软件渲染。
        /// 该偏好由系统在创建 D3D 设备时读取，写入后需重启程序才会生效。
        /// 注意：Windows 10 1803 起支持；更早的系统忽略此键，仍按系统默认适配器渲染。
        /// </summary>
        private static void PreferHighPerformanceGpu()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }

                const string preference = "GpuPreference=2;"; // 0=系统决定 1=省电 2=高性能

                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences");
                if (key == null)
                {
                    return;
                }

                if (!string.Equals(key.GetValue(exePath) as string, preference, StringComparison.Ordinal))
                {
                    key.SetValue(exePath, preference, RegistryValueKind.String);
                }
            }
            catch (Exception)
            {
                // 注册表不可写（受控环境）时保持系统默认选择，不影响启动。
            }
        }

        /// <summary>崩溃日志大小上限：超过后轮转为 crash.log.1，避免日志无限增长。</summary>
        private const long CrashLogMaxBytes = 1024 * 1024;

        private static readonly object CrashLogLock = new object();
        private static DateTime _lastCrashLogUtc = DateTime.MinValue;
        private static string _lastCrashSignature = string.Empty;

        private static void WriteCrashLog(System.Exception? exception)
        {
            try
            {
                // 同一个异常反复抛出（例如某个渲染回调每帧失败）时 60 秒内只记一次，
                // 既保留排查线索，又不会把磁盘写满、把日志刷成噪音。
                string signature = (exception?.GetType().FullName ?? "null")
                    + "|" + (exception?.Message ?? string.Empty);
                DateTime now = DateTime.UtcNow;
                lock (CrashLogLock)
                {
                    if (string.Equals(signature, _lastCrashSignature, StringComparison.Ordinal)
                        && now - _lastCrashLogUtc < TimeSpan.FromSeconds(60))
                    {
                        return;
                    }

                    _lastCrashSignature = signature;
                    _lastCrashLogUtc = now;
                }

                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pynelith");
                System.IO.Directory.CreateDirectory(directory);

                string path = System.IO.Path.Combine(directory, "crash.log");
                var info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > CrashLogMaxBytes)
                {
                    System.IO.File.Move(path, path + ".1", overwrite: true);
                }

                string line = string.Format(
                    "[{0}] {1}{2}{3}",
                    DateTime.Now,
                    exception?.GetType().FullName,
                    Environment.NewLine,
                    exception);

                System.IO.File.AppendAllText(path, line + Environment.NewLine + Environment.NewLine);
            }
            catch (System.Exception)
            {
                // 日志失败不影响退出流程。
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            _tray = null;

            _media?.Dispose();
            _configService?.Dispose();

            if (_ownsMutex && _instanceMutex != null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 互斥量归属异常时忽略，进程退出后系统会自动释放。
                }
            }

            _instanceMutex?.Dispose();
            _instanceMutex = null;

            base.OnExit(e);
        }
    }
}
