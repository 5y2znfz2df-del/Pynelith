using System;
using System.ComponentModel;
using System.Windows.Threading;

namespace Pynelith.Island.Configuration
{
    /// <summary>
    /// 配置中枢：持有唯一配置实例，任何属性变更都会
    /// 1) 立刻抛出 <see cref="Changed"/>（UI 实时应用，含推给渲染模块）；
    /// 2) 防抖落盘，避免拖动滑块时疯狂写文件。
    /// </summary>
    public sealed class ConfigService : IDisposable
    {
        private readonly DispatcherTimer _saveTimer;
        private bool _disposed;

        public ConfigService()
        {
            Config = ConfigStore.Load();
            Config.PropertyChanged += OnConfigPropertyChanged;

            // 首次运行把默认配置落盘，方便用户直接改文件。
            if (!System.IO.File.Exists(ConfigStore.FilePath))
            {
                SaveNow();
            }

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _saveTimer.Tick += OnSaveTick;
        }

        /// <summary>当前配置（唯一实例，UI 直接读写）。</summary>
        public IslandConfig Config { get; }

        /// <summary>配置文件路径，便于设置窗口展示。</summary>
        public string ConfigPath => ConfigStore.FilePath;

        /// <summary>任一配置项变化时触发（UI 线程）。</summary>
        public event EventHandler? Changed;

        private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Changed?.Invoke(this, EventArgs.Empty);

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTick(object? sender, EventArgs e)
        {
            _saveTimer.Stop();
            SaveNow();
        }

        /// <summary>立即落盘（关闭设置窗口 / 退出时调用）。</summary>
        public void SaveNow()
        {
            try
            {
                ConfigStore.Save(Config);
            }
            catch (Exception)
            {
                // 落盘失败不应打断 UI；下次变更会再次尝试。
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _saveTimer.Stop();
            _saveTimer.Tick -= OnSaveTick;
            Config.PropertyChanged -= OnConfigPropertyChanged;

            SaveNow();
        }
    }
}
