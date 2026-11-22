using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// Siri 组件的门面：把配置、麦克风、离线唤醒与 AI 调用编排成一个状态机。
    ///
    /// 与灵动岛核心完全解耦：
    /// - 组件总开关关闭时不加载模型、不开麦、不监听快捷键；
    /// - 所有对外交互都通过 <see cref="SnapshotChanged"/> 事件（UI 线程触发）。
    /// </summary>
    public sealed class SiriService : IDisposable
    {
        private readonly SiriConfig _config;
        private readonly SiriChatStore _history;
        private readonly SiriApiClient _api;
        private readonly AudioCaptureService _audio;
        private readonly WakeWordRecognizer _wake;
        private readonly SiriSpeaker _speaker = new SiriSpeaker();

        private CancellationTokenSource? _conversationCts;
        private IntPtr _hotkeyHwnd;
        private bool _hotkeyRegistered;
        private bool _disposed;

        // 手动唤起（快捷键 / 点光点）后的自动收尾：说到没人声就结束并转写
        private System.Windows.Threading.DispatcherTimer? _listenTimer;
        private DateTime _listenStartedUtc;
        private DateTime _lastVoiceUtc;
        private bool _heardVoice;
        private bool _captureOpenedByCommand;
        private bool _finishing;

        /// <summary>当前快照（UI 层读取）。</summary>
        public SiriSnapshot Snapshot { get; private set; } = new SiriSnapshot();

        /// <summary>状态变化通知（UI 线程）。</summary>
        public event EventHandler? SnapshotChanged;

        /// <summary>组件是否已启用。</summary>
        public bool IsEnabled => _config.Enabled;

        /// <summary>语音唤醒是否可用（已启用 + 允许语音 + 模型就绪）。</summary>
        public bool IsVoiceWakeReady { get; private set; }

        /// <summary>快捷键是否已成功注册（设置页展示）。</summary>
        public bool IsHotkeyRegistered => _hotkeyRegistered;

        /// <summary>快捷键状态说明（设置页展示，便于排查为什么按了没反应）。</summary>
        public string HotkeyStatus { get; private set; } = "待注册";

        /// <summary>组件配置（设置面板读写）。</summary>
        public SiriConfig Config => _config;

        /// <summary>对话历史。</summary>
        public SiriChatStore History => _history;

        /// <summary>回复播报器（设置页试听直接用它）。</summary>
        public SiriSpeaker Speaker => _speaker;

        /// <summary>用当前音色试听一句（设置页使用）。</summary>
        public void PreviewSpeech(string text)
            => _speaker.Speak(text, _config.SpeechVoice, _config.SpeechRate);

        public SiriService(SiriConfig config)
        {
            _config = config;
            _history = new SiriChatStore();
            _api = new SiriApiClient();
            _audio = new AudioCaptureService();
            _wake = new WakeWordRecognizer();

            _history.Load();
        }

        /// <summary>启动组件：加载唤醒模型、开麦（快捷键由宿主窗口在句柄就绪后注册）。</summary>
        public async Task StartAsync()
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            if (_config.VoiceWakeEnabled)
            {
                await EnsureWakeModelAsync();
            }
        }

        /// <summary>
        /// 在宿主窗口句柄上注册全局快捷键。
        /// WM_HOTKEY 由宿主消息循环派发给 <see cref="HotkeyNative.WM_HOTKEY"/> 的处理者。
        /// </summary>
        public bool RegisterHotkey(IntPtr hwnd)
        {
            ReleaseHotkey();

            if (_disposed || !_config.Enabled)
            {
                HotkeyStatus = "组件已关闭";
                return false;
            }

            if (!_config.HotkeyEnabled)
            {
                HotkeyStatus = "未启用";
                return false;
            }

            HotkeySpec? spec = HotkeyParser.Parse(_config.Hotkey);
            if (spec == null)
            {
                HotkeyStatus = $"无法识别：{_config.Hotkey}（示例：Ctrl+Alt+S）";
                return false;
            }

            if (!HotkeyNative.Register(hwnd, spec.Value))
            {
                HotkeyStatus = HotkeyNative.LastError == 1409
                    ? $"{_config.Hotkey} 已被其它程序占用，换一个组合"
                    : $"{_config.Hotkey} 注册失败（错误码 {HotkeyNative.LastError}）";
                return false;
            }

            _hotkeyHwnd = hwnd;
            _hotkeyRegistered = true;
            HotkeyStatus = $"已注册：{_config.Hotkey}";
            return true;
        }

        /// <summary>注销全局快捷键。</summary>
        public void ReleaseHotkey()
        {
            if (!_hotkeyRegistered)
            {
                return;
            }

            HotkeyNative.Unregister(_hotkeyHwnd);
            _hotkeyHwnd = IntPtr.Zero;
            _hotkeyRegistered = false;
            HotkeyStatus = "已注销";
        }

        /// <summary>确保离线唤醒模型可用（必要时下载）。</summary>
        public async Task<bool> EnsureWakeModelAsync()
        {
            try
            {
                bool ok = await _wake.EnsureModelAsync();
                IsVoiceWakeReady = ok;

                if (ok && _config.Enabled && _config.VoiceWakeEnabled && !_audio.IsCapturing)
                {
                    StartListening();
                }

                return ok;
            }
            catch (Exception)
            {
                IsVoiceWakeReady = false;
                return false;
            }
        }

        /// <summary>开始后台收音与唤醒识别。</summary>
        public void StartListening()
        {
            if (_disposed || !_config.Enabled || !_config.VoiceWakeEnabled)
            {
                return;
            }

            if (!_wake.IsReady)
            {
                return;
            }

            if (_audio.IsCapturing)
            {
                return;
            }

            try
            {
                _audio.Start(_config.MicrophoneDevice, OnAudioData);
            }
            catch (Exception)
            {
                // 麦克风被占用 / 设备不存在：保持待机，等用户改设置或用快捷键唤起。
            }
        }

        /// <summary>停止后台收音。</summary>
        public void StopListening()
        {
            _audio.Stop();
        }

        /// <summary>后台音频回调（采集线程）。</summary>
        private void OnAudioData(byte[] data)
        {
            try
            {
                // 正在为本次指令录音时不再跑唤醒识别，只统计人声能量用于自动收尾
                if (Snapshot.State is SiriState.Listening or SiriState.Transcribing)
                {
                    TrackVoiceLevel(data);
                    return;
                }

                string text = _wake.AcceptSamples(data);
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                if (!WakeWordRecognizer.IsWakeWord(text, _config.WakeWord, _config.WakeSensitivity))
                {
                    return;
                }

                // 命中唤醒词：切到 UI 线程处理
                _wake.Reset();
                Post(() => BeginListening());
            }
            catch (Exception)
            {
                // 识别异常不应中断收音。
            }
        }

        /// <summary>手动唤起（快捷键 / 点击胶囊 / 文字输入）。</summary>
        public void BeginListening()
        {
            if (_disposed || !_config.Enabled)
            {
                return;
            }

            // 开始听用户说话：先掐掉上一条回复的播报，避免被自己声音打断唤醒/录音
            _speaker.Stop();

            // 已经有人在说话时不重复触发
            if (Snapshot.State is SiriState.Listening or SiriState.Transcribing)
            {
                return;
            }

            // 关闭了后台语音唤醒时，这里临时开麦录这一句（说完自动关）
            _captureOpenedByCommand = false;
            if (!_audio.IsCapturing)
            {
                try
                {
                    _audio.Start(_config.MicrophoneDevice, OnAudioData);
                    _captureOpenedByCommand = true;
                }
                catch (Exception)
                {
                    // 麦克风不可用：仍然进入收音态，界面会提示没听到声音
                }
            }

            _wake.Reset();
            _audio.ClearBuffer();
            _listenStartedUtc = DateTime.UtcNow;
            _lastVoiceUtc = _listenStartedUtc;
            _heardVoice = false;

            Update(new SiriSnapshot { State = SiriState.Listening });
            StartListenTimer();
        }

        /// <summary>结束本次收音并转写、提交（快捷键再按一次 / 点停止 / 静音超时）。</summary>
        public async Task FinishListeningAsync()
        {
            if (_disposed || _finishing)
            {
                return;
            }

            if (Snapshot.State is not (SiriState.Listening or SiriState.Transcribing))
            {
                return;
            }

            _finishing = true;
            try
            {
                _listenTimer?.Stop();
                await TranscribeAndSendAsync();
            }
            finally
            {
                // 这一句是为了快捷键临时开的麦，用完就关
                if (_captureOpenedByCommand && !_config.VoiceWakeEnabled)
                {
                    _audio.Stop();
                }

                _captureOpenedByCommand = false;
                _finishing = false;
            }
        }

        private void StartListenTimer()
        {
            if (_listenTimer == null)
            {
                _listenTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Normal,
                    System.Windows.Threading.Dispatcher.CurrentDispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(120),
                };
                _listenTimer.Tick += OnListenTick;
            }

            _listenTimer.Start();
        }

        /// <summary>自动收尾：说过话后静音 1.2 秒视为说完；一直没声音 4 秒后结束。</summary>
        private void OnListenTick(object? sender, EventArgs e)
        {
            if (_disposed || Snapshot.State != SiriState.Listening)
            {
                _listenTimer?.Stop();
                return;
            }

            double elapsed = (DateTime.UtcNow - _listenStartedUtc).TotalSeconds;
            double silence = (DateTime.UtcNow - _lastVoiceUtc).TotalSeconds;

            if (!_heardVoice && elapsed > 4.0)
            {
                _ = FinishListeningAsync();
                return;
            }

            if (_heardVoice && (silence > 1.2 || elapsed > 15.0))
            {
                _ = FinishListeningAsync();
            }
        }

        /// <summary>统计 16bit PCM 的能量，判断用户是否在说话。</summary>
        private void TrackVoiceLevel(byte[] data)
        {
            if (Snapshot.State != SiriState.Listening || data.Length < 2)
            {
                return;
            }

            int count = data.Length / 2;
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                short sample = BitConverter.ToInt16(data, i * 2);
                sum += (double)sample * sample;
            }

            double rms = Math.Sqrt(sum / count) / 32768.0;
            if (rms > 0.012)
            {
                _heardVoice = true;
                _lastVoiceUtc = DateTime.UtcNow;
            }
        }

        /// <summary>提交文本（文字输入通道，或语音转写完成后）。</summary>
        public async Task SendTextAsync(string text)
        {
            if (_disposed || !_config.Enabled || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            CancelGeneration();

            // 新问题打断上一段播报
            _speaker.Stop();

            string input = text.Trim();
            _history.Append(new SiriMessage { Role = "user", Content = input });
            Update(new SiriSnapshot
            {
                State = SiriState.Thinking,
                Input = input,
                Output = string.Empty,
            });

            var cts = new CancellationTokenSource();
            _conversationCts = cts;

            var output = new StringBuilder();
            bool cancelled = false;

            try
            {
                List<SiriMessage> prompt = _history.BuildPrompt(_config.ContextLimit);
                // 刚追加的 user 消息已经进了历史，这里去掉最后一条避免重复
                if (prompt.Count > 0 && prompt[^1].Role == "user" && prompt[^1].Content == input)
                {
                    prompt.RemoveAt(prompt.Count - 1);
                }

                await _api.StreamChatAsync(
                    _config,
                    prompt,
                    input,
                    delta =>
                    {
                        output.Append(delta);
                        Post(() => Update(new SiriSnapshot
                        {
                            State = SiriState.Streaming,
                            Input = input,
                            Output = output.ToString(),
                        }));
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 用户主动停止：保留已生成的部分，但不再播报。
                cancelled = true;
            }
            catch (SiriApiException ex)
            {
                Post(() => Update(new SiriSnapshot
                {
                    State = SiriState.Error,
                    Input = input,
                    Output = output.ToString(),
                    Error = ex.Message,
                }));
                return;
            }
            catch (Exception ex)
            {
                Post(() => Update(new SiriSnapshot
                {
                    State = SiriState.Error,
                    Input = input,
                    Output = output.ToString(),
                    Error = "调用失败：" + ex.Message,
                }));
                return;
            }
            finally
            {
                if (ReferenceEquals(_conversationCts, cts))
                {
                    _conversationCts = null;
                }
                cts.Dispose();
            }

            string finalText = output.ToString();
            if (finalText.Length > 0)
            {
                _history.Append(new SiriMessage { Role = "assistant", Content = finalText });
            }

            Post(() => Update(new SiriSnapshot
            {
                State = SiriState.Answered,
                Input = input,
                Output = finalText,
            }));

            // 播报本次回复（设置里可关闭；被主动停止的截断回复不播）
            if (!cancelled && _config.SpeakReplies && finalText.Length > 0)
            {
                _speaker.Speak(finalText, _config.SpeechVoice, _config.SpeechRate);
            }
        }

        /// <summary>停止当前生成。</summary>
        public void StopGeneration()
        {
            CancelGeneration();
            _speaker.Stop();

            Post(() => Update(new SiriSnapshot
            {
                State = SiriState.Answered,
                Input = Snapshot.Input,
                Output = Snapshot.Output,
            }));
        }

        /// <summary>收起面板，回到静默待机。</summary>
        public void Collapse()
        {
            _listenTimer?.Stop();
            CancelGeneration();
            _speaker.Stop();

            // 为这一句临时开的麦，收起时一并关掉
            if (_captureOpenedByCommand && !_config.VoiceWakeEnabled)
            {
                _audio.Stop();
            }

            _captureOpenedByCommand = false;

            Post(() => Update(new SiriSnapshot { State = SiriState.Idle }));
        }

        /// <summary>清空对话历史。</summary>
        public void ClearHistory()
        {
            _history.Clear();
        }

        /// <summary>把录音送去转写（语音通道）。</summary>
        public async Task TranscribeAndSendAsync()
        {
            byte[] wav = _audio.TakeWav();
            if (wav.Length == 0)
            {
                Update(new SiriSnapshot
                {
                    State = SiriState.Error,
                    Error = "没有录到声音，再说一次试试。",
                });
                return;
            }

            Update(new SiriSnapshot
            {
                State = SiriState.Transcribing,
                Input = Snapshot.Input,
                Output = string.Empty,
            });

            try
            {
                string text;

                if (!string.IsNullOrWhiteSpace(_config.AsrBaseUrl))
                {
                    text = await _api.TranscribeAsync(
                        _config, wav, "speech.wav", CancellationToken.None);
                }
                else if (!_wake.IsReady)
                {
                    // 模型没下好 / 下载中断：这条路径以前会静默失败，
                    // 用户只看到「接口不存在」，完全猜不到是模型的问题。
                    Update(new SiriSnapshot
                    {
                        State = SiriState.Error,
                        Error = "离线语音模型未就绪，请在设置 → Siri 里检查唤醒模型，或填写语音转写接口。",
                    });
                    return;
                }
                else
                {
                    // 没配远端转写接口时用本地 Vosk 兜底。
                    // 之前会把对话网关地址当成转写地址去请求 /audio/transcriptions，
                    // 那边没有这个接口，于是稳定报「接口不存在」。
                    byte[] pcm = TakePcm(wav);
                    text = await Task.Run(() => _wake.TranscribeOffline(pcm));

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Update(new SiriSnapshot
                        {
                            State = SiriState.Error,
                            Error = "离线模型没听清，可在设置 → Siri 里配置语音转写接口。",
                        });
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Update(new SiriSnapshot
                    {
                        State = SiriState.Error,
                        Error = "没有识别到语音内容。",
                    });
                    return;
                }

                await SendTextAsync(text);
            }
            catch (SiriApiException ex)
            {
                Update(new SiriSnapshot { State = SiriState.Error, Error = ex.Message });
            }
            catch (Exception ex)
            {
                Update(new SiriSnapshot { State = SiriState.Error, Error = "转写失败：" + ex.Message });
            }
        }

        /// <summary>从 WAV 里取出裸 PCM（跳过 RIFF 头），喂给 Vosk 整段识别。</summary>
        private static byte[] TakePcm(byte[] wav)
        {
            if (wav == null || wav.Length <= 44)
            {
                return Array.Empty<byte>();
            }

            // 按 "data" 块定位，兼容不同的头部长度
            for (int i = 12; i + 8 <= wav.Length; i++)
            {
                if (wav[i] == (byte)'d' && wav[i + 1] == (byte)'a' &&
                    wav[i + 2] == (byte)'t' && wav[i + 3] == (byte)'a')
                {
                    int start = i + 8;
                    return start < wav.Length ? wav.AsSpan(start).ToArray() : Array.Empty<byte>();
                }
            }

            return wav.AsSpan(44).ToArray();
        }

        /// <summary>停止当前生成并释放取消令牌。</summary>
        private void CancelGeneration()
        {
            CancellationTokenSource? cts = _conversationCts;
            _conversationCts = null;

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        /// <summary>更新快照并通知 UI。</summary>
        private void Update(SiriSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>把动作切到 UI 线程执行。</summary>
        private void Post(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.BeginInvoke(action);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ReleaseHotkey();
            _listenTimer?.Stop();
            CancelGeneration();
            _speaker.Dispose();
            _audio.Dispose();
            _wake.Dispose();
            _api.Dispose();
            _config.Save();
        }
    }
}
