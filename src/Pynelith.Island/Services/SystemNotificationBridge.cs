using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Pynelith.Island.Services
{
    /// <summary>
    /// 系统通知接管桥：轮询 <see cref="UserNotificationListener"/>，
    /// 新通知转交给岛内展示；接管开启时撤下原生 Toast，实现"全部接管"。
    /// 首次使用需要用户在 系统设置 → 通知 → 通知访问 中允许本应用。
    /// </summary>
    public sealed class SystemNotificationBridge : IDisposable
    {
        /// <summary>最近一次授权/运行状态，设置窗口直接展示。</summary>
        public static string Status { get; private set; } = "未连接";

        private readonly UserNotificationListener _listener;
        private readonly DispatcherTimer _timer;
        private readonly HashSet<uint> _known = new HashSet<uint>();
        private bool _takeover;
        private bool _polling;
        private bool _disposed;

        private SystemNotificationBridge(UserNotificationListener listener, bool takeover)
        {
            _listener = listener;
            _takeover = takeover;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(800),
            };
            _timer.Tick += OnTick;
        }

        /// <summary>应用名 + 通知正文。</summary>
        public event Action<string, string>? Received;

        /// <summary>系统不支持时返回 null（调用方回退到模拟数据）。</summary>
        public static SystemNotificationBridge? TryCreate(bool takeover)
        {
            try
            {
                return new SystemNotificationBridge(UserNotificationListener.Current, takeover);
            }
            catch (Exception)
            {
                Status = "当前系统不支持通知监听";
                return null;
            }
        }

        public void SetTakeover(bool takeover) => _takeover = takeover;

        /// <summary>请求通知访问权限；返回是否已授权。</summary>
        public async Task<bool> EnsureAccessAsync()
        {
            try
            {
                UserNotificationListenerAccessStatus access = await _listener.RequestAccessAsync();
                Status = access == UserNotificationListenerAccessStatus.Allowed
                    ? "已授权，正在接管系统通知"
                    : "未授权：请到 系统设置 → 通知 → 通知访问 中允许 Pynelith";
                return access == UserNotificationListenerAccessStatus.Allowed;
            }
            catch (Exception ex)
            {
                Status = "通知服务不可用：" + ex.Message;
                return false;
            }
        }

        public void Start()
        {
            _timer.Start();
        }

        private async void OnTick(object? sender, EventArgs e)
        {
            if (_polling || _disposed)
            {
                return;
            }

            _polling = true;
            try
            {
                await PollAsync();
            }
            catch (Exception)
            {
                // 轮询失败不打断 UI，下一轮再试。
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task PollAsync()
        {
            IReadOnlyList<UserNotification> list =
                await _listener.GetNotificationsAsync(NotificationKinds.Toast);

            var seen = new HashSet<uint>();
            foreach (UserNotification notification in list)
            {
                seen.Add(notification.Id);

                if (!_known.Add(notification.Id))
                {
                    continue;
                }

                string app = ResolveAppName(notification);
                string body = ResolveBody(notification);

                Received?.Invoke(app, body);

                if (_takeover)
                {
                    try
                    {
                        _listener.RemoveNotification(notification.Id);
                    }
                    catch (Exception)
                    {
                        // 撤下失败不影响岛内展示。
                    }
                }
            }

            // 已被系统清除的通知允许再次出现
            _known.IntersectWith(seen);
        }

        private static string ResolveAppName(UserNotification notification)
        {
            try
            {
                string? display = notification.AppInfo?.DisplayInfo?.DisplayName;
                if (!string.IsNullOrWhiteSpace(display))
                {
                    return display;
                }

                string? aumid = notification.AppInfo?.AppUserModelId;
                if (!string.IsNullOrWhiteSpace(aumid))
                {
                    int cut = aumid.LastIndexOf('!');
                    return cut > 0 ? aumid[..cut] : aumid;
                }
            }
            catch (Exception)
            {
                // 取不到应用信息时归入"系统"。
            }

            return "系统";
        }

        private static string ResolveBody(UserNotification notification)
        {
            var texts = new List<string>();
            try
            {
                NotificationVisual visual = notification.Notification.Visual;
                NotificationBinding? binding = visual.Bindings.Count > 0 ? visual.Bindings[0] : null;

                try
                {
                    binding = visual.GetBinding("ToastGeneric") ?? binding;
                }
                catch (Exception)
                {
                    // 没有 ToastGeneric 绑定时用第一个绑定。
                }

                if (binding != null)
                {
                    foreach (var text in binding.GetTextElements())
                    {
                        if (!string.IsNullOrWhiteSpace(text.Text))
                        {
                            texts.Add(text.Text);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 视觉树解析失败时返回空正文。
            }

            return texts.Count > 0 ? string.Join(" · ", texts) : "新通知";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
