using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HandwritingRecognition
{
    /// <summary>
    /// 手写面板主组件。挂载到面板根物体（包含 RawImage 画布与提交/清除/取消按钮）。
    /// 同时提供 <see cref="ShowAsync"/> 异步 API 与 <see cref="Show"/> 回调 API。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HandwrittenPanel : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("手写识别配置 (ScriptableObject)")]
        [SerializeField] private HandwritingConfig _config;

        [Header("引用")]
        [SerializeField] private HandwritingCanvas _canvas;
        [SerializeField] private Button _submitButton;
        [SerializeField] private Button _clearButton;
        [SerializeField] private Button _cancelButton;

        [Header("可选 UI")]
        [Tooltip("识别中的 Loading 遮罩 (识别期间显示)")]
        [SerializeField] private GameObject _loadingOverlay;
        [Tooltip("错误提示文本 (UGUI Text；可空)。失败时显示错误消息")]
        [SerializeField] private TMP_Text _errorText;

        [Header("行为")]
        [Tooltip("显示面板时是否自动清空之前的笔迹")]
        [SerializeField] private bool _clearOnShow = true;
        [Tooltip("识别失败时是否保留笔迹，仅显示错误供重试")]
        [SerializeField] private bool _keepStrokesOnError = true;

        /// <summary>
        /// 识别成功（点击提交并拿到结果）时触发。
        /// </summary>
        public event Action<string> OnRecognized;
        /// <summary>
        /// 取消（点击取消或外部 token 取消）时触发。
        /// </summary>
        public event Action OnCancelled;
        /// <summary>
        /// 识别失败时触发。
        /// </summary>
        public event Action<string> OnFailed;

        // 当前会话状态。每次 Show 创建一个新的 CTS；任何按钮点击只针对当前会话。
        private CancellationTokenSource _sessionCts;
        private UniTaskCompletionSource<HandwritingResult> _currentTcs;
        private bool _isRecognizing;
        private bool _buttonsHooked;
        // 会话纪元号：每次 ShowAsync 自增。用于防止「旧会话的 finally 块隐藏新会话面板」的竞态。
        private int _sessionEpoch;

        public bool IsVisible => gameObject.activeSelf;
        public HandwritingConfig Config => _config;

        private void Awake()
        {
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (_canvas == null)
            {
                _canvas = GetComponentInChildren<HandwritingCanvas>(true);
            }
            HookButtons();
        }

        private void OnDestroy()
        {
            UnhookButtons();
            // 关闭未完成会话
            CancelInternal(disposeOnly: true);
        }

        private void HookButtons()
        {
            if (_buttonsHooked) return;
            if (_submitButton != null) _submitButton.onClick.AddListener(OnClickSubmit);
            if (_clearButton != null) _clearButton.onClick.AddListener(OnClickClear);
            if (_cancelButton != null) _cancelButton.onClick.AddListener(OnClickCancel);
            _buttonsHooked = true;
        }

        private void UnhookButtons()
        {
            if (!_buttonsHooked) return;
            if (_submitButton != null) _submitButton.onClick.RemoveListener(OnClickSubmit);
            if (_clearButton != null) _clearButton.onClick.RemoveListener(OnClickClear);
            if (_cancelButton != null) _cancelButton.onClick.RemoveListener(OnClickCancel);
            _buttonsHooked = false;
        }

        /// <summary>
        /// 运行时替换配置（下次 Show 生效；若当前正在显示，会立即按新分辨率重建画布并清空）。
        /// </summary>
        public void SetConfig(HandwritingConfig config)
        {
            _config = config;
            if (IsVisible && _canvas != null && _config != null)
            {
                _canvas.Initialize(_config);
            }
        }

        /// <summary>
        /// 异步显示面板并等待用户操作。返回 <see cref="HandwritingResult"/>。
        /// 此方法不会抛 OperationCanceledException —— 取消会以 Status=Cancelled 形式返回。
        /// </summary>
        public async UniTask<HandwritingResult> ShowAsync(CancellationToken cancellationToken = default)
        {
            EnsureInitialized();

            if (_config == null)
            {
                Debug.LogError("[HandwrittenPanel] Config is null. Please assign HandwritingConfig.");
                return HandwritingResult.Failed("Config is null");
            }
            if (_canvas == null)
            {
                Debug.LogError("[HandwrittenPanel] HandwritingCanvas reference is missing.");
                return HandwritingResult.Failed("Canvas missing");
            }

            // 若已有会话，先取消旧的并以 Cancelled 结束（不抛异常给上一个调用方）
            if (_currentTcs != null)
            {
                CancelInternal(disposeOnly: false);
            }

            // 进入新纪元
            int myEpoch = ++_sessionEpoch;

            // 初始化 UI
            _canvas.Initialize(_config);
            if (_clearOnShow) _canvas.Clear();
            SetLoading(false);
            SetError(null);
            SetInteractable(true);
            gameObject.SetActive(true);

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentTcs = new UniTaskCompletionSource<HandwritingResult>();
            var localTcs = _currentTcs;
            var localCts = _sessionCts;

            // 注册外部取消（链路：cancellationToken -> sessionCts.Token -> Register）
            // 用 ValueTuple 装载 this+epoch，避免闭包分配
            var ctr = localCts.Token.Register(static state =>
            {
                var tuple = ((HandwrittenPanel panel, int epoch))state;
                tuple.panel.HandleExternalCancel(tuple.epoch);
            }, (this, myEpoch));

            try
            {
                var result = await localTcs.Task;
                return result;
            }
            finally
            {
                ctr.Dispose();
                // 仅当当前纪元未被新会话顶替时，才执行清理。否则新会话已经在管理状态。
                if (myEpoch == _sessionEpoch)
                {
                    if (this != null && gameObject != null)
                    {
                        gameObject.SetActive(false);
                    }
                    _currentTcs = null;
                    if (_sessionCts != null)
                    {
                        _sessionCts.Dispose();
                        _sessionCts = null;
                    }
                    _isRecognizing = false;
                }
                else
                {
                    // 仅释放本会话自己持有的 CTS
                    try { localCts.Dispose(); }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }

        /// <summary>
        /// 回调风格 API。内部调用 <see cref="ShowAsync"/>，等价于事件订阅 + 异步等待。
        /// </summary>
        public void Show(Action<string> onRecognized = null, Action onCancelled = null, Action<string> onFailed = null, CancellationToken cancellationToken = default)
        {
            ShowAsync(cancellationToken).ContinueWith(result =>
            {
                switch (result.Status)
                {
                    case HandwritingResultStatus.Success:
                        onRecognized?.Invoke(result.Text);
                        break;
                    case HandwritingResultStatus.Cancelled:
                        onCancelled?.Invoke();
                        break;
                    case HandwritingResultStatus.Failed:
                        onFailed?.Invoke(result.ErrorMessage);
                        break;
                }
            }).Forget();
        }

        /// <summary>
        /// 主动关闭面板，会以 Cancelled 结束当前 ShowAsync 调用。
        /// </summary>
        public void Close()
        {
            CancelInternal(disposeOnly: false);
        }

        #region 按钮回调

        private void OnClickClear()
        {
            if (_isRecognizing) return;
            _canvas?.Clear();
            SetError(null);
        }

        private void OnClickCancel()
        {
            if (_currentTcs == null) return;
            CancelInternal(disposeOnly: false);
        }

        private void OnClickSubmit()
        {
            if (_currentTcs == null || _isRecognizing) return;
            if (_canvas == null || _config == null) return;
            if (!_canvas.HasStrokes)
            {
                SetError("请先书写内容");
                return;
            }
            DoRecognizeAsync().Forget();
        }

        private async UniTaskVoid DoRecognizeAsync()
        {
            // 捕获本会话的 TCS / CTS 到本地变量。任何外部 Cancel/Show 都不会让本任务访问错乱的状态。
            var localTcs = _currentTcs;
            var localCts = _sessionCts;
            int myEpoch = _sessionEpoch;

            _isRecognizing = true;
            SetError(null);
            SetInteractable(false);
            SetLoading(true);

            try
            {
                byte[] jpg = _canvas.CaptureJpg(_config.jpgQuality, _config.maxImageSize);
                if (jpg == null || jpg.Length == 0)
                {
                    // 若仍是当前会话，记一次失败
                    if (myEpoch == _sessionEpoch) CompleteFailed("画布截图失败");
                    return;
                }

                var token = localCts?.Token ?? CancellationToken.None;
                string text = await HandwritingRecognizer.RecognizeAsync(_config, jpg, token);

                if (myEpoch == _sessionEpoch) CompleteSuccess(text ?? string.Empty);
            }
            catch (OperationCanceledException)
            {
                // 取消路径：可能是 Cancel 按钮 / Close / 外部 token。CancelInternal 已经把 TCS 置为 Cancelled，无需重复。
                // 若由于异常顺序问题尚未置为完成，则补一刀（仅当仍是当前会话）。
                if (myEpoch == _sessionEpoch && localTcs != null && localTcs.Task.Status == UniTaskStatus.Pending)
                {
                    CompleteCancelled();
                }
            }
            catch (TimeoutException tex)
            {
                if (myEpoch == _sessionEpoch) CompleteFailed(tex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HandwrittenPanel] Recognition error: {ex}");
                if (myEpoch == _sessionEpoch) CompleteFailed(ex.Message);
            }
            finally
            {
                // 仅在仍是当前会话时恢复 UI 可交互状态。否则面板已被新会话替换，无需操作。
                if (myEpoch == _sessionEpoch)
                {
                    _isRecognizing = false;
                    SetLoading(false);
                    SetInteractable(true);
                }
            }
        }

        #endregion

        #region 完成 / 取消处理

        private void HandleExternalCancel(int epoch)
        {
            // 由 CancellationToken 触发 —— 在主线程异步调度更安全
            // 仅处理与该 epoch 对应的会话
            UniTask.Post(() =>
            {
                if (epoch != _sessionEpoch) return;
                if (_currentTcs == null) return;
                CompleteCancelled();
            });
        }

        private void CancelInternal(bool disposeOnly)
        {
            // 先触发底层 Web 请求取消（已注册的 token 回调会调用 HandleExternalCancel -> CompleteCancelled）
            // 注意：Cancel 不 Dispose CTS，因为正在进行的 RecognizeAsync 可能仍持有 token；Dispose 由 ShowAsync 的 finally 块负责。
            try { _sessionCts?.Cancel(); } catch { /* ignore */ }

            // 再抢先把 TCS 置为 Cancelled —— TrySetResult 是幂等的，重复调用安全。
            // 这样即便 token 回调因 SynchronizationContext 异步而延迟，也能立即结束 await。
            if (_currentTcs != null)
            {
                CompleteCancelled();
            }

            if (disposeOnly)
            {
                if (_sessionCts != null)
                {
                    _sessionCts.Dispose();
                    _sessionCts = null;
                }
                _currentTcs = null;
            }
        }

        private void CompleteSuccess(string text)
        {
            var tcs = _currentTcs;
            if (tcs == null) return;
            // 先 TrySetResult，成功（首个）才触发 event，避免重复回调
            if (tcs.TrySetResult(HandwritingResult.Success(text)))
            {
                try { OnRecognized?.Invoke(text); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void CompleteCancelled()
        {
            var tcs = _currentTcs;
            if (tcs == null) return;
            if (tcs.TrySetResult(HandwritingResult.Cancelled()))
            {
                try { OnCancelled?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void CompleteFailed(string error)
        {
            // 失败时无论是否保留会话，都触发一次 OnFailed event
            try { OnFailed?.Invoke(error); }
            catch (Exception ex) { Debug.LogException(ex); }

            if (_keepStrokesOnError)
            {
                // 保留笔迹与面板，仅显示错误；不结束会话，等待用户选择重新提交 / 清除 / 取消
                SetError(error);
                return;
            }
            _currentTcs?.TrySetResult(HandwritingResult.Failed(error));
        }

        #endregion

        #region UI 辅助

        private void SetLoading(bool show)
        {
            if (_loadingOverlay != null && _loadingOverlay.activeSelf != show)
            {
                _loadingOverlay.SetActive(show);
            }
        }

        private void SetError(string message)
        {
            if (_errorText == null) return;
            if (string.IsNullOrEmpty(message))
            {
                if (_errorText.gameObject.activeSelf) _errorText.gameObject.SetActive(false);
                _errorText.text = string.Empty;
            }
            else
            {
                _errorText.text = message;
                if (!_errorText.gameObject.activeSelf) _errorText.gameObject.SetActive(true);
            }
        }

        private void SetInteractable(bool interactable)
        {
            if (_submitButton != null) _submitButton.interactable = interactable;
            if (_clearButton != null) _clearButton.interactable = interactable;
            if (_cancelButton != null) _cancelButton.interactable = interactable;
        }

        #endregion
    }
}