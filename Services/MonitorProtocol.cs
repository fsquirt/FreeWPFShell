using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeWPFShell.Services
{
    /// <summary>
    /// linux-monitor 探针的纯 TCP 通信协议（替代原 HTTP 实现，Rust 端不再依赖 tiny_http）。
    /// 帧格式：[4 字节大端长度][payload UTF-8]。
    /// 请求为 JSON 信封 {"op":"...","token":"...",...参数}；响应体与旧 HTTP 响应一致（JSON / "true"/"false" / 纯文本），
    /// 鉴权失败或未知 op 返回 {"err":"..."}。
    /// </summary>
    public static class MonitorProtocol
    {
        /// <summary>读帧上限，防止垃圾数据导致无界内存分配。</summary>
        public const int MaxFrameLength = 64 * 1024 * 1024;

        /// <summary>请求超时，对齐原 HttpClient.Timeout = 10s。</summary>
        public const int DefaultTimeoutMs = 10_000;

        #region 帧编解码

        public static void WriteFrame(Stream stream, byte[] payload)
        {
            if (payload.Length == 0 || payload.Length > MaxFrameLength)
                throw new IOException($"非法帧长度: {payload.Length}");
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)payload.Length);
            stream.Write(lenBytes, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static byte[] ReadFrame(Stream stream)
        {
            Span<byte> lenBytes = stackalloc byte[4];
            ReadExactly(stream, lenBytes);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            if (len == 0 || len > MaxFrameLength)
                throw new IOException($"非法帧长度: {len}");
            var buf = new byte[len];
            ReadExactly(stream, buf);
            return buf;
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.Read(buffer[offset..]);
                if (n <= 0) throw new IOException("连接在读取帧数据时中断");
                offset += n;
            }
        }

        private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
        {
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)payload.Length);
            await stream.WriteAsync(lenBytes, ct);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
                if (n <= 0) throw new IOException("连接在读取帧数据时中断");
                offset += n;
            }
            return buf;
        }

        private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            var lenBytes = await ReadExactlyAsync(stream, 4, ct);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            if (len == 0 || len > MaxFrameLength)
                throw new IOException($"非法帧长度: {len}");
            return await ReadExactlyAsync(stream, (int)len, ct);
        }

        #endregion

        /// <summary>构造请求信封：{"op":...,"token":...,...args}（扁平结构，与 Rust 端解析对应）。</summary>
        public static byte[] BuildEnvelope(string token, string op, IReadOnlyDictionary<string, object?>? args = null)
        {
            var envelope = new Dictionary<string, object?>(args?.Count + 2 ?? 2)
            {
                ["op"] = op,
                ["token"] = token ?? ""
            };
            if (args != null)
            {
                foreach (var kv in args) envelope[kv.Key] = kv.Value;
            }
            return JsonSerializer.SerializeToUtf8Bytes(envelope);
        }

        /// <summary>
        /// 发送一次请求（短连接，语义对齐原 HttpClient.GetStringAsync：失败抛异常，返回响应体字符串）。
        /// op=exit 时写入请求后立即关闭，不等待响应（agent 收到即退出）。
        /// </summary>
        public static async Task<string> SendRequestAsync(string host, int port, string token, string op,
            IReadOnlyDictionary<string, object?>? args = null, int timeoutMs = DefaultTimeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"连接监控探针超时 ({host}:{port})");
            }

            await using var stream = client.GetStream();
            await WriteFrameAsync(stream, BuildEnvelope(token, op, args), cts.Token);

            if (op == "exit") return "";

            byte[] body;
            try
            {
                body = await ReadFrameAsync(stream, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"等待监控探针响应超时 (op={op})");
            }

            var text = Encoding.UTF8.GetString(body);
            ThrowIfErrResponse(text);
            return text;
        }

        /// <summary>探针错误响应 {"err":"..."} 对齐原 HTTP 非 2xx 抛异常的语义。</summary>
        private static void ThrowIfErrResponse(string text)
        {
            if (text.Length < 8 || text[0] != '{' || !text.Contains("\"err\"")) return;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String)
                    throw new IOException($"监控探针返回错误: {err.GetString()}");
            }
            catch (JsonException) { }
        }
    }
}
