# HandwritingRecognition

Unity 中文手写识别面板组件 (UGUI + UniTask + OpenAI 兼容多模态接口)。

## 快速上手

### 1. 创建配置

Project 面板右键: **Create > Handwriting > Config**。

关键字段：
- **API URL** — `https://api.siliconflow.cn/v1/chat/completions`（默认已填，硅基流动）
- **API Key** — 运行时填入，**不要提交到版本控制**
- **Model Name** — 推荐 `Qwen/Qwen2.5-VL-7B-Instruct`（对中文手写效果好且免费/低价）
- **Image Detail** — `Low`（速度最快，token 消耗最低，手写场景足够）
- **Disable Thinking** — 勾选（对支持思考开关的模型可加快响应）
- **Max Tokens** — 64（手写短文本不需要太多）
- **Max Image Size** — 768（VLM 模型对小图响应更快）
- **Timeout Seconds** — 15（VLM 推理一般 2-10s）

### 2. 把 Shader 加入 Always Included Shaders

`Edit > Project Settings > Graphics > Always Included Shaders`，添加 **`Handwriting/Brush`**，确保打包后不被剥离。

### 3. 搭建面板 Prefab

层级结构（推荐）：

```
HandwrittenPanel (挂 HandwrittenPanel.cs)
├── Canvas (RawImage, 挂 HandwritingCanvas.cs；自动开启 raycastTarget)
├── ButtonsRow
│   ├── SubmitButton
│   ├── ClearButton
│   └── CancelButton
├── LoadingOverlay (可选；识别期间显示)
└── ErrorText (可选；TMP_Text，失败时显示)
```

Inspector 上把对应组件拖入 `HandwrittenPanel` 的字段。

### 4. 使用 API

#### 异步风格（推荐）
```csharp
using Cysharp.Threading.Tasks;
using HandwritingRecognition;
using UnityEngine;
using TMPro;

public class Example : MonoBehaviour
{
    [SerializeField] private HandwrittenPanel _panel;
    [SerializeField] private TMP_Text _output;

    public void OnClickWriteButton() => OnClickAsync().Forget();

    private async UniTaskVoid OnClickAsync()
    {
        var result = await _panel.ShowAsync(this.GetCancellationTokenOnDestroy());
        if (result.IsSuccess)
            _output.text = result.Text;
        else if (result.IsFailed)
            Debug.LogError(result.ErrorMessage);
    }
}
```

#### 回调风格
```csharp
_panel.Show(
    onRecognized: text => _output.text = text,
    onCancelled: () => Debug.Log("取消"),
    onFailed: err => Debug.LogError(err));
```

#### 事件订阅
```csharp
_panel.OnRecognized += text => _output.text = text;
_panel.OnFailed += err => Debug.LogError(err);
_panel.Show();
```

#### 运行时切换配置
```csharp
// 用于安全地从外部配置文件加载 API Key 后注入
_panel.SetConfig(myRuntimeConfig);
```

## 模型推荐（硅基流动）

| 模型 | 速度 | 准确度 | 备注 |
|---|---|---|---|
| `Qwen/Qwen2.5-VL-7B-Instruct` | 快 | ✅ 推荐 | 通义视觉 7B，对中文手写效果好 |
| `Qwen/Qwen2.5-VL-32B-Instruct` | 中 | 高 | 同系列更大版本，需要更高额度 |
| `Qwen/Qwen3-VL-235B-A22B-Instruct` | 慢 | 最高 | 旗舰多模态 |
| `deepseek-ai/DeepSeek-OCR` | — | ❌ 不推荐 | 仅适合印刷版面/票据，对手写无响应 |

## 性能与设计要点

### 渲染
- **笔刷 Mesh 预分配**，运行时无 GC；`SetVertexBufferData` 一次提交一批。
- **跨管线**：使用 `CommandBuffer.SetRenderTarget + DrawMesh + Graphics.ExecuteCommandBuffer`，在内置/URP/HDRP 下行为完全一致。
- **Shader 直接输出 NDC**，不依赖任何 view-projection 矩阵。
- **每帧 LateUpdate 合并 Flush**，确保 DrawCall 数最少。
- **Y 翻转在 C# 端处理**（PixelToNdc 中 `1f - py * _invHalfHeight`），让 RT 内容是「左上原点」朝向，与 RawImage 显示、ReadPixels 读出、JPG/PNG 文件标准方向完全一致。

### 输入
- 通过 UGUI 的 `IPointerDown/Drag/UpHandler` 接事件，鼠标/触控统一处理。
- 锁定第一个指针 `pointerId`，第二根手指或鼠标右键不会污染笔迹。
- 自动开启 RawImage 的 `raycastTarget`。

### 网络
- `UnityWebRequest` + `UniTask.WithCancellation`，按钮取消立即 Abort 请求。
- `CancellationTokenSource.CreateLinkedTokenSource` 合并外部 token 与超时定时器。
- 请求 JSON 手写拼装，`[ThreadStatic] StringBuilder` 复用减少 GC。
- 响应 JSON 轻量解析（只关心 `choices[0].message.content`），不引入第三方 JSON 库。

### 取消语义
- 所有结束路径（成功 / 失败 / 取消 / Panel Destroy）统一通过 `UniTaskCompletionSource.TrySetResult`（幂等）收敛。
- 会话 epoch 防止旧会话 finally 块误操作新会话状态。
- `ShowAsync` 不抛 `OperationCanceledException`，取消通过 `Result.Status = Cancelled` 返回。

### GC 概要（运行时稳态）
- 笔迹绘制：0 分配
- 按帧 Flush：0 分配
- 截图提交：~3 份 jpg 大小的 byte[]/string 临时分配（一次性，可接受）
- await/UniTask 链路：UniTask 本身已优化为 0 装箱

## API 速览

| 成员 | 说明 |
|---|---|
| `UniTask<HandwritingResult> ShowAsync(CancellationToken)` | 异步打开并等待结果 |
| `void Show(onRecognized, onCancelled, onFailed, ct)` | 回调风格 |
| `void Close()` | 主动以 Cancelled 关闭 |
| `void SetConfig(HandwritingConfig)` | 运行时切换配置 |
| `event Action<string> OnRecognized` | 识别成功 |
| `event Action OnCancelled` | 取消 |
| `event Action<string> OnFailed` | 失败 |

`HandwritingResult` 结构：
- `Status`：Success / Cancelled / Failed
- `Text`：成功时为识别文本
- `ErrorMessage`：失败时为错误描述
- `IsSuccess` / `IsCancelled` / `IsFailed` 辅助属性

## 注意事项

1. **API Key 不要硬编码到 ScriptableObject**（会被打包到 Resources）。建议运行时从加密配置/服务端获取后调用 `SetConfig`。
2. **Shader 必须加入 Always Included Shaders**。
3. **手写效果优化**：模型对「黑字白底」识别最准，深色背景上的彩色笔迹效果稍差。如果识别质量不理想，把 `backgroundColor` 改成 `Color.white`、`strokeColor` 改成 `Color.black` 重试。
4. **响应慢**：检查 `imageDetail = Low`、`maxImageSize = 512~768`、`maxTokens = 64`；选用 7B 级别模型。
