using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>一条对话消息。</summary>
    public sealed class SiriMessage
    {
        /// <summary>角色：user / assistant / system。</summary>
        public string Role { get; set; } = "user";

        /// <summary>消息内容。</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>时间戳。</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Siri 对话历史：本地持久化，支持清空。
    /// 存放于 %AppData%\Pynelith\siri-history.json。
    /// </summary>
    public sealed class SiriChatStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        private readonly List<SiriMessage> _messages = new List<SiriMessage>();

        /// <summary>历史变更通知。</summary>
        public event EventHandler? Changed;

        /// <summary>历史存储路径。</summary>
        public static string StorePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pynelith",
            "siri-history.json");

        /// <summary>当前所有消息（只读视图）。</summary>
        public IReadOnlyList<SiriMessage> Messages => _messages;

        /// <summary>读取历史；文件不存在时为空。</summary>
        public void Load()
        {
            _messages.Clear();
            try
            {
                if (File.Exists(StorePath))
                {
                    var list = JsonSerializer.Deserialize<List<SiriMessage>>(
                        File.ReadAllText(StorePath), Options);
                    if (list != null)
                    {
                        _messages.AddRange(list);
                    }
                }
            }
            catch (Exception)
            {
                // 历史损坏时从空历史开始。
            }
        }

        /// <summary>追加一条消息并落盘。</summary>
        public void Append(SiriMessage message)
        {
            _messages.Add(message);
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>清空全部对话。</summary>
        public void Clear()
        {
            if (_messages.Count == 0)
            {
                return;
            }

            _messages.Clear();
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 取出给模型的历史：只保留最近 <paramref name="contextLimit"/> 轮（user+assistant 算一轮）。
        /// </summary>
        public List<SiriMessage> BuildPrompt(int contextLimit)
        {
            if (contextLimit <= 0 || _messages.Count == 0)
            {
                return new List<SiriMessage>();
            }

            // 从后往前按「user + assistant」成对取最近 N 轮。
            // 必须成对取：只取半轮会让 prompt 以 assistant 开头，模型容易跑偏。
            var picked = new List<SiriMessage>();
            int rounds = 0;

            for (int i = _messages.Count - 1; i >= 0 && rounds < contextLimit; i--)
            {
                if (_messages[i].Role != "user")
                {
                    continue;
                }

                // 这条 user 后面紧跟的必须是 assistant；否则说明它还在等待回复，跳过。
                if (i + 1 >= _messages.Count || _messages[i + 1].Role != "assistant")
                {
                    continue;
                }

                picked.Insert(0, _messages[i + 1]);
                picked.Insert(0, _messages[i]);
                rounds++;
                i--; // 已消费掉后面的 assistant
            }

            return picked;
        }

        private void Save()
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = StorePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_messages, Options));
                File.Move(tmp, StorePath, overwrite: true);
            }
            catch (Exception)
            {
                // 落盘失败不影响对话本身。
            }
        }
    }
}
