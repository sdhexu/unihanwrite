using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HandwritingRecognition
{
    /// <summary>
    /// 手写画布。挂载到承载手写的 RawImage 物体上。
    /// 负责：
    /// 1) 维护一个 RenderTexture 并赋给 RawImage；
    /// 2) 把指针事件（屏幕坐标）转换为画布像素坐标；
    /// 3) 通过 <see cref="HandwritingBrushDrawer"/> 把笔迹绘制到 RT。
    /// 注：使用 IPointerDownHandler/IDragHandler/IPointerUpHandler，Unity 的 EventSystem 自然只追踪一个主指针，
    /// 第二根手指在 UGUI 下会作为「第二指针」分别派发事件，本组件通过 pointerId 锁定第一根手指，忽略其它指针，防止误触。
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    [DisallowMultipleComponent]
    public sealed class HandwritingCanvas : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [SerializeField] private Shader _brushShader; // 可选，未设置时通过 Shader.Find 加载

        private RawImage _rawImage;
        private RectTransform _rectTransform;
        private HandwritingBrushDrawer _drawer;
        private HandwritingConfig _config;
        private bool _refsCached;

        // 当前激活的指针 id（-1 表示无）
        private int _activePointerId = int.MinValue;
        private bool _hasLastPoint;
        private Vector2 _lastCanvasPoint;

        // 是否有任何笔迹（用于「画布是否为空」的快速判断）
        public bool HasStrokes { get; private set; }
        // 标记本帧是否有新增 quad 需要 flush 到 RT
        private bool _dirty;

        /// <summary>
        /// 当前 RenderTexture（绑定到 RawImage）。在 Initialize 后有效。
        /// </summary>
        public RenderTexture Texture => _drawer?.Target;

        private void Awake()
        {
            CacheReferences();
        }

        /// <summary>
        /// 懒加载缓存 RawImage / RectTransform 引用。
        /// 当 Panel 默认隐藏时，子物体的 Awake 不会触发，外部调用 Initialize 时需手动调用此方法。
        /// </summary>
        private void CacheReferences()
        {
            if (_refsCached) return;
            _rawImage = GetComponent<RawImage>();
            _rectTransform = (RectTransform)transform;
            // 必须启用 raycastTarget 才能接收 EventSystem 事件（这是新手最常踩的坑，自动修复并提示）
            if (_rawImage != null && !_rawImage.raycastTarget)
            {
                _rawImage.raycastTarget = true;
                Debug.LogWarning("[HandwritingCanvas] RawImage.raycastTarget was disabled. Auto-enabled. Without it the canvas cannot receive pointer events.", this);
            }
            _refsCached = true;
        }

        /// <summary>
        /// 初始化或重新初始化画布。可在运行时切换配置（会重建 RT）。
        /// </summary>
        public void Initialize(HandwritingConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            // 确保引用已缓存（应对 Panel 默认 inactive 导致 Awake 未跑的情形）
            CacheReferences();
            _config = config;

            // 如果分辨率/背景色变化，则重建
            bool needRecreate = _drawer == null
                                || _drawer.Width != config.textureWidth
                                || _drawer.Height != config.textureHeight;

            if (needRecreate)
            {
                _drawer?.Dispose();
                var shader = _brushShader != null ? _brushShader : Shader.Find("Handwriting/Brush");
                if (shader == null)
                {
                    Debug.LogError("[HandwritingCanvas] Brush shader 'Handwriting/Brush' not found. Please ensure it is included in builds (Project Settings > Graphics > Always Included Shaders).");
                    return;
                }
                _drawer = new HandwritingBrushDrawer(config.textureWidth, config.textureHeight, config.backgroundColor, shader);
                if (_rawImage != null) _rawImage.texture = _drawer.Target;
            }
            else
            {
                // 仅清空
                _drawer.Clear();
            }

            HasStrokes = false;
            _hasLastPoint = false;
            _activePointerId = int.MinValue;
        }

        /// <summary>
        /// 清空画布。
        /// </summary>
        public void Clear()
        {
            if (_drawer == null) return;
            _drawer.Clear();
            HasStrokes = false;
            _hasLastPoint = false;
            // 不重置 _activePointerId：若用户在按下时点了清除按钮一般不会发生，且按钮会拦截事件
        }

        /// <summary>
        /// 异步读取当前画布为 JPG 字节数组。需在主线程调用。
        /// </summary>
        /// <param name="jpgQuality">1-100</param>
        /// <param name="maxSize">最长边像素，<=0 不缩放</param>
        public byte[] CaptureJpg(int jpgQuality, int maxSize)
        {
            if (_drawer == null || _drawer.Target == null) return null;
            // 先确保所有挂起 quad 已 flush
            _drawer.Flush();

            var src = _drawer.Target;
            int srcW = src.width;
            int srcH = src.height;
            int dstW = srcW, dstH = srcH;
            if (maxSize > 0)
            {
                int longSide = Mathf.Max(srcW, srcH);
                if (longSide > maxSize)
                {
                    float scale = (float)maxSize / longSide;
                    dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
                    dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));
                }
            }

            RenderTexture scaled = null;
            RenderTexture readSource = src;
            if (dstW != srcW || dstH != srcH)
            {
                scaled = RenderTexture.GetTemporary(dstW, dstH, 0, src.format);
                scaled.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, scaled);
                readSource = scaled;
            }

            // ReadPixels 会触发 GPU->CPU 同步；运行频率低（仅提交时一次），可接受。
            var tex = new Texture2D(dstW, dstH, TextureFormat.RGB24, false, false);
            var prev = RenderTexture.active;
            RenderTexture.active = readSource;
            try
            {
                tex.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0, false);
                tex.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = prev;
                if (scaled != null) RenderTexture.ReleaseTemporary(scaled);
            }

            byte[] jpg = tex.EncodeToJPG(Mathf.Clamp(jpgQuality, 1, 100));
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tex); else UnityEngine.Object.Destroy(tex);
#else
            UnityEngine.Object.Destroy(tex);
