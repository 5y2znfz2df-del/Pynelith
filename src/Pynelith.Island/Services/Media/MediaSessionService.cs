using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media;
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
        /// <summary>
        /// 与播放器时间轴比对时的容差：超过该偏差立即校正。
        /// 取 80ms，保证歌词行切换与音频的误差稳定在 100ms 以内。
        /// </summary>
        private static readonly TimeSpan TimelineTolerance = TimeSpan.FromMilliseconds(80);

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _windowTimer;
        private readonly PlayerWindowWatcher _windowWatcher;
        private TimeSpan _positionBase;
        private DateTime _positionStampUtc = DateTime.UtcNow;
        private TimeSpan _lastSmtcPosition = TimeSpan.MinValue;
        private DateTimeOffset _lastTimelineUpdated = DateTimeOffset.MinValue;
        private bool _timelineReliable;
        private bool _isPlaying;
        private bool _disposed;

        // 不上报进度的播放器（网易云）：用窗口标题变化作为更准的"换歌 0 点"信号。
        private PlayerProfile _profile = PlayerProfiles.Unknown;
        private string _assistedTrackKey = string.Empty;
        private DateTime _lastTrackChangeUtc = DateTime.MinValue;
        private int _progressOffsetMs;

        // 跳转后的保护窗口：部分播放器在 Seek 完成后仍会回报旧位置，
        // 若立即采信，进度条会先跳到目标再弹回原处。保护窗口内忽略时间轴校正。
        private DateTime _seekGuardUntilUtc = DateTime.MinValue;

        /// <summary>任何状态变化（会话切换、曲目、播放状态、时间轴、封面）。</summary>
        public event EventHandler? StateChanged;

        public bool HasSession { get; private set; }
        public string Title { get; private set; } = string.Empty;
        public string Artist { get; private set; } = string.Empty;
        public BitmapImage? Artwork { get; private set; }
        public bool IsPlaying => _isPlaying;
        public TimeSpan Duration { get; private set; }
        public string SourceApp { get; private set; } = string.Empty;

        /// <summary>当前播放器的画像（用于判断它是否上报进度）。</summary>
        public PlayerProfile Profile => _profile;

        /// <summary>
        /// 当前播放位置是否可信。
        /// 播放器上报了真实进度时为 true；否则说明位置是推算值，可能存在偏差。
        /// </summary>
        public bool IsPositionEstimated => !_profile.ReportsProgress || !_timelineReliable;

        /// <summary>当前播放器是否开启随机播放。</summary>
        public bool IsShuffleActive { get; private set; }

        /// <summary>当前循环模式。</summary>
        public MediaRepeatMode RepeatMode { get; private set; }

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

                // 进度对齐预设：把推算值整体平移，修正不同音频版本之间的固定偏差。
                pos += TimeSpan.FromMilliseconds(_progressOffsetMs);

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

        /// <summary>
        /// 套用进度对齐预设。
        /// 传入的预设 id 为空表示回到纯推算进度（偏移归零）。
        /// </summary>
        public void ApplyOffsetPreset(string? presetId)
        {
            var preset = string.IsNullOrWhiteSpace(presetId)
                ? null
                : PlayerOffsetPresets.Find(presetId);

            _progressOffsetMs = preset?.ProgressOffsetMs ?? 0;
        }

        public MediaSessionService()
        {
            _windowWatcher = new PlayerWindowWatcher();
            _windowWatcher.TitleChanged += OnWindowTitleChanged;

            _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _positionTimer.Tick += (_, _) =>
            {
                // 心跳校验：部分播放器退出时既不触发 SessionsChanged，也不回报 Closed，
                // 只能靠"会话是否还在系统列表里"来判断，否则暂停态界面会永久残留。
                if (!IsSessionStillRegistered())
                {
                    Rescan();
                    return;
                }

                RefreshTimelineIfPlaying();
            };

            // 窗口标题轮询独立于位置心跳：只有需要辅助同步的播放器才会启动它，
            // 而且轮询频率可以更高（标题变化的时间点才估得准）。
            _windowTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _windowTimer.Tick += (_, _) => PollWindowAssist();
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

            // 没有任何会话：说明播放器已退出（网易云等退出后 SMTC 会话会被系统移除），
            // 必须主动清空曲目/封面/播放状态，否则岛会一直停在"已暂停"的媒体界面。
            if (best == null)
            {
                if (_session != null)
                {
                    SwitchTo(null);
                }
                else
                {
                    UpdatePlaybackInfo();
                }

                return;
            }

            // 同一会话不切换：WinRT 每次返回的包装对象可能不同实例，
            // 必须按 AppId 判等，否则暂停时会误走 SwitchTo 把播放位置重置为 0。
            if (IsSameSession(best, _session))
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

        /// <summary>当前会话是否仍注册在系统会话列表里（播放器退出后会被移除）。</summary>
        private bool IsSessionStillRegistered()
        {
            try
            {
                if (_manager == null || _session == null)
                {
                    // 管理器或会话尚未就绪时不做判断，避免误清空。
                    return true;
                }

                return _manager.GetSessions().Any(s => IsSameSession(s, _session));
            }
            catch (Exception)
            {
                return true;
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
                IsShuffleActive = false;
                RepeatMode = MediaRepeatMode.None;
                _positionBase = TimeSpan.Zero;
                _positionStampUtc = DateTime.UtcNow;
                _profile = PlayerProfiles.Unknown;
                _assistedTrackKey = string.Empty;
                _windowWatcher.Detach();
                _windowTimer.Stop();
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

            // 识别播放器画像：决定时间轴能不能采信，以及要不要启用窗口标题辅助同步。
            _profile = PlayerProfiles.FromAumid(SourceApp);

            // 切歌后位置归零，避免沿用上一首的时间轴导致歌词错位
            _positionBase = TimeSpan.Zero;
            _positionStampUtc = DateTime.UtcNow;
            _lastSmtcPosition = TimeSpan.MinValue;
            _timelineReliable = false;
            _assistedTrackKey = string.Empty;
            _lastTrackChangeUtc = DateTime.MinValue;

            // 不上报进度的播放器：用窗口标题变化捕捉更准的"换歌 0 点"。
            if (PlayerProfiles.NeedsWindowAssist(_profile))
            {
                AttachWindowWatcher();
            }
            else
            {
                _windowWatcher.Detach();
                _windowTimer.Stop();
            }

            // 新会话先清空旧的随机/循环状态，随后由 UpdatePlaybackInfo 按真实状态覆盖
            IsShuffleActive = false;
            RepeatMode = MediaRepeatMode.None;

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

        #region 换歌检测与窗口标题辅助同步

        /// <summary>
        /// 换歌处理：重置播放位置基准。
        ///
        /// 上报进度的播放器：位置会被时间轴校正，这里的重置只是让歌词立即回到第一行。
        /// 不上报进度的播放器（网易云）：「曲目元数据变化」的时刻只能近似当��新歌的 0 点，
        /// 真正的 0 点由窗口标题变化来捕捉（见 <see cref="OnWindowTitleChanged"/>）。
        /// </summary>
        private void HandleTrackChanged()
        {
            _positionBase = TimeSpan.Zero;
            _positionStampUtc = DateTime.UtcNow;
            _lastSmtcPosition = TimeSpan.MinValue;
            _lastTrackChangeUtc = DateTime.UtcNow;

            // 让窗口标题监听器忘掉旧标题：下次读到同一个标题时不会被误判成"又换歌了"。
            _assistedTrackKey = string.Empty;
        }

        /// <summary>把窗口标题监听器绑到播放器进程上。</summary>
        private void AttachWindowWatcher()
        {
            uint processId = ResolveProcessId(_profile);
            if (processId == 0)
            {
                return;
            }

            _windowWatcher.Attach(processId);
            _windowTimer.Start();
        }

        /// <summary>按画像里的进程名关键字找到播放器进程。</summary>
        private static uint ResolveProcessId(PlayerProfile profile)
        {
            if (profile.ProcessKeywords.Length == 0)
            {
                return 0;
            }

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string name = process.ProcessName.ToLowerInvariant();
                        foreach (var keyword in profile.ProcessKeywords)
                        {
                            if (name.Contains(keyword, StringComparison.Ordinal))
                            {
                                return (uint)process.Id;
                            }
                        }
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // 枚举进程失败（权限等）：放弃窗口辅助，退回纯 SMTC 推算。
            }

            return 0;
        }

        /// <summary>轮询窗口标题（只在需要辅助同步的播放器上才有开销）。</summary>
        private void PollWindowAssist()
        {
            if (_disposed || !PlayerProfiles.NeedsWindowAssist(_profile))
            {
                return;
            }

            _windowWatcher.Poll();
        }

        /// <summary>
        /// 窗口标题变化 = 换歌了。
        ///
        /// 这是网易云这类不上报进度的播放器上最准的 0 点信号：
        /// 标题在音频真正开始播放时就已经更新，比 SMTC 元数据事件早几百毫秒。
        /// </summary>
        private void OnWindowTitleChanged(string windowTitle, DateTime changedAtUtc)
        {
            if (_disposed || !_isPlaying)
            {
                return;
            }

            var (title, artist) = PlayerWindowWatcher.SplitTitle(windowTitle, _profile.TitleSeparator);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            // 标题里常常带着播放器的固定前后缀（如"网易云音乐"），
            // 只有歌名确实变了才算换歌，否则每次刷新都会把位置重置。
            string key = title + "|" + artist;
            if (string.Equals(key, _assistedTrackKey, StringComparison.Ordinal))
            {
                return;
            }

            _assistedTrackKey = key;

            // 与 SMTC 曲名不一致时不做处理：说明窗口标题不是当前曲目（可能是别的窗口）。
            if (!string.IsNullOrWhiteSpace(Title)
                && !string.IsNullOrWhiteSpace(title)
                && !ContainsNormalized(title, Title)
                && !ContainsNormalized(Title, title))
            {
                return;
            }

            // 换歌：把播放位置基准归零。
            // 基准戳用「标题变化时刻」而非「现在」——标题变化比元数据事件早，
            // 这样歌词能从真正的音频起点开始，而不是从元数据到达时开始。
            // 再减去该播放器的元数据延迟（网易云约 350ms），补偿"标题先变、音频后起"的差。
            _positionBase = TimeSpan.Zero;
            _positionStampUtc = changedAtUtc.AddMilliseconds(-_profile.MetadataLagMs);
            _lastSmtcPosition = TimeSpan.MinValue;
            _lastTrackChangeUtc = DateTime.UtcNow;
            Raise();
        }

        /// <summary>归一化后判断两个标题是否指向同一首歌（忽略大小写、空格与标点差异）。</summary>
        private static bool ContainsNormalized(string haystack, string needle)
        {
            return Normalize(haystack).Contains(Normalize(needle), StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        #endregion

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

                string newTitle = props?.Title ?? string.Empty;
                string newArtist = props?.Artist ?? string.Empty;
                bool trackChanged = !string.Equals(newTitle, Title, StringComparison.Ordinal)
                    || !string.Equals(newArtist, Artist, StringComparison.Ordinal);

                Title = newTitle;
                Artist = newArtist;

                if (trackChanged)
                {
                    HandleTrackChanged();
                }

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
            string status = string.Empty;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
            try
            {
                info = _session?.GetPlaybackInfo();
                status = info?.PlaybackStatus.ToString() ?? string.Empty;
                playing = IsPlayingStatus(info);
            }
            catch (Exception)
            {
                playing = false;
            }

            try
            {
                if (info != null)
                {
                    IsShuffleActive = info.IsShuffleActive ?? false;
                    RepeatMode = info.AutoRepeatMode switch
                    {
                        MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.One,
                        MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.All,
                        _ => MediaRepeatMode.None,
                    };
                }
            }
            catch (Exception)
            {
                // 部分播放器不实现随机/循环状态，保持旧值。
            }

            // 播放器退出后，系统偶尔仍保留一个状态为 Closed 的会话：
            // 这时必须按"无会话"处理，否则岛会残留上一首的封面和暂停图标。
            if (string.Equals(status, "Closed", StringComparison.Ordinal))
            {
                SwitchTo(null);
                return;
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
                    ApplyTimeline(timeline);
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
                    ApplyTimeline(timeline);
                }
            }
            catch (Exception)
            {
                // 忽略瞬时失败，下一拍再试。
            }
        }

        /// <summary>
        /// 采用播放器上报的时间轴位置。
        /// 关键点：<c>LastUpdatedTime</c> 是播放器采样该 Position 的时刻，
        /// 用它把位置外推到"此刻"，即使播放器每秒才上报一次，也能得到毫秒级精度。
        /// 位置完全没变（部分播放器时间轴长期停在 0）时一律不校正，继续用单调时钟推算。
        /// </summary>
        private void ApplyTimeline(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
        {
            TimeSpan smtcPosition = timeline.Position;
            DateTimeOffset updatedAt = timeline.LastUpdatedTime;

            bool positionChanged = smtcPosition != _lastSmtcPosition;
            bool stampChanged = updatedAt != _lastTimelineUpdated;

            _lastSmtcPosition = smtcPosition;
            _lastTimelineUpdated = updatedAt;

            if (smtcPosition > TimeSpan.Zero)
            {
                // 出现过非零位置，说明这个播放器确实在上报进度
                _timelineReliable = true;
            }

            // 刚提交过 Seek：播放器可能还在回报跳转前的位置，这段时间不做校正
            if (DateTime.UtcNow < _seekGuardUntilUtc)
            {
                return;
            }

            if (!positionChanged && !stampChanged)
            {
                return;
            }

            if (!positionChanged)
            {
                // 位置没变（可能只是时间戳刷新）：不做校正，避免把推算值拉回停滞的 0
                return;
            }

            if (!_isPlaying)
            {
                // 暂停期间不接受校正：有的播放器暂停时把时间轴清零，
                // 若照单全收，恢复播放后歌词会从第一句重新滚。
                return;
            }

            TimeSpan effective = smtcPosition;
            if (updatedAt != DateTimeOffset.MinValue)
            {
                TimeSpan age = DateTimeOffset.Now - updatedAt;
                if (age > TimeSpan.Zero && age < TimeSpan.FromSeconds(5))
                {
                    effective += age;
                }
            }

            if (effective < TimeSpan.Zero)
            {
                effective = TimeSpan.Zero;
            }

            // 偏差超过容差才校正：换歌 / 拖动进度会立刻对齐，正常播放时不会来回拉扯
            if ((effective - Position).Duration() > TimelineTolerance)
            {
                _positionBase = effective;
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

        /// <summary>跳转到指定播放位置（进度条拖拽）。</summary>
        public async Task<bool> SeekAsync(TimeSpan position)
        {
            var session = _session;
            if (session == null || _disposed)
            {
                return false;
            }

            try
            {
                // SMTC 的位置单位是 100ns
                long ticks = Math.Max(0, (long)(position.TotalMilliseconds * 10000.0));
                bool ok = await session.TryChangePlaybackPositionAsync(ticks);
                if (ok)
                {
                    _positionBase = position;
                    _positionStampUtc = DateTime.UtcNow;
                    _seekGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
                    Raise();
                }

                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>切换随机播放。</summary>
        public async Task<bool> ToggleShuffleAsync()
        {
            var session = _session;
            if (session == null || _disposed)
            {
                return false;
            }

            try
            {
                bool target = !IsShuffleActive;
                bool ok = await session.TryChangeShuffleActiveAsync(target);
                if (ok)
                {
                    IsShuffleActive = target;
                    Raise();
                }

                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>循环切换：关闭 → 列表循环 → 单曲循环。</summary>
        public async Task<bool> CycleRepeatAsync()
        {
            var session = _session;
            if (session == null || _disposed)
            {
                return false;
            }

            try
            {
                MediaRepeatMode next = RepeatMode switch
                {
                    MediaRepeatMode.None => MediaRepeatMode.All,
                    MediaRepeatMode.All => MediaRepeatMode.One,
                    _ => MediaRepeatMode.None,
                };

                MediaPlaybackAutoRepeatMode mode = next switch
                {
                    MediaRepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                    MediaRepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                    _ => MediaPlaybackAutoRepeatMode.None,
                };

                bool ok = await session.TryChangeAutoRepeatModeAsync(mode);
                if (ok)
                {
                    RepeatMode = next;
                    Raise();
                }

                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

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
            _windowTimer.Stop();
            _windowWatcher.TitleChanged -= OnWindowTitleChanged;
            _windowWatcher.Dispose();

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

    /// <summary>循环模式（对应 SMTC 的 MediaPlaybackAutoRepeatMode）。</summary>
    public enum MediaRepeatMode
    {
        None,
        All,
        One,
    }
}
