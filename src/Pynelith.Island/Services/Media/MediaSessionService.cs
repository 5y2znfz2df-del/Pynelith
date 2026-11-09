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
        /// <summary>与播放器时间轴比对时的容差：小于该偏差视为推算更可靠。</summary>
        private static readonly TimeSpan TimelineTolerance = TimeSpan.FromSeconds(1.5);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly DispatcherTimer _positionTimer;
        private TimeSpan _positionBase;
        private DateTime _positionStampUtc = DateTime.UtcNow;
        private TimeSpan _lastSmtcPosition = TimeSpan.MinValue;
        private bool _isPlaying;
        private bool _disposed;

        /// <summary>任何状态变化（会话切换、曲目、播放状态、时间轴、封面）。</summary>
        public event EventHandler? StateChanged;

        public bool HasSession { get; private set; }
        public string Title { get; private set; } = string.Empty;
        public string Artist { get; private set; } = string.Empty;
        public BitmapImage? Artwork { get; private set; }
        public bool IsPlaying => _isPlaying;
        public TimeSpan Duration { get; private set; }
        public string SourceApp { get; private set; } = string.Empty;

        /// <summary>
        /// 当前播放位置：以最近一次时间轴快照为基线，播放中按经过的真实时间推算，
        /// 保证歌词逐行推进（部分播放器的 SMTC 时间轴不会持续刷新）。
        /// </summary>
        public TimeSpan Position
        {
            get
            {
                TimeSpan pos = _positionBase;
                if (_isPlaying)
                {
                    pos += DateTime.UtcNow - _positionStampUtc;
                }

                if (pos < TimeSpan.Zero)
                {
                    pos = TimeSpan.Zero;
                }

                if (Duration > TimeSpan.Zero && pos > Duration)
                {
                    pos = Duration;
                }

                return pos;
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

            // 同一会话不切换：WinRT 每次返回的包装对象可能不同实例，
            // 必须按 AppId 判等，否则暂停时会误走 SwitchTo 把播放位置重置为 0。
            if (best == null || IsSameSession(best, _session))
            {
                UpdatePlaybackInfo();
                return;
            }

            SwitchTo(best);
        }

        /// <summary>两个会话是否指向同一个播放器/同一路媒体。</summary>
        private static bool IsSameSession(
            GlobalSystemMediaTransportControlsSession a,
            GlobalSystemMediaTransportControlsSession? b)
        {
            if (b == null)
            {
                return false;
            }

            if (ReferenceEquals(a, b))
            {
                return true;
            }

            try
            {
                string idA = a.SourceAppUserModelId ?? string.Empty;
                string idB = b.SourceAppUserModelId ?? string.Empty;
                return idA.Length > 0 && string.Equals(idA, idB, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
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
                _isPlaying = false;
                Duration = TimeSpan.Zero;
                _positionBase = TimeSpan.Zero;
                _positionStampUtc = DateTime.UtcNow;
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

            // 切歌后位置归零，避免沿用上一首的时间轴导致歌词错位
            _positionBase = TimeSpan.Zero;
            _positionStampUtc = DateTime.UtcNow;
            _lastSmtcPosition = TimeSpan.MinValue;

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

                // 当前会话暂停/停止时不要盲目重新扫描：只有确实存在"另一个正在播放"
                // 的会话才跟随过去，否则会重置位置基准，表现为歌词暂停后恢复时重头滚。
                if (!IsPlaying && HasOtherPlayingSession())
                {
                    Rescan();
                }
            });

        private bool HasOtherPlayingSession()
        {
            try
            {
                return _manager != null
                    && _manager.GetSessions().Any(s => !IsSameSession(s, _session) && IsActivelyPlaying(s));
            }
            catch (Exception)
            {
                return false;
            }
        }

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
            bool playing;
            try
            {
                playing = IsPlayingStatus(_session?.GetPlaybackInfo());
            }
            catch (Exception)
            {
                playing = false;
            }

            if (playing != _isPlaying)
            {
                // 结算到此刻：暂停后不再继续推进，恢复播放时从新基线起算
                if (!playing)
                {
                    _positionBase = Position;
                }

                _isPlaying = playing;
                _positionStampUtc = DateTime.UtcNow;
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
                    ApplyTimeline(timeline.Position);
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
            if (_disposed || _session == null || !_isPlaying)
            {
                return;
            }

            try
            {
                var timeline = _session.GetTimelineProperties();
                if (timeline != null)
                {
                    ApplyTimeline(timeline.Position);
                }
            }
            catch (Exception)
            {
                // 忽略瞬时失败，下一拍再试。
            }
        }

        /// <summary>
        /// 采用播放器上报的时间轴位置：只在播放器确实更新了数值（换歌/拖动进度）且偏差
        /// 超过容差时校正。部分播放器的 SMTC 时间轴长期停在起点，若无条件采用会把
        /// 歌词一直拉回第一行，因此停滞的时间轴必须忽略。
        /// </summary>
        private void ApplyTimeline(TimeSpan smtcPosition)
        {
            bool changed = smtcPosition != _lastSmtcPosition;
            bool advanced = smtcPosition > _lastSmtcPosition;
            _lastSmtcPosition = smtcPosition;

            if (!changed)
            {
                // 时间轴停滞（部分播放器只给初始值）：继续用时间推算保证歌词推进
                return;
            }

            if (!_isPlaying)
            {
                // 暂停期间不接受校正：有的播放器暂停时把时间轴清零，
                // 若照单全收，恢复播放后歌词会从第一句重新滚。
                return;
            }

            // 播放器在推进时以它为准（比推算更准）；回退/拖动则要求偏差足够大才采用
            if (advanced || (smtcPosition - Position).Duration() > TimelineTolerance)
            {
                _positionBase = smtcPosition;
                _positionStampUtc = DateTime.UtcNow;
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
