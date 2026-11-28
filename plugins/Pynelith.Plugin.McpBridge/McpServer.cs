using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Plugin.McpBridge
{
    /// <summary>
    /// 极简 MCP 服务器（Streamable HTTP + JSON-RPC 2.0）。
    ///
    /// 只用 <see cref="TcpListener"/> 手写 HTTP/1.1，避免 HttpListener 需要 URL ACL。
    /// 只监听回环地址，默认端口 17863，路径不限（推荐 /mcp）。
    /// 支持的方法：initialize、notifications/initialized、ping、tools/list、tools/call。
    /// </summary>
    internal sealed class McpServer : IDisposable
    {
        private const string ProtocolVersion = "2025-06-18";
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 4 * 1024 * 1024;

        private readonly TcpListener _listener;
        private readonly McpBridgePlugin _plugin;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        private CancellationTokenSource? _cts;

        public McpServer(int port, McpBridgePlugin plugin)
        {
            Port = port;
            _plugin = plugin;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public int Port { get; }

        public string Endpoint => "http://127.0.0.1:" + Port + "/mcp";

        public bool IsRunning { get; private set; }

        public string? LastError { get; private set; }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (SocketException exception)
            {
                // 端口被占 / 无权限：不抛给宿主，卡片上会显示失败原因
                LastError = exception.Message;
                IsRunning = false;
                return;
            }

            IsRunning = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(token));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                client.NoDelay = true;

                try
                {
                    using NetworkStream stream = client.GetStream();
                    HttpRequest? request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                    if (request == null)
                    {
                        return;
                    }

                    if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, 204, "No Content", null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        // 无状态实现，不提供服务端主动推送的 SSE 流
                        await WriteJsonAsync(stream, 405, Error(null, -32000, "仅支持 POST"), cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(stream, 405, Error(null, -32000, "Method Not Allowed"), cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    string? response = await DispatchAsync(request.Body, cancellationToken).ConfigureAwait(false);
                    if (response == null)
                    {
                        // 通知类消息没有响应体
                        await WriteAsync(stream, 202, "Accepted", string.Empty, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonAsync(stream, 200, response, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 宿主关闭
                }
                catch (Exception exception)
                {
                    _plugin.Log("MCP 连接处理失败：" + exception.Message);
                }
            }
        }

        /// <summary>解析请求：返回 null 表示连接已断开。批量 JSON-RPC 会按数组返回。</summary>
        private async Task<string?> DispatchAsync(string body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return Error(null, -32700, "Parse error");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return Error(null, -32700, "Parse error");
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                _plugin.ReportRequest();

                if (root.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (JsonElement element in root.EnumerateArray())
                    {
                        string? one = await HandleMessageAsync(element, cancellationToken).ConfigureAwait(false);
                        if (one != null)
                        {
                            parts.Add(one);
                        }
                    }

                    return parts.Count == 0 ? null : "[" + string.Join(",", parts) + "]";
                }

                return await HandleMessageAsync(root, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<string?> HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                return Error(null, -32600, "Invalid Request");
            }

            string? id = message.TryGetProperty("id", out JsonElement idElement) ? idElement.GetRawText() : null;
            string method = message.TryGetProperty("method", out JsonElement methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            JsonElement parameters = message.TryGetProperty("params", out JsonElement p) ? p : default;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, new Dictionary<string, object?>
                        {
                            ["protocolVersion"] = parameters.ValueKind == JsonValueKind.Object
                                && parameters.TryGetProperty("protocolVersion", out JsonElement version)
                                && version.ValueKind == JsonValueKind.String
                                    ? version.GetString()
                                    : ProtocolVersion,
                            ["capabilities"] = new Dictionary<string, object?>
                            {
                                ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false },
                            },
                            ["serverInfo"] = new Dictionary<string, object?>
                            {
                                ["name"] = "pynelith-mcp-bridge",
                                ["version"] = "1.0.0",
                            },
                            ["instructions"] = "Pynelith 灵动岛桥接：notify 推送通知，ask 在岛内提问并等待用户回复，status 查看服务状态。",
                        });

                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return null;

                    case "ping":
                        return Result(id, new Dictionary<string, object?>());

                    case "tools/list":
                        return Result(id, new Dictionary<string, object?>
                        {
                            ["tools"] = BuildToolList(),
                        });

                    case "tools/call":
                    {
                        string name = parameters.ValueKind == JsonValueKind.Object
                            && parameters.TryGetProperty("name", out JsonElement nameElement)
                            && nameElement.ValueKind == JsonValueKind.String
                                ? nameElement.GetString() ?? string.Empty
                                : string.Empty;

                        JsonElement arguments = parameters.ValueKind == JsonValueKind.Object
                            && parameters.TryGetProperty("arguments", out JsonElement args)
                                ? args
                                : default;

                        ToolResult toolResult = await _plugin
                            .ExecuteToolAsync(name, arguments, cancellationToken)
                            .ConfigureAwait(false);

                        return Result(id, new Dictionary<string, object?>
                        {
                            ["content"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "text",
                                    ["text"] = toolResult.Text,
                                },
                            },
                            ["isError"] = toolResult.IsError,
                        });
                    }

                    default:
                        return id == null ? null : Error(id, -32601, "Method not found: " + method);
                }
            }
            catch (OperationCanceledException)
            {
                return id == null ? null : Error(id, -32800, "Request cancelled");
            }
            catch (Exception exception)
            {
                _plugin.Log("MCP 工具调用异常：" + exception.Message);
                return id == null ? null : Error(id, -32603, exception.Message);
            }
        }

        private static object[] BuildToolList()
        {
            return new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "notify",
                    ["description"] = "在 Pynelith 灵动岛推送一条通知（使用原生通知动效）。",
                    ["inputSchema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["app"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "来源名称，例如 Claude Code、Codex、dsh",
                            },
                            ["title"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "通知标题",
                            },
                            ["body"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "通知正文",
                            },
                        },
                        ["required"] = new[] { "title" },
                    },
                },
                new Dictionary<string, object?>
                {
                    ["name"] = "ask",
                    ["description"] = "在灵动岛弹出带输入框的通知并等待用户回复；用户回车或点击发送后返回输入内容。",
                    ["inputSchema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["title"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "标题，例如 Claude Code 需要确认",
                            },
                            ["body"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["description"] = "要展示给用户的内容",
                            },
                            ["timeout_seconds"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer",
                                ["description"] = "等待秒数，默认 120（最小 5，最大 3600）",
                            },
                        },
                        ["required"] = new[] { "title", "body" },
                    },
                },
                new Dictionary<string, object?>
                {
                    ["name"] = "status",
                    ["description"] = "查看 MCP 桥接与灵动岛的连接状态。",
                    ["inputSchema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>(),
                    },
                },
            };
        }

        private string Result(string? id, object value)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"result\":" + JsonSerializer.Serialize(value, _json) + "}";
        }

        private static string Error(string? id, int code, string message)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null")
                + ",\"error\":{\"code\":" + code.ToString(CultureInfo.InvariantCulture)
                + ",\"message\":" + JsonSerializer.Serialize(message) + "}}";
        }

        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[MaxHeaderBytes];
            int total = 0;
            int headerEnd = -1;

            while (headerEnd < 0 && total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return null;
                }

                total += read;
                headerEnd = IndexOfHeaderEnd(buffer, total);
            }

            if (headerEnd < 0)
            {
                return null;
            }

            string headerText = Encoding.Latin1.GetString(buffer, 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
            {
                return null;
            }

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
                {
                    contentLength = Math.Clamp(length, 0, MaxBodyBytes);
                }
            }

            int bodyStart = headerEnd + 4;
            int alreadyRead = total - bodyStart;
            var body = new byte[contentLength];

            if (alreadyRead > 0)
            {
                Array.Copy(buffer, bodyStart, body, 0, Math.Min(alreadyRead, contentLength));
            }

            int offset = Math.Min(Math.Max(alreadyRead, 0), contentLength);
            while (offset < contentLength)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            return new HttpRequest
            {
                Method = requestLine[0],
                Path = requestLine[1],
                Body = Encoding.UTF8.GetString(body, 0, offset),
            };
        }

        private static int IndexOfHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                {
                    return i;
                }
            }

            return -1;
        }

        private static Task WriteJsonAsync(NetworkStream stream, int status, string body, CancellationToken cancellationToken)
        {
            return WriteAsync(stream, status, "OK", body, cancellationToken);
        }

        private static async Task WriteAsync(
            NetworkStream stream,
            int status,
            string reason,
            string? body,
            CancellationToken cancellationToken)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? string.Empty);
            string head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + "Access-Control-Allow-Headers: *\r\n"
                + "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n"
                + "\r\n";

            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            IsRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // 关闭阶段的 Socket 异常无需上报
            }
        }

        private sealed class HttpRequest
        {
            public string Method { get; set; } = string.Empty;

            public string Path { get; set; } = string.Empty;

            public string Body { get; set; } = string.Empty;
        }
    }
}