#endif
            return jpg;
        }

        private void OnDestroy()
        {
            if (_rawImage != null) _rawImage.texture = null;
            _drawer?.Dispose();
            _drawer = null;
        }

        /// <summary>
        /// 在每帧最后批量提交本帧产生的笔刷 quads，保证渲染到 RT 但合并 DrawCall。
        /// </summary>
        private void LateUpdate()
        {
            if (_dirty && _drawer != null)
            {
                _drawer.Flush();
                _dirty = false;
            }
        }

        #region 指针事件

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_drawer == null || _config == null) return;
            // 已有激活指针：忽略其它手指
            if (_activePointerId != int.MinValue) return;
            _activePointerId = eventData.pointerId;

            if (!TryGetCanvasPoint(eventData, out var p)) return;
            // 起笔：先点一个圆点
            DrawDot(p);
            _lastCanvasPoint = p;
            _hasLastPoint = true;
            HasStrokes = true;
            _dirty = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_drawer == null || _config == null) return;
            if (eventData.pointerId != _activePointerId) return;

            if (!TryGetCanvasPoint(eventData, out var p)) return;
            if (_hasLastPoint)
            {
                DrawSegment(_lastCanvasPoint, p);
            }
            else
            {
                DrawDot(p);
            }
            _lastCanvasPoint = p;
            _hasLastPoint = true;
            HasStrokes = true;
            _dirty = true;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId != _activePointerId) return;
            EndStroke();
        }

        private void EndStroke()
        {
            _activePointerId = int.MinValue;
            _hasLastPoint = false;
            // 立即 flush，确保截图/下一帧渲染包含完整笔迹
            if (_drawer != null)
            {
                _drawer.Flush();
                _dirty = false;
            }
        }

        private bool TryGetCanvasPoint(PointerEventData eventData, out Vector2 canvasPoint)
        {
            // 屏幕坐标 -> RectTransform 本地坐标
            var cam = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rectTransform, eventData.position, cam, out var local))
            {
                canvasPoint = default;
                return false;
            }

            var rect = _rectTransform.rect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                canvasPoint = default;
                return false;
            }

            // RectTransform 本地坐标原点取决于 pivot；rect.xMin/yMin 已经处理了 pivot。
            // 归一化到 [0,1]
            float u = (local.x - rect.xMin) / rect.width;
            float v = (local.y - rect.yMin) / rect.height;
            // 映射到 RT 像素坐标（左下原点，符合 GL 投影）
            canvasPoint = new Vector2(u * _drawer.Width, v * _drawer.Height);
            return true;
        }

        private void DrawDot(Vector2 p)
        {
            float radius = Mathf.Max(1f, _config.strokeWidth * 0.5f);
            _drawer.AppendDot(p, radius, _config.strokeSoftness, _config.strokeColor);
        }

        /// <summary>
        /// 在两点之间按最小采样距离插值出多个圆点，构成连续笔迹。
        /// </summary>
        private void DrawSegment(Vector2 from, Vector2 to)
        {
            float radius = Mathf.Max(1f, _config.strokeWidth * 0.5f);
            float softness = _config.strokeSoftness;
            var color = _config.strokeColor;

            Vector2 delta = to - from;
            float dist = delta.magnitude;
            if (dist <= 0.0001f)
            {
                _drawer.AppendDot(to, radius, softness, color);
                return;
            }

            float step = Mathf.Max(_config.minSampleDistance, 0.5f);
            int count = Mathf.Max(1, Mathf.CeilToInt(dist / step));
            Vector2 inv = delta / count;
            // 不重复绘制 from（在 OnPointerDown / 上一次 segment 已绘制）
            Vector2 cur = from;
            for (int i = 0; i < count; i++)
            {
                cur += inv;
                _drawer.AppendDot(cur, radius, softness, color);
            }
        }

        #endregion
    }
}