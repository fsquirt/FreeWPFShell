using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeWPFShell.Services;

namespace FreeWPFShell.Tests.Services
{

    [TestClass]
    public class MonitorProtocolTests
    {
        [TestMethod]
        public void Frame_RoundTrip_PreservesPayload()
        {
            var payload = Encoding.UTF8.GetBytes("{\"cpu_pct\":42.5}");
            using var ms = new MemoryStream();
            MonitorProtocol.WriteFrame(ms, payload);
            ms.Position = 0;
            CollectionAssert.AreEqual(payload, MonitorProtocol.ReadFrame(ms));
        }

        [TestMethod]
        public void WriteFrame_UsesBigEndianLengthPrefix()
        {
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            using var ms = new MemoryStream();
            MonitorProtocol.WriteFrame(ms, payload);
            var bytes = ms.ToArray();

            Assert.AreEqual(0, bytes[0], "大端序第 1 字节应为 0");
            Assert.AreEqual(0, bytes[1], "大端序第 2 字节应为 0");
            Assert.AreEqual(0, bytes[2], "大端序第 3 字节应为 0");
            Assert.AreEqual(5, bytes[3], "大端序第 4 字节应为长度 5");
        }

        [TestMethod]
        public void ReadFrame_RejectsZeroLength()
        {
            using var ms = new MemoryStream(new byte[] { 0, 0, 0, 0, 1, 2 });
            Assert.ThrowsException<IOException>(() => MonitorProtocol.ReadFrame(ms));
        }

        [TestMethod]
        public void ReadFrame_RejectsOversizedLength()
        {

            using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2 });
            Assert.ThrowsException<IOException>(() => MonitorProtocol.ReadFrame(ms));
        }

        [TestMethod]
        public void BuildEnvelope_ContainsOpTokenAndArgs()
        {
            var bytes = MonitorProtocol.BuildEnvelope("tok-123", "kill", new Dictionary<string, object?>
            {
                ["pid"] = 4242u,
                ["sig"] = 9
            });

            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            Assert.AreEqual("kill", root.GetProperty("op").GetString());
            Assert.AreEqual("tok-123", root.GetProperty("token").GetString());
            Assert.AreEqual(4242u, root.GetProperty("pid").GetUInt32());
            Assert.AreEqual(9, root.GetProperty("sig").GetInt32());
        }

        [TestMethod]
        public async Task SendRequestAsync_FullChain_ReturnsBody()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var serverTask = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var request = MonitorProtocol.ReadFrame(stream);
                    using var doc = JsonDocument.Parse(request);
                    Assert.AreEqual("stats", doc.RootElement.GetProperty("op").GetString());
                    Assert.AreEqual("secret", doc.RootElement.GetProperty("token").GetString());
                    MonitorProtocol.WriteFrame(stream, Encoding.UTF8.GetBytes("{\"cpu_pct\":1.5}"));
                });

                var body = await MonitorProtocol.SendRequestAsync("127.0.0.1", port, "secret", "stats");
                await serverTask;

                Assert.AreEqual("{\"cpu_pct\":1.5}", body);
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task SendRequestAsync_ErrResponse_Throws()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                var serverTask = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    MonitorProtocol.ReadFrame(stream);
                    MonitorProtocol.WriteFrame(stream, Encoding.UTF8.GetBytes("{\"err\":\"unauthorized\"}"));
                });

                await Assert.ThrowsExceptionAsync<IOException>(
                    () => MonitorProtocol.SendRequestAsync("127.0.0.1", port, "bad-token", "stats"));
                await serverTask;
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task SendRequestAsync_ExitOp_DoesNotWaitForResponse()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;


                var serverTask = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    MonitorProtocol.ReadFrame(stream);
                });

                var body = await MonitorProtocol.SendRequestAsync("127.0.0.1", port, "secret", "exit", timeoutMs: 2000);
                await serverTask;

                Assert.AreEqual("", body);
            }
            finally
            {
                listener.Stop();
            }
        }

        [TestMethod]
        public async Task SendRequestAsync_ServerSilent_TimesOut()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;


                var serverTask = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await Task.Delay(2000);
                });

                await Assert.ThrowsExceptionAsync<TimeoutException>(
                    () => MonitorProtocol.SendRequestAsync("127.0.0.1", port, "secret", "stats", timeoutMs: 500));
                await serverTask;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
