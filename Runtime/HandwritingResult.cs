namespace HandwritingRecognition
{
    /// <summary>
    /// 识别结果状态。
    /// </summary>
    public enum HandwritingResultStatus
    {
        /// <summary>识别成功并返回了文本（可能为空字符串，表示模型认为画布无内容）。</summary>
        Success,
        /// <summary>用户取消（点击取消、关闭面板、外部 CancellationToken 触发）。</summary>
        Cancelled,
        /// <summary>识别过程中发生错误（网络、API、解析等）。</summary>
        Failed
    }

    /// <summary>
    /// 手写识别结果。
    /// </summary>
    public readonly struct HandwritingResult
    {
        public readonly HandwritingResultStatus Status;
        public readonly string Text;
        public readonly string ErrorMessage;

        public bool IsSuccess => Status == HandwritingResultStatus.Success;
        public bool IsCancelled => Status == HandwritingResultStatus.Cancelled;
        public bool IsFailed => Status == HandwritingResultStatus.Failed;

        private HandwritingResult(HandwritingResultStatus status, string text, string error)
        {
            Status = status;
            Text = text;
            ErrorMessage = error;
        }

        public static HandwritingResult Success(string text) => new HandwritingResult(HandwritingResultStatus.Success, text ?? string.Empty, null);
        public static HandwritingResult Cancelled() => new HandwritingResult(HandwritingResultStatus.Cancelled, null, null);
        public static HandwritingResult Failed(string error) => new HandwritingResult(HandwritingResultStatus.Failed, null, error ?? "unknown error");
    }
}