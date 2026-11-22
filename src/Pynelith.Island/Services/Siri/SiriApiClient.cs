using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>AI 调用过程中抛出的可读错误（网络 / Key / 模型名 / 余额等）。</summary>
    public sealed class SiriApiException : Exception
    {
        public SiriApiException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// OpenAI 兼容协议客户端：对话走 /chat/completions（SSE 流式），转写走 /audio/transcriptions。
    ///
    /// 全部端点、Key、模型名由用户自行填写，程序不内置任何中转地址或密钥。
    /// </summary>
    public sealed class SiriApiClient : IDisposable
    {
        private readonly HttpClient _http;

        /// <summary>
        /// 会话标识：OpenCode 网关要求每个对话带稳定的 x-opencode-session，
        /// 否则一律 400（MissingSessionID）。同一个客户端实例复用同一个会话。
        /// </summary>
        private readonly string _sessionId = System.Guid.NewGuid().ToString();

        public SiriApiClient()
        {
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120),
            };

            // 网关会按 UA 识别客户端，默认的 .NET 库名属于"通用 HTTP 库"，不被推荐。
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("pynelith-siri/0.3.0");
        }

        /// <summary>
        /// 流式对话。逐段回调增量文本。
        /// </summary>
        /// <param name="cancellationToken">取消后立即停止生成。</param>
        public async Task StreamChatAsync(
            SiriConfig config,
            IReadOnlyList<SiriMessage> history,
            string userInput,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.ChatApiKey))
            {
                throw new SiriApiException("未填写 API Key：请到设置 → Siri → API 配置填写。");
            }

            if (string.IsNullOrWhiteSpace(config.ChatModel))
            {
                throw new SiriApiException("未填写模型名称：请到设置 → Siri → API 配置填写。");
            }

            string baseUrl = NormalizeBaseUrl(config.ChatBaseUrl);
            string url = baseUrl.TrimEnd('/') + "/chat/completions";

            var messages = new List<object>();
            foreach (SiriMessage m in history)
            {
                messages.Add(new { role = m.Role, content = m.Content });
            }
            messages.Add(new { role = "user", content = userInput });

            var payload = new
            {
                model = config.ChatModel,
                messages,
                stream = true,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ChatApiKey);

            // OpenCode 网关（zen/go）需要会话头来路由与命中缓存，缺了直接 400。
            if (url.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("x-opencode-session", _sessionId);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SiriApiException("网络请求失败：" + ex.Message);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string body = await SafeReadAsync(response);
                    throw new SiriApiException(DescribeError((int)response.StatusCode, body));
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    // SSE：以 "data: " 开头，"data: [DONE]" 表示结束
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string payloadPart = line["data:".Length..].Trim();
                    if (payloadPart.Length == 0)
                    {
                        continue;
                    }

                    if (payloadPart == "[DONE]")
                    {
                        break;
                    }

                    string delta = ExtractDelta(payloadPart);
                    if (delta.Length > 0)
                    {
                        onDelta(delta);
                    }
                }
            }
        }

        /// <summary>
        /// 语音转文字：把 WAV 音频 POST 到 /audio/transcriptions。
        /// </summary>
        public async Task<string> TranscribeAsync(
            SiriConfig config,
            byte[] wavData,
            string fileName,
            CancellationToken cancellationToken)
        {
            string key = string.IsNullOrWhiteSpace(config.AsrApiKey)
                ? config.ChatApiKey
                : config.AsrApiKey;

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new SiriApiException("未填写 ASR Key：请到设置 → Siri → API 配置填写。");
            }

            if (string.IsNullOrWhiteSpace(config.AsrModel))
            {
                throw new SiriApiException("未填写 ASR 模型名称：请到设置 → Siri → API 配置填写。");
            }

            // 不能回退到对话网关：那边通常只有 /chat/completions，
            // 请求 /audio/transcriptions 会稳定返回「接口不存在」。
            string baseUrl = NormalizeBaseUrl(config.AsrBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new SiriApiException(
                    "未填写语音转写地址：对话接口通常不提供 /audio/transcriptions，请单独配置 ASR 接口。");
            }

            string url = baseUrl.TrimEnd('/') + "/audio/transcriptions";

            using var form = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wavData);
            audio.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(audio, "file", fileName);

            form.Add(new StringContent(config.AsrModel), "model");

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SiriApiException("转写请求失败：" + ex.Message);
            }

            using (response)
            {
                string body = await SafeReadAsync(response);
                if (!response.IsSuccessStatusCode)
                {
                    throw new SiriApiException(DescribeError((int)response.StatusCode, body));
                }

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("text", out JsonElement text))
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
                catch (JsonException)
                {
                    // 不是 JSON：直接把响应当文本（有些服务返回纯文本）
                }

                return body.Trim();
            }
        }

        /// <summary>从 SSE 的一个 data 段里取出增量文本。</summary>
        private static string ExtractDelta(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                JsonElement delta = choices[0].GetProperty("delta");
                if (!delta.TryGetProperty("content", out JsonElement content)
                    || content.ValueKind == JsonValueKind.Null)
                {
                    return string.Empty;
                }

                return content.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
            catch (KeyNotFoundException)
            {
                return string.Empty;
            }
        }

        /// <summary>把 HTTP 错误翻译成人能看懂的提示。</summary>
        private static string DescribeError(int status, string body)
        {
            string detail = ExtractErrorMessage(body);

            return status switch
            {
                401 or 403 => "鉴权失败：API Key 无效或没有权限。" + detail,
                402 => "额度不足（HTTP 402）：请在服务商控制台订阅或充值后再试。" + detail,
                404 => "接口不存在：请检查 Base URL 是否正确（应包含 /v1 这类路径）。" + detail,
                429 => "请求过于频繁或额度不足：请稍后再试。" + detail,
                >= 500 => "服务端错误（" + status + "）：请稍后再试。" + detail,
                _ => "请求失败（HTTP " + status + "）。" + detail,
            };
        }

        /// <summary>从错误响应体里挖出 message 字段。</summary>
        private static string ExtractErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("error", out JsonElement err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        return "（" + err.GetString() + "）";
                    }

                    if (err.TryGetProperty("message", out JsonElement msg))
                    {
                        return "（" + msg.GetString() + "）";
                    }
                }

                if (root.TryGetProperty("message", out JsonElement topMsg))
                {
                    return "（" + topMsg.GetString() + "）";
                }
            }
            catch (JsonException)
            {
                // 不是 JSON 就截一段原文
            }

            string trimmed = body.Trim();
            if (trimmed.Length > 160)
            {
                trimmed = trimmed[..160] + "…";
            }

            return trimmed.Length > 0 ? "（" + trimmed + "）" : string.Empty;
        }

        /// <summary>补全 Base URL：空则给默认；没写 /v1 的按 OpenAI 惯例补上。</summary>
        private static string NormalizeBaseUrl(string baseUrl)
        {
            string url = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://api.openai.com/v1"
                : baseUrl.Trim();

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            return url;
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
