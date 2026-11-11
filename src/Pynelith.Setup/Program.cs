using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Pynelith.Setup
{
    /// <summary>
    /// 安装器入口：默认显示安装界面；带 <c>/uninstall</c> 时执行卸载。
    /// 清单已声明 requireAdministrator，启动即获得管理员权限。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstall = args.Any(a =>
                string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

            if (uninstall)
            {
                bool quiet = args.Any(a =>
                    string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase));

                Uninstaller.Run(quiet);
                return;
            }

            Application.Run(new InstallerForm());
        }

        /// <summary>从内嵌资源加载应用图标（没有也不影响安装）。</summary>
        internal static System.Drawing.Icon? LoadIcon()
        {
            try
            {
                using Stream? stream = typeof(Program).Assembly
                    .GetManifestResourceStream("pynelith.ico");
                return stream == null ? null : new System.Drawing.Icon(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
