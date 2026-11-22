using System;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// Siri 组件的交互状态。
    ///
    /// 状态机（单向推进，收尾时可回退）：
    /// <code>
    /// Idle ──(唤醒/快捷键/点击)──> Listening ──(识别到语音)──> Transcribing
    ///                                   ▲                              │
    ///                                   │                              ▼
    ///                                   └──────(收音超时)────── Thinking
    ///                                                                  │
    ///                                                                  ▼
    ///                                                              Streaming
    ///                                                                  │
    ///                                                                  ▼
    ///                                                                Answered
    /// </code>
    /// </summary>
    public enum SiriState
    {
        /// <summary>静默待机：胶囊只显示极简光点，透镜收起。</summary>
        Idle = 0,

        /// <summary>正在收音：光带脉动闪烁。</summary>
        Listening,

        /// <summary>语音已转成文字，等待或正在提交。</summary>
        Transcribing,

        /// <summary>AI 正在思考（等待首个 token）：光带聚拢成一点。</summary>
        Thinking,

        /// <summary>AI 流式输出中：光带平滑延展流动。</summary>
        Streaming,

        /// <summary>回复结束：光带收拢变暗。</summary>
        Answered,

        /// <summary>出错（网络 / Key）：显示错误提示，光带转暗红。</summary>
        Error,
    }

    /// <summary>Siri 组件对外暴露的快照，UI 层据此刷新。</summary>
    public sealed class SiriSnapshot
    {
        /// <summary>当前状态。</summary>
        public SiriState State { get; init; } = SiriState.Idle;

        /// <summary>用户说出的（或键入的）文本。</summary>
        public string Input { get; init; } = string.Empty;

        /// <summary>AI 已输出的文本（流式累积）。</summary>
        public string Output { get; init; } = string.Empty;

        /// <summary>错误信息（网络 / Key / 模型名错误等），无错误时为空。</summary>
        public string Error { get; init; } = string.Empty;

        /// <summary>是否正在生成（用于允许"停止生成"）。</summary>
        public bool IsBusy => State is SiriState.Thinking or SiriState.Streaming;

        /// <summary>是否已经展开出透镜面板（收起时为 false）。</summary>
        public bool IsExpanded => State != SiriState.Idle;
    }
}
