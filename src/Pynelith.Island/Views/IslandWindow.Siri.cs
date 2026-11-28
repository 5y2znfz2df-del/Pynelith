using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pynelith.Glass.Effects;
using Pynelith.Island.Services.Siri;

namespace Pynelith.Island.Views
{
    /// <summary>
    /// Siri 组件在灵动岛里的集成层。
    ///
    /// 职责边界：
    /// - 组件本体（配置 / 麦克风 / 唤醒 / AI）在 <see cref="Services.Siri.SiriService"/>；
    /// - 本文件只负责「把组件状态映射成透镜视觉」与「和岛的面板互斥」。
    ///
    /// 透镜视觉（对齐 iOS 27 Beta Siri 参考图）：
    /// 椭圆液态玻璃 + 中心横向色散光带，光带按状态脉动 / 流动 / 呼吸 / 收拢。
    /// </summary>
    public partial class IslandWindow
    {
        /// <summary>透镜光带的刷新频率（毫秒）。60fps 足够顺滑，也够省。</summary>
        private const int SiriGlowTickMs = 16;

        /// <summary>光带脉动的周期（毫秒）：收音时一明一暗。</summary>
        private const double SiriPulsePeriodMs = 620;

        /// <summary>光带呼吸的周期（毫秒）：空闲时缓慢明暗。</summary>
        private const double SiriBreathePeriodMs = 3400;

        /// <summary>快捷键消息挂接的宿主窗口（热键消息由它派发）。</summary>
        private HwndSource? _siriHotkeySource;

        /// <summary>当前透镜玻璃是否跑在"兼容模式"（不排除抓屏）下。</summary>
        private bool _siriGlassCompat = true;

        /// <summary>
        /// 初始化 Siri 组件：装玻璃层、建光带定时器、订阅状态变化。
        /// 组件被禁用时不装玻璃、不开麦，胶囊只保留普通内容。
        /// </summary>
        private void InitSiri()
        {
            _siriGlowTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(SiriGlowTickMs),
            };
            _siriGlowTimer.Tick += OnSiriGlowTick;

            if (_siri == null)
            {
                return;
            }

            // 胶囊态：右端保留一颗极简的 Siri 光点，作为组件的存在标识与点击入口
            SiriDot.Visibility = Visibility.Visible;

            _siri.SnapshotChanged += OnSiriSnapshotChanged;

            // 透镜玻璃层：独立于胶囊玻璃，可单独关闭（低配 GPU 模式）。
            InstallSiriGlass();
        }

        /// <summary>
        /// 装/换透镜玻璃层。
        ///
        /// 装在正方形 SiriOrb 上，着色器的椭圆 SDF 才能裁出正圆。
        /// ExcludeFromCapture 必须跟随「兼容模式」：
        /// 兼容模式下整个岛要留在抓屏范围里（截图/录屏可见），
        /// 如果这里仍按默认排除，会把窗口从抓屏里摘掉，
        /// 导致球体采样到的是一片空背景 —— 横光看起来像贴在平面上，
        /// 和折射完全脱节。
        /// </summary>
        private void InstallSiriGlass()
        {
            _siriGlass?.Dispose();
            _siriGlass = null;

            if (_siri == null || _siri.Config.LowGpuMode)
            {
                return;
            }

            _siriGlassCompat = Config.ScreenshotCompatible;

            try
            {
                _siriGlass = LensGlowSurface.Install(
                    SiriOrb,
                    new LensGlowSurface.LensStyle
                    {
                        BezelWidth = 26,
                        Thickness = 30,
                        GlassIor = 1.46,
                        Dispersion = 0.07,
                        // 上黑下透：上半边是深色玻璃（保留折射，只压暗染色），
                        // 下半边完全透明、只走桌面折射，中间平滑过渡。
                        TintAmount = 0.86,
                        HorizonSplit = 0.5,
                        HorizonSoftness = 0.18,
                        BandHalfWidth = 1.0,
                        BandCoreBrightness = 0.90,
                        CaptureMargin = 120,
                        FramesPerSecond = 24,
                        // 兼容模式（可录屏）下不再把窗口排除出抓屏，而是用采样遮罩剔除自身区域：
                        // 遮罩内的像素由四邻边缘插值重建，因此折射 + 色散仍然生效，且不会被录屏漏掉。
                        TransparentInterior = false,
                        ExcludeFromCapture = !Config.ScreenshotCompatible,
                    });

                // 重建后要把当前光带状态推一次，否则要等下一次 Tick 才有亮度
                _siriGlass.SetState(_siriGlowIntensity, _siriGlowCollapse, Math.Clamp(_siriSpring.Value, 0, 1));
            }
            catch (Exception)
            {
                // 玻璃层装不上（驱动异常等）：透镜退化为纯光带，不影响语音功能。
                _siriGlass = null;
            }
        }

