using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// Siri 组件的独立配置。
    ///
    /// 与岛的核心配置（<see cref="Configuration.IslandConfig"/>）分开存放：
    /// 组件可以整体禁用，禁用时这份配置原样保留，重新启用即可恢复。
    /// 存放于 %AppData%\Pynelith\siri.json。
    /// </summary>
    public sealed class SiriConfig
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private bool _enabled = true;
        private bool _voiceWakeEnabled = true;
        private string _wakeWord = "嘿 Siri";
        private string _microphoneDevice = string.Empty;
        private double _wakeSensitivity = 0.6;
        private string _chatApiKey = string.Empty;
        private string _chatBaseUrl = "https://api.openai.com/v1";
        private string _chatModel = "gpt-4o-mini";
        private int _contextLimit = 20;
        private string _asrApiKey = string.Empty;
        private string _asrBaseUrl = string.Empty;
        private string _asrModel = "whisper-1";
        private bool _lowGpuMode;
        private string _hotkey = "Ctrl+Alt+S";
        private bool _hotkeyEnabled = true;
        private bool _speakReplies = true;
        private string _speechVoice = "zh-CN-XiaoxiaoNeural";
        private int _speechRate;

        /// <summary>Siri 组件总开关。关闭后不加载模型、不开麦、不监听快捷键。</summary>
        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        /// <summary>语音唤醒开关。关闭后只保留快捷键与点击唤起。</summary>
        public bool VoiceWakeEnabled
        {
            get => _voiceWakeEnabled;
            set => Set(ref _voiceWakeEnabled, value);
        }

        /// <summary>自定义唤醒词（默认「嘿 Siri」）。</summary>
        public string WakeWord
        {
            get => _wakeWord;
            set => Set(ref _wakeWord, value ?? string.Empty);
        }

        /// <summary>麦克风设备名。空 = 系统默认设备。</summary>
        public string MicrophoneDevice
        {
            get => _microphoneDevice;
            set => Set(ref _microphoneDevice, value ?? string.Empty);
        }

        /// <summary>唤醒灵敏度 0..1：越高越容易触发（误触发也越多）。</summary>
        public double WakeSensitivity
        {
            get => _wakeSensitivity;
            set => Set(ref _wakeSensitivity, Math.Clamp(value, 0, 1));
        }

        /// <summary>对话模型的 API Key。完全由用户填写，程序不内置任何密钥。</summary>
        public string ChatApiKey
        {
            get => _chatApiKey;
            set => Set(ref _chatApiKey, value ?? string.Empty);
        }

        /// <summary>对话模型的 Base URL（OpenAI 兼容协议）。</summary>
        public string ChatBaseUrl
        {
            get => _chatBaseUrl;
            set => Set(ref _chatBaseUrl, value ?? string.Empty);
        }

        /// <summary>对话模型名称。</summary>
        public string ChatModel
        {
            get => _chatModel;
            set => Set(ref _chatModel, value ?? string.Empty);
        }

        /// <summary>上下文上限：最多带多少轮历史对话给模型。</summary>
        public int ContextLimit
        {
            get => _contextLimit;
            set => Set(ref _contextLimit, Math.Clamp(value, 0, 100));
        }

        /// <summary>语音转文字（ASR）端点的 API Key。留空则复用对话的 Key。</summary>
        public string AsrApiKey
        {
            get => _asrApiKey;
            set => Set(ref _asrApiKey, value ?? string.Empty);
        }

        /// <summary>语音转文字（ASR）端点的 Base URL。留空则复用对话的 Base URL。</summary>
        public string AsrBaseUrl
        {
            get => _asrBaseUrl;
            set => Set(ref _asrBaseUrl, value ?? string.Empty);
        }

        /// <summary>语音转文字（ASR）模型名称。</summary>
        public string AsrModel
        {
            get => _asrModel;
            set => Set(ref _asrModel, value ?? string.Empty);
        }

        /// <summary>
        /// 低配 GPU 模式：一键关闭透镜折射效果（不再抓屏），降低显存占用。
        /// 适合 MX330 这类核显 / 独显都吃力的机器。
        /// </summary>
        public bool LowGpuMode
        {
            get => _lowGpuMode;
            set => Set(ref _lowGpuMode, value);
        }

        /// <summary>快捷键唤起（全局热键）。格式如 "Ctrl+Alt+S"。</summary>
        public string Hotkey
        {
            get => _hotkey;
            set => Set(ref _hotkey, value ?? string.Empty);
        }

        /// <summary>快捷键开关。</summary>
        public bool HotkeyEnabled
        {
            get => _hotkeyEnabled;
            set => Set(ref _hotkeyEnabled, value);
        }

        /// <summary>是否把回复朗读出来。</summary>
        public bool SpeakReplies
        {
            get => _speakReplies;
            set => Set(ref _speakReplies, value);
        }

        /// <summary>
        /// 播报音色：Edge 神经网络语音名（如 zh-CN-XiaoxiaoNeural），
        /// 或 <see cref="SiriSpeaker.SystemVoice"/> 表示用本机离线语音。
        /// </summary>
        public string SpeechVoice
        {
            get => _speechVoice;
            set => Set(ref _speechVoice, string.IsNullOrWhiteSpace(value) ? "zh-CN-XiaoxiaoNeural" : value);
        }

        /// <summary>播报语速偏移（百分比，-50 ~ 50）。</summary>
        public int SpeechRate
        {
            get => _speechRate;
            set => Set(ref _speechRate, Math.Clamp(value, -50, 50));
        }

        /// <summary>对外通知配置变更（设置面板实时预览用）。</summary>
        public event EventHandler? Changed;

        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>恢复默认值（包括清空所有用户填写的密钥）。</summary>
        public void ResetToDefaults()
        {
            var defaults = new SiriConfig();

            Enabled = defaults.Enabled;
            VoiceWakeEnabled = defaults.VoiceWakeEnabled;
            WakeWord = defaults.WakeWord;
            MicrophoneDevice = defaults.MicrophoneDevice;
            WakeSensitivity = defaults.WakeSensitivity;
            ChatApiKey = defaults.ChatApiKey;
            ChatBaseUrl = defaults.ChatBaseUrl;
            ChatModel = defaults.ChatModel;
            ContextLimit = defaults.ContextLimit;
            AsrApiKey = defaults.AsrApiKey;
            AsrBaseUrl = defaults.AsrBaseUrl;
            AsrModel = defaults.AsrModel;
            LowGpuMode = defaults.LowGpuMode;
            Hotkey = defaults.Hotkey;
            HotkeyEnabled = defaults.HotkeyEnabled;
            SpeakReplies = defaults.SpeakReplies;
            SpeechVoice = defaults.SpeechVoice;
            SpeechRate = defaults.SpeechRate;
        }

        #region 持久化

        /// <summary>配置文件路径。</summary>
        public static string ConfigPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pynelith",
            "siri.json");

        /// <summary>读取配置；文件不存在或损坏时返回默认值。</summary>
        public static SiriConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<SiriConfig>(json, Options);
                    if (config != null)
                    {
                        return config;
                    }
                }
            }
            catch (Exception)
            {
                // 配置损坏时退回默认值，不影响主程序启动。
            }

            return new SiriConfig();
        }

        /// <summary>保存配置（先写临时文件再替换，避免半截文件）。</summary>
        public void Save()
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
                File.Move(tmp, ConfigPath, overwrite: true);
            }
            catch (Exception)
            {
                // 落盘失败不影响使用（下次仍可用内存中的配置）。
            }
        }

        #endregion
    }
}
