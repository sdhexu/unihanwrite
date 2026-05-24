using UnityEngine;

namespace HandwritingRecognition
{
    /// <summary>
    /// OpenAI 兼容多模态接口的图像精度。low 速度最快、token 消耗最低；high 质量最好、最慢。
    /// 对手写短文本识别，<c>Low</c> 已足够。
    /// </summary>
    public enum ImageDetail
    {
        Auto,
        Low,
        High,
    }

    /// <summary>
    /// 手写识别配置。可在 Project 中右键 Create > Handwriting > Config 创建实例。
    /// 将来可从外部配置文件读取后赋值到此对象。
    /// </summary>
    [CreateAssetMenu(fileName = "HandwritingConfig", menuName = "Handwriting/Config", order = 0)]
    public class HandwritingConfig : ScriptableObject
    {
        [Header("画布外观")]
        [Tooltip("画布背景颜色")]
        public Color backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);

        [Tooltip("笔迹颜色")]
        public Color strokeColor = Color.white;

        [Tooltip("笔迹粗细 (像素)，相对于画布的 RenderTexture 分辨率")]
        [Range(2f, 60f)]
        public float strokeWidth = 12f;

        [Tooltip("笔刷软边比例 (0 = 硬边, 1 = 全软边)")]
        [Range(0f, 1f)]
        public float strokeSoftness = 0.25f;

        [Header("画布分辨率")]
        [Tooltip("RenderTexture 宽度 (像素)。建议使用 2 的幂或 4 的倍数")]
        public int textureWidth = 1024;

        [Tooltip("RenderTexture 高度 (像素)")]
        public int textureHeight = 512;

        [Header("绘制采样")]
        [Tooltip("两次输入点之间的插值最小距离 (画布像素)。越小越平滑但越耗")]
        [Range(0.5f, 10f)]
        public float minSampleDistance = 1.5f;

        [Header("识别 API (OpenAI 兼容)")]
        [Tooltip("Chat Completions 接口完整 URL")]
        public string apiUrl = "https://api.siliconflow.cn/v1/chat/completions";

        [Tooltip("API Key (Bearer Token)。建议在生产环境通过 SetConfig 在运行时注入，避免随 ScriptableObject 入库")]
        public string apiKey = "";

        [Tooltip("使用的多模态模型名称。手写识别推荐通用 VL 模型，不推荐 DeepSeek-OCR（更适合印刷版面/票据）")]
        public string modelName = "Qwen/Qwen3.6-35B-A3B";

        [Tooltip("发送给大模型的 Prompt，要求其只返回识别到的纯文本")]
        [TextArea(3, 6)]
        public string prompt = "请识别图片中的手写中文文字，直接返回识别到的纯文本内容。要求：只输出文字本身，不要任何解释、说明、标点修正或额外字符；如果图片为空或无法识别，输出空字符串。";

        [Tooltip("模型最多返回的 token 数。建议 16-128（识别短文本无需太大）")]
        [Range(8, 512)]
        public int maxTokens = 64;

        [Tooltip("图像精度：Low 速度最快、token 消耗最低（手写短文本足够）；High 最准但慢；Auto 由服务端决定")]
        public ImageDetail imageDetail = ImageDetail.Low;

        [Tooltip("关闭思考模式 (enable_thinking=false)。仅对硅基流动支持思考开关的模型有效（如 Qwen3 系列、GLM-4.7、DeepSeek-V3.2 等），对不支持的模型（如 Qwen2.5-VL）会被服务端忽略")]
        public bool disableThinking = true;

        [Header("网络")]
        [Tooltip("请求超时时间 (秒)。OCR/VLM 推理一般 2-10s，建议至少 15s")]
        [Range(1f, 60f)]
        public float timeoutSeconds = 15f;

        [Tooltip("JPG 编码质量 (1-100)。手写场景 70-85 已足够")]
        [Range(1, 100)]
        public int jpgQuality = 80;

        [Tooltip("发送给模型时图像最长边的最大尺寸 (像素)。超过将等比缩小以减小带宽与推理时间。VLM 模型对 512-768 边长的小图响应最快；<=0 表示不缩放")]
        public int maxImageSize = 768;
    }
}