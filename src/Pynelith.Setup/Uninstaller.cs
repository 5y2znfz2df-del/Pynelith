using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Pynelith.Setup
{
    /// <summary>
    /// 一键卸载：结束常驻进程、删除快捷方式、开机自启项、卸载注册表项、
    /// 本地配置与歌词缓存，最后延时删除安装目录。
    /// 由安装目录下的 uninstall.exe 带 <c>/uninstall</c> 参数触发；
    /// 安装界面调用时附加 <c>/quiet</c>，由界面自己负责确认。
    /// </summary>
    internal static class Uninstaller
    {
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Pynelith";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Pynelith";
        private const string ProductFolder = "Pynelith";

        public static void Run(bool quiet)
        {
            string dir = (AppContext.BaseDirectory ?? string.Empty).TrimEnd('\\');
            if (dir.Length == 0)
            {
                return;
            }

            if (!quiet)
            {
                DialogResult confirm = MessageBox.Show(
                    "确定要卸载 Pynelith 吗？\r\n\r\n"
                    + "将同时清除：程序文件、桌面与开始菜单快捷方式、开机自启项、\r\n"
                    + "本地配置、歌词缓存与运行日志。\r\n\r\n"
                    + "安装位置：" + dir,
                    "卸载 Pynelith " + Installer.VersionText,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            KillIslandProcesses();

            DeleteShortcuts();
            DeleteAutoStartEntry();
            DeleteUserData();
            DeleteUninstallEntry();
            DeleteInstallDirectory(dir);

            if (!quiet)
            {
                MessageBox.Show(
                    "Pynelith 已卸载。\r\n\r\n程序文件、快捷方式、开机启动项、配置、歌词缓存均已清除。",
                    "卸载完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        /// <summary>结束常驻的灵动岛进程（先礼后兵：先请求关闭，再强制结束）。</summary>
        private static void KillIslandProcesses()
        {
            foreach (Process running in Process.GetProcessesByName("Pynelith.Island"))
            {
                try
                {
                    running.CloseMainWindow();
                    if (!running.WaitForExit(1500))
                    {
                        running.Kill();
                        running.WaitForExit(3000);
                    }
                }
                catch (Exception)
                {
                    // 进程可能已经退出，忽略。
                }
            }
        }

        private static void DeleteShortcuts()
        {
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductFolder + ".lnk"));
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductFolder + ".lnk"));
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductFolder + ".lnk"));
        }

        /// <summary>移除登录自启项（CURRENT_USER\...\Run）。</summary>
        private static void DeleteAutoStartEntry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>清除配置、歌词缓存、崩溃日志，以及单文件运行时可能产生的临时解包目录。</summary>
        private static void DeleteUserData()
        {
            TryDeleteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductFolder));
            TryDeleteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder));

            string temp = Path.GetTempPath();
            TryDeleteDirectory(Path.Combine(temp, ".net", "Pynelith.Island"));
            TryDeleteDirectory(Path.Combine(temp, ".net", "PynelithSetup"));
        }

        private static void DeleteUninstallEntry()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    baseKey.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 卸载程序自身就在安装目录里，无法直接删自己：
        /// 交给一个工作目录在 System32 的 cmd，等本进程退出后再整目录删除。
        /// </summary>
        private static void DeleteInstallDirectory(string dir)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory,
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception)
            {
                // 文件被占用时忽略：下次卸载/重启后可再清理，不影响已删除的主体。
            }
        }
    }
}
