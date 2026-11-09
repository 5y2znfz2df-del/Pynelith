using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Pynelith.Island.Services.Media
{
    /// <summary>
    /// SMTC 全局媒体会话服务。
    /// 基于 Windows 系统媒体传输控件（System Media Transport Controls）的全局会话管理器：
    /// 自动扫描系统中所有注册了 SMTC 的媒体会话，优先跟随"正在播放"的会话，
    /// 暴露曲目元数据、专辑封面、播放位置，并提供播放/暂停、上一首/下一首控制。
    /// 所有对外事件都在 UI 线程触发。
    /// </summary>
    public sealed class MediaSessionService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly DispatcherTimer _positionTimer;
        private TimeSpan _positionBase;
        private bool _disposed;

        /// <summary>任何状态变化（会话切换、曲目、播放状态、时间轴、封面）。</summary>
        public event EventHandler? StateChanged;

        public bool HasSession { get; private set; }
        public string Title { get; private set; } = string.Empty;
        public string Artist { get; private set; } = string.Empty;
        public BitmapImage? Artwork { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan Duration { get; private set; }
        public string SourceApp { get; private set; } = string.Empty;

        /// <summary>当前播放位置（播放中按最近一次时间轴快照推算）。</summary>
        public TimeSpan Position
        {
            get
            {
                TimeSpan pos = _positionBase;
                if (Duration > TimeSpan.Zero && pos > Duration)
                {
                    pos = Duration;
                }

                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
        }

        public MediaSessionService()
        {
            _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _positionTimer.Tick += (_, _) => RefreshTimelineIfPlaying();
        }

        /// <summary>请求系统全局会话管理器并开始自动扫描。</summary>
        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (_, _) => OnUi(Rescan);
                _manager.SessionsChanged += (_, _) => OnUi(Rescan);
                Rescan();
                _positionTimer.Start();
            }
            catch (Exception)
            {
                // 系统媒体会话不可用（极少见）：保持无会话状态，岛显示空闲态。
                HasSession = false;
                Raise();
            }
        }

        #region 自动扫描与会话切换

        /// <summary>扫描全部会话：优先跟随正在播放的，其次系统当前会话。</summary>
        private void Rescan()
        {
            if (_manager == null || _disposed)
            {
                return;
            }

            GlobalSystemMediaTransportControlsSession? best = null;

            try
            {
                var sessions = _manager.GetSessions();
                best = sessions.FirstOrDefault(IsActivelyPlaying) ?? _manager.GetCurrentSession();
            }
            catch (Exception)
            {
                best = null;
            }

            if (best == _session)
            {
                UpdatePlaybackInfo();
                return;
            }

            SwitchTo(best);
        }

        private static bool IsActivelyPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return IsPlayingStatus(session.GetPlaybackInfo());
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 投影中播放状态枚举类型名随 SDK 版本变化，用字符串比较保持稳定。
        private static bool IsPlayingStatus(GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
        {
            if (info == null)
            {
                return false;
            }

            try
            {
                return info.PlaybackStatus.ToString() == "Playing";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private async void SwitchTo(GlobalSystemMediaTransportControlsSession? next)
        {
            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnSessionMediaChanged;
                    _session.PlaybackInfoChanged -= OnSessionPlaybackChanged;
                    _session.TimelinePropertiesChanged -= OnSessionTimelineChanged;
                }
                catch (Exception)
                {
                    // 会话可能已被系统关闭，忽略解绑失败。
                }
            }

            _session = next;
            HasSession = next != null;

            if (next == null)
            {
                Title = string.Empty;
                Artist = string.Empty;
                SourceApp = string.Empty;
                Artwork = null;
                IsPlaying = false;
                Duration = TimeSpan.Zero;
                _positionBase = TimeSpan.Zero;
                Raise();
                return;
            }

            next.MediaPropertiesChanged += OnSessionMediaChanged;
            next.PlaybackInfoChanged += OnSessionPlaybackChanged;
            next.TimelinePropertiesChanged += OnSessionTimelineChanged;

            try
            {
                SourceApp = next.SourceAppUserModelId ?? string.Empty;
            }
            catch (Exception)
            {
                SourceApp = string.Empty;
            }

            UpdatePlaybackInfo();
            UpdateTimeline();
            await RefreshMediaPropertiesAsync(next);
        }

        private void OnSessionMediaChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => OnUi(() => _ = RefreshMediaPropertiesAsync(sender));

        private void OnSessionPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => OnUi(() =>
            {
                UpdatePlaybackInfo();
                // 当前会话停住而别的会话开播：自动跟随过去。
                if (!IsPlaying)
                {
                    Rescan();
                }
            });

        private void OnSessionTimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args)
            => OnUi(UpdateTimeline);

        #endregion

        #region 状态刷新

        private async Task RefreshMediaPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (_disposed || !ReferenceEquals(_session, session))
                {
                    return;
                }

                Title = props?.Title ?? string.Empty;
                Artist = props?.Artist ?? string.Empty;
                Raise();

                var thumbnail = props?.Thumbnail;
                if (thumbnail != null)
                {
                    var artwork = await LoadArtworkAsync(thumbnail);
                    if (_disposed || !ReferenceEquals(_session, session))
                    {
                        return;
                    }

                    if (artwork != null)
                    {
                        Artwork = artwork;
                        Raise();
                    }
                }
                else if (Artwork != null)
                {
                    Artwork = null;
                    Raise();
                }
            }
            catch (Exception)
            {
                // 元数据拉取失败不影响播放控制。
            }
        }

        private static async Task<BitmapImage?> LoadArtworkAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
        {
            try
            {
                using var stream = await reference.OpenReadAsync();
                uint size = (uint)stream.Size;
                if (size == 0 || size > 16 * 1024 * 1024)
                {
                    return null;
                }

                var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
                using (reader)
                {
                    await reader.LoadAsync(size);
                    var bytes = new byte[size];
                    reader.ReadBytes(bytes);

                    var bitmap = new BitmapImage();
                    using var memory = new MemoryStream(bytes);
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 160;
                    bitmap.StreamSource = memory;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void UpdatePlaybackInfo()
        {
            try
            {
                IsPlaying = IsPlayingStatus(_session?.GetPlaybackInfo());
            }
            catch (Exception)
            {
                IsPlaying = false;
            }

            Raise();
        }

        private void UpdateTimeline()
        {
            try
            {
                var timeline = _session?.GetTimelineProperties();
                if (timeline != null)
                {
                    _positionBase = timeline.Position;
                    Duration = timeline.EndTime > timeline.StartTime
                        ? timeline.EndTime - timeline.StartTime
                        : TimeSpan.Zero;
                }
            }
            catch (Exception)
            {
                // 时间轴不可用时保持旧值。
            }

            Raise();
        }

        private void RefreshTimelineIfPlaying()
        {
            if (_disposed || _session == null || !IsPlaying)
            {
                return;
            }

            try
            {
                var timeline = _session.GetTimelineProperties();
                if (timeline != null)
                {
                    _positionBase = timeline.Position;
                }
            }
            catch (Exception)
            {
                // 忽略瞬时失败，下一拍再试。
            }
        }

        #endregion

        #region SMTC 播放控制

        /// <summary>播放/暂停切换：投影未提供 TryPlayPauseAsync，按当前状态分发。</summary>
        public async Task<bool> TogglePlayPauseAsync()
        {
            var session = _session;
            if (session == null || _disposed)
            {
                return false;
            }

            try
            {
                return IsPlaying
                    ? await session.TryPauseAsync()
                    : await session.TryPlayAsync();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public Task<bool> SkipNextAsync() => RunAsync(s => s.TrySkipNextAsync());

        public Task<bool> SkipPreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync());

        private async Task<bool> RunAsync(
            Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
        {
            try
            {
                var session = _session;
                if (session == null || _disposed)
                {
                    return false;
                }

                return await action(session);
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        private void OnUi(Action action)
        {
            if (_disposed)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        private void Raise()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _positionTimer.Stop();

            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnSessionMediaChanged;
                    _session.PlaybackInfoChanged -= OnSessionPlaybackChanged;
                    _session.TimelinePropertiesChanged -= OnSessionTimelineChanged;
                }
                catch (Exception)
                {
                }
            }

            _session = null;
            _manager = null;
        }
    }
}
