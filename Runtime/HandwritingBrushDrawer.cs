using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace HandwritingRecognition
{
    /// <summary>
    /// 笔刷渲染器：把笔画段以「软边圆形 quad」的方式绘制到一张 RenderTexture 上。
    /// 使用预分配 Mesh + DrawMeshNow，运行时几乎零 GC。
    /// </summary>
    internal sealed class HandwritingBrushDrawer : IDisposable
    {
        // 每个 quad 4 个顶点，每段笔迹由两个 quad（起点端帽 + 起到终点 capsule 体）组成 —— 这里简化为：
        // 对每两个连续输入点之间画一条「胶囊段」，胶囊段 = 1 个矩形 + 两端各 1 个圆（端帽）。
        // 但软边 shader 仅依赖到中心的距离，所以我们用一个 OBB（沿段方向的矩形）+ 两个圆 quad 也可。
        // 为了简化与高效，这里采用「每段输入点画一个圆 quad」的策略：当采样足够密时，连续的圆构成一条平滑笔迹。
        // 同时为了避免极端高速移动留下空隙，在采样阶段会按距离插值出中间点（在 HandwritingCanvas 处理）。

        private const int VERTS_PER_QUAD = 4;
        private const int INDICES_PER_QUAD = 6;

        // 顶点结构：position(3 float) + color(4 float) + uv(4 float) = 44 字节
        // 字段顺序必须与 VertexLayout 一致，并遵守 Unity 要求的标准属性顺序：
        // Position -> Normal -> Tangent -> Color -> TexCoord0..7 -> BlendWeight -> BlendIndices
        // 若顺序错乱，Unity 会自动调整 GPU layout 而不调整 C# 结构体，导致数据错位。
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4, Size = 44)]
        private struct BrushVertex
        {
            public Vector3 Position; // 12
            public Color Color;      // 16  RGBA float
            public Vector4 UV;       // 16  xy: 局部坐标 -1..1, z: softness, w: 备用
        }

        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color,     VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
        };

        private readonly RenderTexture _target;
        private readonly Material _brushMaterial;
        private readonly Color _backgroundColor;
        private readonly float _invHalfWidth;   // 2 / width  -- 用于像素→NDC 换算
        private readonly float _invHalfHeight;  // 2 / height

        // 批量提交缓冲
        private readonly int _maxQuadsPerBatch;
        private readonly BrushVertex[] _vertices;
        private readonly Mesh _mesh;
        private int _pendingQuadCount;

        // CommandBuffer 用于跨渲染管线（内置/URP/HDRP）稳定地写 RT
        private readonly CommandBuffer _cmd;

        public RenderTexture Target => _target;
        public int Width => _target.width;
        public int Height => _target.height;

        public HandwritingBrushDrawer(int width, int height, Color backgroundColor, Shader brushShader, int maxQuadsPerBatch = 256)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Invalid texture size.");
            if (brushShader == null) throw new ArgumentNullException(nameof(brushShader));

            _backgroundColor = backgroundColor;
            _maxQuadsPerBatch = Mathf.Max(1, maxQuadsPerBatch);
            _invHalfWidth = 2f / width;
            _invHalfHeight = 2f / height;

            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                msaaSamples = 1,
                sRGB = true,
                useMipMap = false,
                autoGenerateMips = false
            };
            _target = new RenderTexture(desc)
            {
                name = "HandwritingRT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            _target.Create();

            _brushMaterial = new Material(brushShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _vertices = new BrushVertex[_maxQuadsPerBatch * VERTS_PER_QUAD];
            var indices = new ushort[_maxQuadsPerBatch * INDICES_PER_QUAD];
            // 预填充索引（quad 顺序固定）
            for (int q = 0; q < _maxQuadsPerBatch; q++)
            {
                int vi = q * VERTS_PER_QUAD;
                int ii = q * INDICES_PER_QUAD;
                indices[ii + 0] = (ushort)(vi + 0);
                indices[ii + 1] = (ushort)(vi + 1);
                indices[ii + 2] = (ushort)(vi + 2);
                indices[ii + 3] = (ushort)(vi + 0);
                indices[ii + 4] = (ushort)(vi + 2);
                indices[ii + 5] = (ushort)(vi + 3);
            }

            _mesh = new Mesh
            {
                name = "HandwritingBrushMesh",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = IndexFormat.UInt16
            };
            _mesh.MarkDynamic();
            // 预分配 buffer
            _mesh.SetVertexBufferParams(_vertices.Length, VertexLayout);
            _mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
            _mesh.SetIndexBufferData(indices, 0, 0, indices.Length, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            _mesh.subMeshCount = 1;
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length, MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 1f)); // NDC bounds

            _cmd = new CommandBuffer { name = "HandwritingBrushFlush" };

            Clear();
        }

        /// <summary>
        /// 像素坐标 (左下原点, Y 向上为正) 转 NDC (-1..1)。
        /// 注意：Y 取反 —— 因为 UGUI 的 RawImage 采样 RT 时，UV(0,0) 在显示空间的「左下」，
        /// 而 GPU 写 RT 时（DX/Vulkan/Metal）实际是「左上原点」存储，Unity 在 RawImage 采样时不会自动翻转。
        /// 因此我们让画布像素的 Y 向上对应 NDC 的 Y 向下（写入到 RT 的「上方 row」），
        /// 这样 RawImage 显示出来才是「正立」的（与鼠标输入方向一致）。
        /// 副作用：CaptureJpg 会得到与显示一致的图像（左上原点 = 文字朝上），
        /// 这正好符合 JPG/PNG 等图像格式的标准方向，模型识别时方向正确。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void PixelToNdc(float px, float py, out float nx, out float ny)
        {
            nx = px * _invHalfWidth - 1f;
            ny = 1f - py * _invHalfHeight;
        }

        /// <summary>
        /// 用背景色清空画布。使用 CommandBuffer 立即执行，跨管线稳定。
        /// </summary>
        public void Clear()
        {
            _pendingQuadCount = 0;
            _cmd.Clear();
            _cmd.SetRenderTarget(_target);
            _cmd.ClearRenderTarget(RTClearFlags.Color, _backgroundColor, 1f, 0);
            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();
        }

        /// <summary>
        /// 添加一个圆点笔刷 quad 到批次中。坐标系：左下原点，单位像素。
        /// 内部会把像素坐标转换为 NDC (-1..1)，再写入顶点缓冲（shader 直接当 clip 空间使用）。
        /// </summary>
        public void AppendDot(Vector2 center, float radius, float softness, Color color)
        {
            if (radius <= 0f) return;
            if (_pendingQuadCount >= _maxQuadsPerBatch)
            {
                Flush();
            }

            int vi = _pendingQuadCount * VERTS_PER_QUAD;
            float px0 = center.x - radius;
            float px1 = center.x + radius;
            float py0 = center.y - radius;
            float py1 = center.y + radius;

            // 像素 -> NDC
            PixelToNdc(px0, py0, out float nx0, out float ny0);
            PixelToNdc(px1, py1, out float nx1, out float ny1);

            // UV.xy 在 (-1..1) 范围，shader 据此计算到中心距离
            // 字段赋值用命名属性，顺序无关；底层布局已由结构体声明顺序保证
            _vertices[vi + 0] = new BrushVertex { Position = new Vector3(nx0, ny0, 0f), Color = color, UV = new Vector4(-1f, -1f, softness, 0f) };
            _vertices[vi + 1] = new BrushVertex { Position = new Vector3(nx0, ny1, 0f), Color = color, UV = new Vector4(-1f,  1f, softness, 0f) };
            _vertices[vi + 2] = new BrushVertex { Position = new Vector3(nx1, ny1, 0f), Color = color, UV = new Vector4( 1f,  1f, softness, 0f) };
            _vertices[vi + 3] = new BrushVertex { Position = new Vector3(nx1, ny0, 0f), Color = color, UV = new Vector4( 1f, -1f, softness, 0f) };

            _pendingQuadCount++;
        }

        /// <summary>
        /// 提交当前批次中的所有 quad 到 RenderTexture。使用 CommandBuffer + ExecuteCommandBuffer，
        /// 跨渲染管线（内置/URP/HDRP）稳定。
        /// </summary>
        public void Flush()
        {
            if (_pendingQuadCount <= 0) return;

            int vertCount = _pendingQuadCount * VERTS_PER_QUAD;
            int idxCount = _pendingQuadCount * INDICES_PER_QUAD;

            // 上传顶点数据（仅前 vertCount 个）
            _mesh.SetVertexBufferData(_vertices, 0, 0, vertCount,
                flags: MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers);
            // 调整 SubMesh 长度（只画当前批次部分）
            _mesh.SetSubMesh(0, new SubMeshDescriptor(0, idxCount, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

            _cmd.Clear();
            _cmd.SetRenderTarget(_target);
            // 顶点已是 NDC 坐标，shader 自行处理为 clip 空间（无需 view-projection）
            _cmd.DrawMesh(_mesh, Matrix4x4.identity, _brushMaterial, 0, 0);
            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();

            _pendingQuadCount = 0;
        }

        public void Dispose()
        {
            // Flush 残留（理论上调用方应自行 Flush；这里防御性处理时不再绘制以避免在非渲染线程使用）
            _pendingQuadCount = 0;

            _cmd?.Release();

            if (_mesh != null)
            {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(_mesh); else UnityEngine.Object.DestroyImmediate(_mesh);
#else
                UnityEngine.Object.Destroy(_mesh);
#endif
            }
            if (_brushMaterial != null)
            {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(_brushMaterial); else UnityEngine.Object.DestroyImmediate(_brushMaterial);
#else
                UnityEngine.Object.Destroy(_brushMaterial);
#endif
            }
            if (_target != null)
            {
                _target.Release();
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(_target); else UnityEngine.Object.DestroyImmediate(_target);
#else
                UnityEngine.Object.Destroy(_target);
#endif
            }
        }
    }
}