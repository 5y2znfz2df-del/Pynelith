using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Pynelith.Island.Services.Plugins
{
    /// <summary>
    /// 可回收的插件加载上下文。
    ///
    /// 每个插件独占一个 ALC：卸载（Unload）后程序集可被回收，插件目录可以整体替换，
    /// 无需重启主程序即可完成升级 —— 这是"导入插件不重启"的基础。
    /// </summary>
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginDirectory)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginDirectory);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契约程序集必须由默认上下文提供：若插件目录里也带了一份，
            // 两个副本会造成接口类型不一致，宿主 Cast 直接失败。
            if (string.Equals(
                assemblyName.Name,
                typeof(Pynelith.Plugins.IPynelithPlugin).Assembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
