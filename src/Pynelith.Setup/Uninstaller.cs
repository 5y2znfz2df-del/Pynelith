using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Pynelith.Setup
{
    /// <summary>
    /// 卸载：删除快捷方式、卸载注册表项，然后延时删除安装目录。
    /// 由安装目录下的 uninstall.exe 带 <c>/uninstall</c> 参数触发。
    /// </summary>
    internal static class Uninstaller
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Pynelith";

        public static void Run()
        {
            string dir = (AppContext.BaseDirectory ?? string.Empty).TrimEnd('\\');
            if (dir.Length == 0)
            {
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                "确定要卸载 Pynelith 吗？\n\n安装位置：" + dir,
                "卸载 Pynelith beta0.1.0",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (Process running in Process.GetProcessesByName("Pynelith.Island"))
            {
                try
                {
                    running.Kill();
                    running.WaitForExit(3000);
                }
                catch (Exception)
                {
                }
            }

            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Pynelith.lnk"));
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Pynelith.lnk"));

            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
            }

            MessageBox.Show("已卸载 Pynelith。", "卸载完成",
                MessageBoxButton.OK, MessageBoxImage.Information);

            // 卸载程序自身仍在运行，交给 cmd 延时删除整个目录
            try
            {
                Process.Start(new ProcessStartInfo(
                    "cmd.exe",
                    "/c timeout /t 2 >nul & rmdir /s /q \"" + dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
