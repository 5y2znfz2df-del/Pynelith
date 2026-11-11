using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pynelith.Setup
{
    /// <summary>
    /// 安装执行器：真正的文件落盘、快捷方式、卸载注册表与权限校验。
    /// 全程在后台线程运行，通过 <see cref="IProgress{T}"/> 回报进度。
    /// </summary>
    internal static class Installer
    {
        internal const string ProductName = "Pynelith";
        internal const string VersionText = "beta0.1.3";
        internal const string AppExeName = "Pynelith.Island.exe";
        internal const string UninstallExeName = "uninstall.exe";
        // 载荷是压缩后的 zip（内含 Pynelith.Island.exe），把安装包从 ~150MB 压到 10MB 以内
        private const string PayloadResource = "Pynelith.Island.payload.zip";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Pynelith";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Pynelith";

        public static void Install(string installDir, IProgress<(int Percent, string Status)> progress)
        {
            // 旧版安装器关窗后不退出（ShutdownMode 缺陷），会残留无窗口进程并锁住安装包。
            // 安装器本身已提权，这里顺手把同类僵尸实例清掉。
            KillStaleInstallerInstances();

            // 覆盖安装：先结束正在运行的实例，否则文件被占用
            foreach (Process running in Process.GetProcessesByName("Pynelith.Island"))
            {
                try
                {
                    running.Kill();
                    running.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // 结束失败则让后面的文件写入报出真实原因
                }
            }

            progress.Report((0, "正在创建安装目录…"));
            try
            {
                Directory.CreateDirectory(installDir);
            }
            catch (Exception ex)
            {
                throw new IOException("无法创建目录 " + installDir + "：" + ex.Message);
            }

            VerifyWritable(installDir);

            progress.Report((4, "正在释放程序文件…"));
            string targetExe = Path.Combine(installDir, AppExeName);
            ExtractPayload(targetExe, percent =>
                progress.Report((4 + percent * 72 / 100, "正在释放程序文件… " + percent + "%")));

            progress.Report((80, "正在创建快捷方式…"));
            CreateShortcuts(targetExe);

            progress.Report((84, "正在写入卸载信息…"));
            WriteUninstallEntry(installDir, targetExe);

            progress.Report((89, "正在注册开机启动…"));
            RegisterAutoStart(targetExe);

            progress.Report((94, "正在准备卸载程序…"));
            CopyUninstaller(installDir);

            progress.Report((100, "安装完成"));
        }

        /// <summary>
        /// 结束"同名但没有窗口"的旧安装器实例：这是旧版本 ShutdownMode 缺陷留下的僵尸，
        /// 没有界面也不会自行退出，还会锁住安装包。只处理与本进程同名的实例，避免误杀。
        /// </summary>
        private static void KillStaleInstallerInstances()
        {
            string selfName = Process.GetCurrentProcess().ProcessName;
            string uninstallName = Path.GetFileNameWithoutExtension(UninstallExeName);

            if (!selfName.StartsWith("beta0.1.", StringComparison.OrdinalIgnoreCase)
                && !selfName.StartsWith("PynelithSetup", StringComparison.OrdinalIgnoreCase)
                && !selfName.Equals(uninstallName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (Process peer in Process.GetProcessesByName(selfName))
            {
                if (peer.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    // 有窗口的实例可能正在安装，保留；无窗口的基本是旧版残留。
                    if (peer.MainWindowHandle == IntPtr.Zero)
                    {
                        peer.Kill();
                        peer.WaitForExit(2000);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>写一个探针文件验证目录可写（无权限时给出明确提示）。</summary>
        private static void VerifyWritable(string dir)
        {
            string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));

            try
            {
                File.WriteAllText(probe, "ok");
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(
                    "没有写入权限：" + dir + "\n请选择其它目录，或以管理员身份运行安装程序。");
            }
            catch (IOException ex)
            {
                throw new IOException("目录不可写：" + dir + "\n" + ex.Message);
            }
            finally
            {
                try
                {
                    File.Delete(probe);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>解压内嵌的程序 zip 并分块写出，回报百分比。</summary>
        private static void ExtractPayload(string targetPath, Action<int> onProgress)
        {
            using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidOperationException("安装包不完整：缺少程序文件载荷。");

            using var archive = new System.IO.Compression.ZipArchive(
                source, System.IO.Compression.ZipArchiveMode.Read);

            if (archive.Entries.Count == 0)
            {
                throw new InvalidOperationException("安装包不完整：程序文件载荷为空。");
            }

            System.IO.Compression.ZipArchiveEntry entry = archive.Entries[0];

            long total = Math.Max(1, entry.Length);
            byte[] buffer = new byte[1 << 20];
            long copied = 0;
            int lastPercent = -1;

            using Stream payload = entry.Open();
            using var destination = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            int read;
            while ((read = payload.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, read);
                copied += read;

                int percent = (int)(copied * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    onProgress(percent);
                }
            }
        }

        private static void CreateShortcuts(string targetExe)
        {
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk"),
                targetExe);

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName + ".lnk"),
                targetExe);
        }

        private static void CreateShortcut(string linkPath, string targetExe)
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            object? shell = null;
            object? shortcut = null;

            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell == null)
                {
                    return;
                }

                dynamic dynamicShell = shell;
                shortcut = dynamicShell.CreateShortcut(linkPath);
                dynamic link = shortcut;

                link.TargetPath = targetExe;
                link.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? string.Empty;
                link.IconLocation = targetExe + ",0";
                link.Description = ProductName + " 灵动岛";
                link.Save();
            }
            catch (Exception)
            {
                // 快捷方式失败不阻断安装（开始菜单项可能因策略受限）
            }
            finally
            {
                ReleaseCom(shortcut);
                ReleaseCom(shell);
            }
        }

        private static void ReleaseCom(object? instance)
        {
            if (instance != null && Marshal.IsComObject(instance))
            {
                try
                {
                    Marshal.ReleaseComObject(instance);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void WriteUninstallEntry(string installDir, string targetExe)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = baseKey.CreateSubKey(UninstallKey, writable: true);

            key.SetValue("DisplayName", ProductName + " " + VersionText);
            key.SetValue("DisplayVersion", VersionText);
            key.SetValue("Publisher", ProductName);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", targetExe + ",0");
            key.SetValue("UninstallString",
                "\"" + Path.Combine(installDir, UninstallExeName) + "\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 写入当前用户的登录自启项（HKCU\...\Run）。
        /// 灵动岛是普通桌面程序，不需要服务或计划任务：登录后由资源管理器直接拉起即可；
        /// 卸载时删除同一项就能彻底移除常驻启动。
        /// </summary>
        private static void RegisterAutoStart(string targetExe)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                key.SetValue(RunValueName, "\"" + targetExe + "\"", RegistryValueKind.String);
            }
            catch (Exception)
            {
                // 组策略可能禁止写 Run 项：不影响安装本身，用户仍可从快捷方式启动。
            }
        }

        /// <summary>读取已安装位置（供安装界面显示"卸载已安装版本"）。</summary>
        public static string? ReadInstallLocation()
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? key = baseKey.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把安装器自身复制为卸载程序，卸载走同一份代码（带 /uninstall）。</summary>
        private static void CopyUninstaller(string installDir)
        {
            string? self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self) || !File.Exists(self))
            {
                return;
            }

            string target = Path.Combine(installDir, UninstallExeName);
            if (string.Equals(self, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Copy(self, target, true);
            }
            catch (Exception)
            {
                // 卸载程序缺失只影响控制面板卸载，不影响主体安装
            }
        }
    }
}
