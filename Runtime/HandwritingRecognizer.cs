using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace HandwritingRecognition
{
    /// <summary>
    /// 调用 OpenAI 兼容多模态接口进行手写识别。无状态、线程安全（但 UnityWebRequest 必须主线程调用）。
    /// </summary>
    internal static class HandwritingRecognizer
    {
        // 复用同一线程内的 StringBuilder，减少分配。VLM 请求体可能 >20KB，需要较大初始容量。
        [ThreadStatic] private static StringBuilder _tlsBuilder;

        /// <summary>
        /// 异步调用模型识别图片中的文字。
        /// </summary>
        /// <param name="config">配置</param>
        /// <param name="jpgBytes">JPG 图像字节</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>识别到的文本（已 trim）</returns>
        /// <exception cref="OperationCanceledException">外部取消或超时</exception>
        /// <exception cref="TimeoutException">请求超时</exception>
        /// <exception cref="Exception">网络或解析错误</exception>
        public static async UniTask<string> RecognizeAsync(HandwritingConfig config, byte[] jpgBytes, CancellationToken cancellationToken)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (jpgBytes == null || jpgBytes.Length == 0) throw new ArgumentException("Empty image data.", nameof(jpgBytes));
            if (string.IsNullOrWhiteSpace(config.apiUrl)) throw new InvalidOperationException("API URL is not configured.");
            if (string.IsNullOrWhiteSpace(config.modelName)) throw new InvalidOperationException("Model name is not configured.");

            string body = BuildRequestBody(config, jpgBytes);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            using var req = new UnityWebRequest(config.apiUrl, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(bodyBytes) { contentType = "application/json" };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(config.apiKey))
            {
                req.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
            }

            // 超时：使用 CancellationTokenSource.CreateLinkedTokenSource，组合外部取消与超时
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int timeoutMs = Mathf.Max(1000, Mathf.RoundToInt(config.timeoutSeconds * 1000f));
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                // UniTask 的 SendWebRequest 扩展会在取消时 Abort 请求
                await req.SendWebRequest().WithCancellation(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // 区分超时还是外部取消：若外部 token 未取消，则定性为超时
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException($"Handwriting recognition request timed out after {config.timeoutSeconds:F1}s.");
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                throw new Exception($"HTTP {(int)req.responseCode} {req.error}: {req.downloadHandler?.text}");
            }

            string responseText = req.downloadHandler.text;
            string content = ExtractAssistantContent(responseText);
            return content?.Trim() ?? string.Empty;
        }

        #region 请求体构建

        /// <summary>
        /// 构建 OpenAI Chat Completions 兼容的请求体。包含一张 base64 JPG 图。
        /// 直接拼装 JSON 字符串：可控、低 GC，避免引入 JSON 库的依赖；字段不多。
        /// </summary>
        private static string BuildRequestBody(HandwritingConfig config, byte[] jpgBytes)
        {
            string base64 = Convert.ToBase64String(jpgBytes);
            string detail = DetailToString(config.imageDetail);

            // 估算容量：base64 + prompt + 固定模板
            int estCap = base64.Length + (config.prompt?.Length ?? 0) + 320;
            var sb = GetBuilder(estCap);

            sb.Append('{');
            sb.Append("\"model\":\""); AppendJsonString(sb, config.modelName); sb.Append("\",");
            sb.Append("\"temperature\":0");
            if (config.maxTokens > 0)
            {
                sb.Append(",\"max_tokens\":"); sb.Append(config.maxTokens);
            }
            if (config.disableThinking)
            {
                // 硅基流动文档：enable_thinking=false 关闭思考链路（仅对支持思考的模型生效，其它模型会被忽略）
                sb.Append(",\"enable_thinking\":false");
            }
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":[");
            // 图像部分在前（更符合多数多模态模型的训练习惯，且与官方示例一致）
            sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/jpeg;base64,");
            sb.Append(base64);
            sb.Append("\",\"detail\":\""); sb.Append(detail); sb.Append("\"}},");
            // 文本部分在后
            sb.Append("{\"type\":\"text\",\"text\":\""); AppendJsonString(sb, config.prompt ?? string.Empty); sb.Append("\"}");
            sb.Append("]}]}");
            return sb.ToString();
        }

        private static string DetailToString(ImageDetail d) => d switch
        {
            ImageDetail.Low => "low",
            ImageDetail.High => "high",
            _ => "auto",
        };

        private static StringBuilder GetBuilder(int estCapacity)
        {
            var sb = _tlsBuilder;
            if (sb == null)
            {
                _tlsBuilder = sb = new StringBuilder(Mathf.Max(1024, estCapacity));
            }
            else
            {
                sb.Clear();
                if (sb.Capacity < estCapacity) sb.Capacity = estCapacity;
            }
            return sb;
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }

        #endregion

        #region 响应解析

        /// <summary>
        /// 从 OpenAI Chat Completions 响应中提取 choices[0].message.content。
        /// 实现策略：定位到 "message":{...} 对象的内部，仅在该范围内查找 "content"。
        /// 这样可避免误中 reasoning_content / 请求 echo 中的 content 字段等。
        /// </summary>
        private static string ExtractAssistantContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;

            // 1) 找 "message" 键
            int msgKey = json.IndexOf("\"message\"", StringComparison.Ordinal);
            if (msgKey < 0)
            {
                // 兼容退化：直接找首个 content
                return ExtractFirstContent(json, 0);
            }
            int colon = json.IndexOf(':', msgKey);
            if (colon < 0) return string.Empty;
            int p = SkipWhitespace(json, colon + 1);
            if (p >= json.Length || json[p] != '{') return ExtractFirstContent(json, p);
            int msgEnd = FindMatching(json, p, '{', '}');
            if (msgEnd < 0) return string.Empty;

            // 2) 在 message 对象范围内查找 content
            return ExtractFirstContent(json, p, msgEnd);
        }

        /// <summary>
        /// 在 json[searchStart..searchEnd] 范围内寻找第一个 "content" 字段，并返回其字符串值或数组的 text 拼接。
        /// searchEnd = -1 表示扫到结尾。
        /// </summary>
        private static string ExtractFirstContent(string json, int searchStart, int searchEnd = -1)
        {
            int end = searchEnd < 0 ? json.Length : searchEnd;
            int idx = json.IndexOf("\"content\"", searchStart, end - searchStart, StringComparison.Ordinal);
            while (idx >= 0 && idx < end)
            {
                int colon = json.IndexOf(':', idx);
                if (colon < 0 || colon >= end) return string.Empty;
                int p = SkipWhitespace(json, colon + 1);
                if (p >= end) return string.Empty;

                if (json[p] == '"')
                {
                    return TryReadJsonString(json, p, out var s, out _) ? s : string.Empty;
                }
                if (json[p] == '[')
                {
                    return ExtractTextFromContentArray(json, p);
                }
                // null 或 object —— 继续寻找下一个 content
                int next = p + 1;
                idx = json.IndexOf("\"content\"", next, end - next, StringComparison.Ordinal);
            }
            return string.Empty;
        }

        /// <summary>
        /// 解析 content 是数组形式（多模态返回）的情况：拼接所有 type="text" 的 text 字段。
        /// </summary>
        private static string ExtractTextFromContentArray(string json, int arrayStart)
        {
            int p = arrayStart + 1;
            int depth = 1;
            StringBuilder buf = null;
            while (p < json.Length && depth > 0)
            {
                char c = json[p];
                if (c == '[') { depth++; p++; continue; }
                if (c == ']') { depth--; p++; continue; }
                if (c == '{')
                {
                    int objEnd = FindMatching(json, p, '{', '}');
                    if (objEnd < 0) break;
                    int textKey = json.IndexOf("\"text\"", p, objEnd - p, StringComparison.Ordinal);
                    if (textKey >= 0)
                    {
                        int objColon = json.IndexOf(':', textKey);
                        if (objColon > 0 && objColon < objEnd)
                        {
                            int q = SkipWhitespace(json, objColon + 1);
                            if (q < objEnd && json[q] == '"' && TryReadJsonString(json, q, out var s, out _))
                            {
                                if (buf == null) buf = new StringBuilder(64);
                                buf.Append(s);
                            }
                        }
                    }
                    p = objEnd + 1;
                    continue;
                }
                p++;
            }
            return buf?.ToString() ?? string.Empty;
        }

        private static int SkipWhitespace(string s, int p)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
            return p;
        }

        private static int FindMatching(string s, int start, char open, char close)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 从 start 位置（必须指向 '"'）读取一个 JSON 字符串，返回解码后的内容与结束位置（含右引号）。
        /// </summary>
        private static bool TryReadJsonString(string s, int start, out string value, out int endIndex)
        {
            value = null;
            endIndex = -1;
            if (start < 0 || start >= s.Length || s[start] != '"') return false;

            var sb = new StringBuilder(64);
            int i = start + 1;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\')
                {
                    if (i + 1 >= s.Length) return false;
                    char n = s[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); i += 2; break;
                        case '\\': sb.Append('\\'); i += 2; break;
                        case '/': sb.Append('/'); i += 2; break;
                        case 'b': sb.Append('\b'); i += 2; break;
                        case 'f': sb.Append('\f'); i += 2; break;
                        case 'n': sb.Append('\n'); i += 2; break;
                        case 'r': sb.Append('\r'); i += 2; break;
                        case 't': sb.Append('\t'); i += 2; break;
                        case 'u':
                            if (i + 5 >= s.Length) return false;
                            if (!int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int code))
                                return false;
                            sb.Append((char)code);
                            i += 6;
                            break;
                        default:
                            return false;
                    }
                }
                else if (c == '"')
                {
                    value = sb.ToString();
                    endIndex = i;
                    return true;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return false;
        }

        #endregion
    }
}
