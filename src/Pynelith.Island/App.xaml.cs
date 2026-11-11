using System;
using System.Threading;
using System.Windows;
using Pynelith.Island.Configuration;
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
        private Mutex? _instanceMutex;
        private bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            _ = _media.InitializeAsync();
        }

        private static void WriteCrashLog(System.Exception? exception)
        {
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pynelith");
                System.IO.Directory.CreateDirectory(directory);

                string line = string.Format(
                    "[{0}] {1}{2}{3}",
                    DateTime.Now,
                    exception?.GetType().FullName,
                    Environment.NewLine,
                    exception);

                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(directory, "crash.log"),
                    line + Environment.NewLine + Environment.NewLine);
            }
            catch (System.Exception)
            {
                // 日志失败不影响退出流程。
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
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
