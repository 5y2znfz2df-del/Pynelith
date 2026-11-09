using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace Pynelith.Setup
{
    /// <summary>
    /// 安装程序宿主：默认进入安装界面；带 <c>/uninstall</c> 参数时执行卸载。
    /// 清单已声明 requireAdministrator，启动即获得管理员权限。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool uninstall = e.Args.Any(a =>
                string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

            if (uninstall)
            {
                Uninstaller.Run();
                Shutdown();
                return;
            }

            var window = new InstallerWindow();
            MainWindow = window;
            window.Show();
        }
    }
}
