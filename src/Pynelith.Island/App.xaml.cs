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

            _configService = new ConfigService();

            var island = new IslandWindow(_configService);
            MainWindow = island;
            island.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _configService?.Dispose();
            base.OnExit(e);
        }
    }
}
