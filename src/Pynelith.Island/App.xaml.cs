using System;
using System.Windows;
using Pynelith.Island.Configuration;
using Pynelith.Island.Views;

namespace Pynelith.Island
{
    /// <summary>
    /// 灵动岛宿主。只负责装配：配置服务 + 岛窗口。
    /// </summary>
    public partial class App : Application
    {
        private ConfigService? _configService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            var island = new IslandWindow(_configService);
            MainWindow = island;
            island.Show();
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
            _configService?.Dispose();
            base.OnExit(e);
        }
    }
}