        /// <summary>兼容模式切换后重建透镜玻璃（显示亲和力作用在窗口上，只能重装）。</summary>
        private void RefreshSiriGlass()
        {
            if (_siriGlassCompat == Config.ScreenshotCompatible)
            {
                return;
            }

            InstallSiriGlass();
        }

        /// <summary>启动 Siri 组件（异步加载唤醒模型）。</summary>
        private async void StartSiriAsync()
        {
            if (_siri == null)
            {
                return;
            }

            try
            {
                await _siri.StartAsync();
            }
            catch (Exception)
            {
                // 模型下载失败等：组件保持禁用状态，用户可在设置里看到提示。
            }
        }

        #region 快捷键

        /// <summary>
        /// 把全局快捷键挂到岛自己的窗口消息循环上（窗口句柄就绪后调用）。
        /// 之前是在后台线程里用 GetMessage 等热键，既拿错了返回值也收不到消息，
        /// 所以按快捷键完全没有反应；改为注册到本窗口 + HwndSource 钩子后走 UI 线程。
        /// </summary>
        private void AttachSiriHotkey(HwndSource source)
        {
            if (_siri == null || source == null)
            {
                return;
            }

            _siriHotkeySource = source;
            source.AddHook(SiriHotkeyWndProc);
            _siri.RegisterHotkey(source.Handle);
        }

