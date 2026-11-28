using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pynelith.Plugins;

namespace Pynelith.Plugin.McpBridge
{
    /// <summary>
    /// MCP 桥接插件（通知源）。
    ///
    /// 在 127.0.0.1:17863 暴露 MCP 服务，任何支持 Streamable HTTP 的客户端
    /// （Claude Code / Codex / dsh 等）都可以：
    /// - notify：把"任务完成"等消息推送到灵动岛，走原生通知动效；
    /// - ask：弹出带输入框的通知，用户回车或点发送后把输入返回给客户端；
    /// - status：查询桥接状态。
    ///
    /// 插件本身不主动轮询，所有工作由外部客户端驱动，因此开销接近零。
    /// </summary>
    public sealed class McpBridgePlugin : INotificationSourcePlugin
    {
        private IPluginHost? _host;
        private McpServer? _server;
        private int _requests;

        /// <summary>通知源契约要求的事件；本插件的通知由 MCP 客户端主动触发。</summary>
#pragma warning disable CS0067 // 事件由外部 MCP 调用直接推送，不经过 Received
        public event EventHandler<PluginNotification>? Received;
#pragma warning restore CS0067

        public void Initialize(IPluginHost host)
        {
            _host = host;

            int port = 17863;
            if (int.TryParse(
                host.GetSetting("port", "17863"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int configured))
            {
                port = Math.Clamp(configured, 1024, 65535);
            }

            _server = new McpServer(port, this);
            _server.Start();

            if (_server.IsRunning)
            {
                host.Log("MCP 桥接已启动：" + _server.Endpoint);
                host.UpdateMetric("mcp.endpoint", "MCP", _server.Endpoint);
            }
            else
            {
                host.Log("MCP 桥接启动失败：" + _server.LastError);
                host.UpdateMetric("mcp.endpoint", "MCP", "启动失败：" + _server.LastError);
            }
        }

        internal void Log(string message) => _host?.Log(message);

        /// <summary>请求计数写回状态行，便于在岛上确认客户端确实连上了。</summary>
        internal void ReportRequest()
        {
            int count = Interlocked.Increment(ref _requests);

            // 每 10 次刷新一次，避免高频请求反复触发 UI 更新
            if (count % 10 == 1)
            {
                _host?.UpdateMetric("mcp.requests", "MCP 请求", count.ToString(CultureInfo.InvariantCulture));
            }
        }

        internal async Task<ToolResult> ExecuteToolAsync(
            string name,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            switch (name)
            {
                case "notify":
                {
                    string app = ReadString(arguments, "app", "MCP");
                    string title = ReadString(arguments, "title", "插件通知");
                    string body = ReadString(arguments, "body", string.Empty);

                    _host?.PublishNotification(app, title, body);
                    return ToolResult.Ok("已推送到灵动岛：" + title);
                }

                case "ask":
                {
                    string title = ReadString(arguments, "title", "需要你的确认");
                    string body = ReadString(arguments, "body", string.Empty);
                    int timeout = ReadInt(arguments, "timeout_seconds", 120);

                    if (_host is IInteractiveHost interactive)
                    {
                        string? answer = await interactive
                            .AskUserAsync(title, body, timeout, cancellationToken)
                            .ConfigureAwait(false);

                        return ToolResult.Ok(answer == null ? "(用户未回复 / 已超时)" : answer);
                    }

                    // 老宿主没有交互能力：退化为普通通知
                    _host?.PublishNotification("MCP", title, body);
                    return ToolResult.Ok("(当前宿主不支持岛内回复，已作为普通通知展示)");
                }

                case "status":
                {
                    string endpoint = _server == null ? "未启动" : _server.Endpoint;
                    string state = _server != null && _server.IsRunning ? "运行中" : "未运行";
                    return ToolResult.Ok(endpoint + " · " + state + " · 已处理 "
                        + Volatile.Read(ref _requests).ToString(CultureInfo.InvariantCulture) + " 个请求");
                }

                default:
                    return ToolResult.Fail("未知工具：" + name);
            }
        }

        private static string ReadString(JsonElement arguments, string name, string fallback)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out JsonElement element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? fallback;
            }

            return fallback;
        }

        private static int ReadInt(JsonElement arguments, string name, int fallback)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(name, out JsonElement element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int value))
            {
                return Math.Clamp(value, 5, 3600);
            }

            return fallback;
        }

        public void Dispose()
        {
            _server?.Dispose();
            _server = null;
            _host = null;
        }
    }

    /// <summary>工具调用结果。</summary>
    internal readonly struct ToolResult
    {
        private ToolResult(string text, bool isError)
        {
            Text = text;
            IsError = isError;
        }

        public string Text { get; }

        public bool IsError { get; }

        public static ToolResult Ok(string text) => new ToolResult(text, false);

        public static ToolResult Fail(string text) => new ToolResult(text, true);
    }
}
