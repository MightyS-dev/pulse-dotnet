using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class HttpTransportTests
    {
        [Theory]
        [InlineData(500)] [InlineData(415)] [InlineData(429)] [InlineData(503)]
        public async Task RealHttpPreservesHeadersGzipRetryAndFallback(int firstStatus)
        {
            var portReservation = new TcpListener(IPAddress.Loopback, 0); portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
            var host = "http://127.0.0.1:" + port;
            using var listener = new HttpListener(); listener.Prefixes.Add(host + "/");
            var useHttpListener = true;
            try { listener.Start(); }
            catch (HttpListenerException) { useHttpListener = false; }
            // Windows HTTP.sys handles are unavailable in some sandboxes. Still run a real HTTP exchange.
            using var socketListener = new TcpListener(IPAddress.Loopback, port);
            if (!useHttpListener) socketListener.Start();
            var received = new List<(string Path, string Key, string Type, string Encoding, byte[] Body)>();
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var status = i == 0 ? firstStatus : 202;
                    var body = i == 0 ? (firstStatus == 429 || firstStatus == 503 ?
                        "{\"recommendedSendIntervalSeconds\":30,\"pause\":false,\"error\":\"busy\"}" : "") : i == 1 ?
                        "{\"recommendedSendIntervalSeconds\":60,\"pause\":true,\"error\":null,\"unknown\":true}" :
                        "{\"recommendedSendIntervalSeconds\":20,\"pause\":false,\"error\":null}";
                    var response = Encoding.UTF8.GetBytes(body);
                    if (useHttpListener)
                    {
                        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        using var buffer = new MemoryStream(); await context.Request.InputStream.CopyToAsync(buffer);
                        received.Add((context.Request.Url.AbsolutePath, context.Request.Headers["X-App-Key"],
                            context.Request.ContentType, context.Request.Headers["Content-Encoding"], buffer.ToArray()));
                        context.Response.StatusCode = status;
                        if (i == 0) context.Response.Headers["Retry-After"] = "2";
                        context.Response.ContentLength64 = response.Length;
                        await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
                    }
                    else
                    {
                        using var connection = await socketListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        using var stream = connection.GetStream();
                        var request = await ReadSocketRequest(stream);
                        received.Add(request);
                        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Length: " + response.Length +
                            "\r\nConnection: close\r\n" + (i == 0 ? "Retry-After: 2\r\n" : "") + "\r\n");
                        await stream.WriteAsync(header); await stream.WriteAsync(response); await stream.FlushAsync();
                    }
                }
            });
            using var transport = new HttpClientTestTransport(); var clock = new FakeClock();
            var client = new PulseClient("synthetic-http-key", new PulseOptions { Host = host }, clock, transport,
                new MemoryStorage(), new FakeEnvironment(), runWorker: false, random: () => 0.5);
            client.Enqueue(SignalJson.Create("http.test", 4.99, new Dictionary<string, object> { ["large"] = new string('a', 2000) },
                clock.UtcNow, "synthetic-session", null, _ => { }));
            await client.ProcessAsync(true);
            if (firstStatus != 415)
            { clock.Advance(firstStatus == 500 ? 2 : 30); await client.ProcessAsync(true); }
            Assert.Equal(2, transport.SendCount); Assert.Equal(0, client.Count);
            client.Enqueue(SignalJson.Create("after.pause", null, null, clock.UtcNow, "synthetic-session", null, _ => { }));
            clock.Advance(59.999); await client.ProcessAsync(true);
            Assert.Equal(2, transport.SendCount); Assert.Equal(1, client.Count);
            clock.Advance(0.001); await client.ProcessAsync(true);
            await server.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(3, received.Count); Assert.Equal(0, client.Count);
            Assert.All(received, r => { Assert.Equal("/api/v1/signals", r.Path); Assert.Equal("synthetic-http-key", r.Key); Assert.Equal("application/json", r.Type); });
            Assert.Equal("gzip", received[0].Encoding);
            if (firstStatus != 415) { Assert.Equal("gzip", received[1].Encoding); Assert.Equal(received[0].Body, received[1].Body); }
            else
            {
                Assert.Null(received[1].Encoding);
                Assert.Equal(Harness.JsonBytes(new TransportRequest(new Uri(host), "", received[0].Body, true)), received[1].Body);
            }
            await client.StopAsync();
        }

        private static async Task<(string Path, string Key, string Type, string Encoding, byte[] Body)> ReadSocketRequest(NetworkStream stream)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length > 16384 || await stream.ReadAsync(one, cancellation.Token) != 1) throw new IOException("Invalid test HTTP request.");
                header.Append((char)one[0]);
            }
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(line => line.Split(new[] { ':' }, 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var body = new byte[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
            var position = 0;
            while (position < body.Length)
            {
                var read = await stream.ReadAsync(body.AsMemory(position), cancellation.Token);
                if (read == 0) throw new EndOfStreamException(); position += read;
            }
            return (lines[0].Split(' ')[1], headers["X-App-Key"], headers["Content-Type"],
                headers.TryGetValue("Content-Encoding", out var encoding) ? encoding : null, body);
        }

        // This adapter exercises the core HTTP contract.
        private sealed class HttpClientTestTransport : ITransport, IDisposable
        {
            private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            internal int SendCount { get; private set; }
            public async Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
            {
                SendCount++;
                using var message = new HttpRequestMessage(HttpMethod.Post, request.Url);
                message.Headers.Add("X-App-Key", request.AppKey); message.Content = new ByteArrayContent(request.Body);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                if (request.IsGzip) message.Content.Headers.ContentEncoding.Add("gzip");
                using var response = await http.SendAsync(message, cancellationToken);
                return new TransportResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken),
                    response.Headers.TryGetValues("Retry-After", out var retry) ? retry.Single() : null);
            }
            public void Dispose() { http.Dispose(); }
        }
    }
}