        private IntPtr SiriHotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == HotkeyNative.WM_HOTKEY && wParam.ToInt32() == HotkeyNative.HotkeyId)
            {
                handled = true;
                TriggerSiriVoice();
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 用户主动唤起（快捷键 / 点击右端光点）。
        /// 第一次按下开始收音；正在收音时再按一次结束并转写。
        /// </summary>
        private void TriggerSiriVoice()
        {
            if (_siri == null)
            {
                return;
            }

            ExpandSiri();
            Reposition();

            if (_siri.Snapshot.State is SiriState.Listening or SiriState.Transcribing)
            {
                _ = _siri.FinishListeningAsync();
                return;
            }

            _siri.BeginListening();
        }

        #endregion

        #region 展开与收起

        /// <summary>
        /// 判断点击是否落在胶囊右端的 Siri 光点上（含 4px 外扩的命中区）。
        /// 光点本身不参与命中测试，这里用坐标换算，避免影响胶囊原有的点击语义。
        /// </summary>
        private bool IsSiriDotHit(Point point)
        {
            if (SiriDot.Visibility != Visibility.Visible || SiriDot.ActualWidth <= 0)
            {
                return false;
            }

            // 媒体态 / 展开态下空闲层会淡出（IsHitTestVisible 同步关闭），此时光点不算命中
            if (IdleView.Visibility != Visibility.Visible || !IdleView.IsHitTestVisible)
            {
                return false;
            }

            try
            {
                Point origin = SiriDot.TranslatePoint(new Point(0, 0), this);
                var rect = new Rect(
                    origin.X - 4,
                    origin.Y - 4,
                    SiriDot.ActualWidth + 8,
                    SiriDot.ActualHeight + 8);
                return rect.Contains(point);
            }
            catch (InvalidOperationException)
            {
                // 视觉尚未接入呈现树：当作未命中
                return false;
            }
        }

        /// <summary>展开 Siri 透镜（语音唤醒 / 快捷键 / 点击胶囊都走这里）。</summary>
        private void ExpandSiri()
        {
            if (_siri == null)
            {
                return;
            }

            // 与其它面板互斥：同一时间只展开一个
            DismissMessages();
            SetDetail(false);

            _siriTarget = true;
            _siriCollapsing = false;
            _siriGlowCollapse = 0;

            EnsureSpring();
            ApplyMorph(_morphSpring.Value);
            _siriGlowTimer?.Start();
        }

        /// <summary>
        /// 收起 Siri 透镜。
        /// 光带先收拢变暗，再让面板缩回胶囊 —— 与原有收起动画无缝衔接。
        /// </summary>
        private void CollapseSiri()
        {
            if (_siri == null || !_siriTarget)
            {
                return;
            }

            // 收拢阶段：光带先聚拢变暗，约 260ms 后再收起面板
            _siriCollapsing = true;
            _siriGlowTimer?.Start();

            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(260),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _siriTarget = false;
                _siriCollapsing = false;
                EnsureSpring();
                ApplyMorph(_morphSpring.Value);

                // 面板收起即结束本次会话：停麦、停止生成
                _siri?.Collapse();
            };
            timer.Start();
        }

        /// <summary>光带状态推进：把组件状态映射成光带强度与收拢度。</summary>
        private void OnSiriGlowTick(object? sender, EventArgs e)
        {
            if (_siri == null)
            {
                return;
            }

            double now = (DateTime.Now - _siriGlowPhase).TotalMilliseconds;
            if (_siriGlowPhase == DateTime.MinValue)
            {
                _siriGlowPhase = DateTime.Now;
                now = 0;
            }

            SiriState state = _siri.Snapshot.State;
            double intensity;
            double collapse = _siriCollapsing ? 1 : 0;

            switch (state)
            {
                case SiriState.Listening:
                    // 收音：脉动闪烁
                    {
                        double phase = (now % SiriPulsePeriodMs) / SiriPulsePeriodMs;
                        double pulse = 0.5 + 0.5 * Math.Cos(phase * Math.PI * 2);
                        intensity = 0.55 + 0.42 * pulse;
                    }
                    break;

                case SiriState.Transcribing:
                case SiriState.Thinking:
                    // 思考：聚拢成一点，缓慢明暗
                    intensity = 0.42 + 0.16 * Math.Sin(now / SiriBreathePeriodMs * Math.PI * 2);
                    collapse = Math.Max(collapse, 0.62);
                    break;

                case SiriState.Streaming:
                    // 生成：平滑延展流动（光带左右缓慢来回 + 整体呼吸）
                    {
                        double flow = 0.5 + 0.5 * Math.Sin(now / 1100.0 * Math.PI * 2);
                        intensity = 0.72 + 0.22 * flow;
                    }
                    break;

                case SiriState.Answered:
                    // 回复结束：收拢变暗
                    intensity = 0.34;
                    collapse = Math.Max(collapse, 0.45);
                    break;

                case SiriState.Error:
                    intensity = 0.28;
                    collapse = Math.Max(collapse, 0.7);
                    break;

                default:
                    // 空闲：缓慢呼吸
                    intensity = 0.22 + 0.12 * (0.5 + 0.5 * Math.Sin(now / SiriBreathePeriodMs * Math.PI * 2));
                    break;
            }

            // 收拢动画：collapse 从 0 平滑推到 1
            if (_siriCollapsing)
            {
                _siriGlowCollapse = Math.Min(1, _siriGlowCollapse + 0.09);
                collapse = _siriGlowCollapse;
                intensity *= 1 - _siriGlowCollapse * 0.75;
            }
            else
            {
                _siriGlowCollapse = Math.Max(0, _siriGlowCollapse - 0.12);
                collapse = Math.Max(collapse, _siriGlowCollapse);
            }

            _siriGlowIntensity = intensity;
            _siriGlowCollapse = collapse;

            _siriGlass?.SetState(intensity, collapse, Math.Clamp(_siriSpring.Value, 0, 1));

            // 面板已完全收起：停掉 60fps 的光带定时器，把开销降到零。
            // 下次展开（唤醒 / 快捷键 / 点光点）时再启动。
            if (!_siriTarget && !_siriCollapsing)
            {
                _siriGlowTimer?.Stop();
                _siriGlass?.SetState(0, 1, 0);
            }
        }

        #endregion

        #region 状态同步

        /// <summary>组件状态变化 → 刷新透镜内容。</summary>
        private void OnSiriSnapshotChanged(object? sender, EventArgs e)
        {
            if (_siri == null)
            {
                return;
            }

            SiriSnapshot snap = _siri.Snapshot;

            SiriPromptText.Text = snap.State switch
            {
                SiriState.Listening when string.IsNullOrEmpty(snap.Input) => "正在听…",
                SiriState.Transcribing when string.IsNullOrEmpty(snap.Input) => "识别中…",
                _ => snap.Input,
            };
            SiriReplyText.Text = snap.Output;

            if (!string.IsNullOrEmpty(snap.Error))
            {
                SiriErrorText.Text = snap.Error;
                SiriErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                SiriErrorText.Visibility = Visibility.Collapsed;
            }

            // 生成中 / 收音中都能用"停止"：后者是手动结束这一句
            bool canStop = snap.IsBusy || snap.State is SiriState.Listening or SiriState.Transcribing;
            SiriActions.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;

            // 收音 / 待机时给出文字输入通道
            bool showInput = snap.State is SiriState.Listening or SiriState.Transcribing
                or SiriState.Answered or SiriState.Error;
            SiriInputBox.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;

            // 状态推进到"非待机"时确保透镜已展开
            if (snap.State != SiriState.Idle && !_siriTarget)
            {
                ExpandSiri();
            }
        }

        /// <summary>文字输入回车提交。</summary>
        private void OnSiriInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;

            if (sender is not TextBox box)
            {
                return;
            }

            string text = box.Text.Trim();
            if (text.Length == 0 || _siri == null)
            {
                return;
            }

            box.Clear();
            _ = _siri.SendTextAsync(text);
        }

        /// <summary>停止：生成中停止生成，收音中结束这一句并转写。</summary>
        private void OnSiriStopClick(object sender, RoutedEventArgs e)
        {
            if (_siri == null)
            {
                return;
            }

            if (_siri.Snapshot.State is SiriState.Listening or SiriState.Transcribing)
            {
                _ = _siri.FinishListeningAsync();
                return;
            }

            _siri.StopGeneration();
        }

        /// <summary>清空对话。</summary>
        private void OnSiriClearClick(object sender, RoutedEventArgs e)
        {
            _siri?.ClearHistory();
        }

        #endregion

        /// <summary>释放 Siri 相关资源。</summary>
        private void DisposeSiri()
        {
            _siriGlowTimer?.Stop();
            if (_siriGlowTimer != null)
            {
                _siriGlowTimer.Tick -= OnSiriGlowTick;
            }

            if (_siri != null)
            {
                _siri.SnapshotChanged -= OnSiriSnapshotChanged;
                _siri.ReleaseHotkey();
            }

            if (_siriHotkeySource != null)
            {
                _siriHotkeySource.RemoveHook(SiriHotkeyWndProc);
                _siriHotkeySource = null;
            }

            if (_siri != null)
            {
                _siri.Dispose();
            }

            _siriGlass?.Dispose();
            _siriGlass = null;
        }
    }
}
