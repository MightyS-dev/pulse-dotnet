using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class AdapterTests
    {
        [Fact]
        public void TestsLoadTheIntendedBinaryTarget()
        {
            var target = typeof(Pulse).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
                .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName;
#if PULSE_TEST_NETSTANDARD
            Assert.Equal(".NETStandard,Version=v2.0", target);
#else
            Assert.Equal(".NETCoreApp,Version=v8.0", target);
#endif
        }

        // Regression: the build-time core adaptation once turned '\r' and '\n' char literals into '/r' and '/n' on
        // Unix (MSBuild rewrites backslashes in task items), so the generated assembly did not compile on macOS.
        [Theory]
        [InlineData("bad\rkey")]
        [InlineData("bad\nkey")]
        public void AdaptedCoreRejectsKeysWithLineBreaks(string key)
        {
            var h = new Harness(initialize: false);
            h.Runtime.Initialize(key);
            Assert.Null(h.Client);
            h.Runtime.Initialize("slash/r-and-slash/n-are-valid");
            Assert.NotNull(h.Client);
        }

        [Fact]
        public async Task ActualAssemblyMatchesV1FixtureAndDotNetIdentityWithInvariantJson()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sr-RS");
                var h = new Harness(); h.Runtime.SetUser("synthetic-person");
                await h.Send("purchase", 4.5, new Dictionary<string, object> { ["item"] = "coins", ["amount"] = 2 });
                var actual = Harness.Event(Assert.Single(h.Transport.Requests));
                using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/v1.jsonc")),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                Assert.Equal(Names(fixture.RootElement[0]), Names(actual));
                // Every fixture field is present; anything extra is an optional system field from the fixed list.
                var extra = Names(actual.GetProperty("system")).Except(Names(fixture.RootElement[0].GetProperty("system"))).ToArray();
                Assert.Empty(Names(fixture.RootElement[0].GetProperty("system")).Except(Names(actual.GetProperty("system"))));
                Assert.All(extra, key => Assert.True(key == "bundleId" || key == "signingCertSha256", key));
                Assert.Equal("pulse-dotnet", actual.GetProperty("system").GetProperty("sdkName").GetString());
                Assert.Equal("0.9.5", actual.GetProperty("system").GetProperty("sdkVersion").GetString());
                Assert.EndsWith("Z", actual.GetProperty("timestamp").GetString());
                Assert.Equal(4.5, actual.GetProperty("value").GetDouble());
                Assert.DoesNotContain("synthetic-person", actual.GetRawText());
                Assert.Equal(64, actual.GetProperty("userHash").GetString().Length);
                var text = "quote\"slash\\\n\t\u017e\U0001f680";
                await h.Send(text, props: new Dictionary<string, object> { [text] = text, ["bool"] = true, ["decimal"] = 1.25m });
                var escaped = Harness.Event(h.Transport.Requests.Last());
                Assert.Equal(text, escaped.GetProperty("type").GetString());
                Assert.Equal(text, escaped.GetProperty("props").GetProperty(text).GetString());
                Assert.Equal("true", escaped.GetProperty("props").GetProperty("bool").GetString());
                Assert.Equal(1.25m, escaped.GetProperty("props").GetProperty("decimal").GetDecimal());
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        private static string[] Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        [Theory]
        [InlineData("Windows", "Windows 11.0 build 123", "desktop", "Windows", "11.0")]
        [InlineData("MacCatalyst", "18.2 (build private)", "desktop", "macOS", "18.2")]
        [InlineData("Linux", "6.8.0-custom", "desktop", "Linux", "6.8")]
        [InlineData("Android", "Android OS 14 / API-34 (private)", "mobile", "Android", "14")]
        [InlineData("iOS", "iOS 18.0.3 (private)", "mobile", "iOS", "18.0.3")]
        [InlineData("untrusted-name", "private", "desktop", "", "")]
        public void EnvironmentUsesOnlyVersionNumberAndNoIdentifiers(string os, string version, string platform, string name, string number)
        {
            var info = new DotNetEnvironmentInfo(os, version, "1.2.3", "42", "sr-RS");
            Assert.Equal(platform, info.Platform); Assert.Equal(name, info.OsName); Assert.Equal(number, info.OsVersion);
            Assert.Equal("1.2.3", info.AppVersion); Assert.Equal("42", info.AppBuildNumber); Assert.Equal("sr-RS", info.Locale);
            Assert.Empty(info.BundleId); Assert.Empty(info.SigningCertSha256);
            Assert.DoesNotContain("private", SignalJson.SystemJson(info, new PulseOptions()));
        }

        [Fact]
        public void DefaultStorageIsAppScopedInsideLocalAppData()
        {
            var root = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var path = Path.GetFullPath(PulseStorage.DefaultDirectory);
            Assert.StartsWith(root + Path.DirectorySeparatorChar, path);
            Assert.Contains(Path.Combine("MightyS", "Pulse"), path);
            Assert.Equal(64, Path.GetFileName(path).Length);
        }

        [Theory]
        [InlineData(500)] [InlineData(429)] [InlineData(503)] [InlineData(415)]
        public async Task RealHttpTransportGzipRetryHeadersAndAdvice(int initialStatus)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var host = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = new List<(Dictionary<string, string> Headers, byte[] Body)>();
            var server = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    using var stream = connection.GetStream();
                    var request = await ReadRequest(stream); received.Add(request);
                    var status = attempt == 0 ? initialStatus : 202;
                    var advice = Encoding.UTF8.GetBytes(attempt == 0 ?
                        "{\"recommendedSendIntervalSeconds\":5,\"pause\":false}" :
                        "{\"recommendedSendIntervalSeconds\":60,\"pause\":true}");
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Length: " + advice.Length +
                        "\r\nConnection: close\r\nRetry-After: 2\r\n\r\n");
                    await stream.WriteAsync(header); await stream.WriteAsync(advice);
                }
            });
            using var transport = new HttpClientTransport(); var clock = new FakeClock();
            var client = new PulseClient("synthetic-http-key", new PulseOptions { Host = host }, clock, transport,
                new MemoryStorage(), new DotNetEnvironmentInfo(), runWorker: false, random: () => 0.5);
            try
            {
                client.Enqueue(SignalJson.Create("transport", null, new Dictionary<string, object> { ["x"] = new string('a', 2000) },
                    clock.UtcNow, "synthetic-session", null, _ => { }));
                await client.ProcessAsync(true);
                if (initialStatus != 415) { clock.Advance(5); await client.ProcessAsync(true); }
                await server.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, received.Count); Assert.Equal(0, client.Count);
                Assert.All(received, r =>
                {
                    Assert.Equal("POST /api/v1/signals HTTP/1.1", r.Headers["request"]);
                    Assert.Equal("synthetic-http-key", r.Headers["X-App-Key"]);
                    Assert.Equal("application/json", r.Headers["Content-Type"]);
                    Assert.False(r.Headers.ContainsKey("Cookie")); Assert.False(r.Headers.ContainsKey("User-Agent"));
                });
                Assert.Equal("gzip", received[0].Headers["Content-Encoding"]);
                if (initialStatus == 415)
                {
                    Assert.False(received[1].Headers.ContainsKey("Content-Encoding"));
                    Assert.Equal(Harness.JsonBytes(new TransportRequest(new Uri(host), "", received[0].Body, true)), received[1].Body);
                }
                else Assert.Equal(received[0].Body, received[1].Body);
                client.Enqueue(SignalJson.Create("paused", null, null, clock.UtcNow, "session", null, _ => { }));
                await client.ProcessAsync(true); Assert.Equal(1, client.Count);
            }
            finally { await client.StopAsync(); }
        }

        [Fact]
        public async Task RedirectIsReturnedWithoutFollowingOrLeakingKey()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var url = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/api/v1/signals");
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var stream = connection.GetStream(); await ReadRequest(stream);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            });
            using var transport = new HttpClientTransport();
            var response = await transport.SendAsync(new TransportRequest(url, "synthetic", Encoding.UTF8.GetBytes("[]"), false), CancellationToken.None);
            await server; Assert.Equal(302, response.StatusCode); Assert.False(listener.Pending());
        }

        [Fact]
        public async Task CancellationAndNetworkFailureReturnRetryableZero()
        {
            using var transport = new HttpClientTransport(new StubHandler(async (_, token) =>
            { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.Accepted); }));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var request = new TransportRequest(new Uri("https://example.invalid"), "synthetic", new byte[] { 1 }, false);
            Assert.Equal(0, (await transport.SendAsync(request, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5))).StatusCode);
            using var broken = new HttpClientTransport(new StubHandler((_, __) => throw new HttpRequestException()));
            Assert.Equal(0, (await broken.SendAsync(request, CancellationToken.None)).StatusCode);
        }

        [Fact]
        public async Task PostCarriesBatchIdHeaderAndGetDoesNot()
        {
            HttpRequestMessage seen = null;
            using var transport = new HttpClientTransport(new StubHandler((message, _) =>
            { seen = message; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)); }));
            var id = new string('a', 32);
            await transport.SendAsync(new TransportRequest(new Uri("https://example.invalid"), "synthetic", new byte[] { 1 }, false, id), CancellationToken.None);
            Assert.Equal(id, Assert.Single(seen.Headers.GetValues("X-Pulse-Batch")));
            await transport.SendAsync(new TransportRequest(new Uri("https://example.invalid"), "synthetic", new byte[] { 1 }, false), CancellationToken.None);
            Assert.False(seen.Headers.Contains("X-Pulse-Batch"));
            await transport.SendAsync(TransportRequest.ForGet(new Uri("https://example.invalid/api/v1/config"), "synthetic", null), CancellationToken.None);
            Assert.False(seen.Headers.Contains("X-Pulse-Batch"));
        }

        [Fact]
        public async Task SettingsRequestIsAGetWithKeyAndValidatorAndNoBody()
        {
            HttpRequestMessage seen = null;
            using var transport = new HttpClientTransport(new StubHandler((message, _) =>
            { seen = message; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }));
            var request = TransportRequest.ForGet(new Uri("https://example.invalid/api/v1/config"), "synthetic", "\"3\"");
            var response = await transport.SendAsync(request, CancellationToken.None);
            Assert.Equal(304, response.StatusCode);
            Assert.Equal(HttpMethod.Get, seen.Method); Assert.Null(seen.Content);
            Assert.Equal("synthetic", seen.Headers.GetValues("X-App-Key").Single());
            Assert.Equal("\"3\"", seen.Headers.GetValues("If-None-Match").Single());
            seen = null;
            await transport.SendAsync(TransportRequest.ForGet(new Uri("https://example.invalid/api/v1/config"), "synthetic", null), CancellationToken.None);
            Assert.False(seen.Headers.Contains("If-None-Match"));
        }

        [Fact]
        public async Task HungTransportIsBoundedByRequestTimeoutKeepsQueueRetriesAndDeliversOnRecovery()
        {
            var calls = 0; var hungSince = DateTimeOffset.MinValue; var recoveredAt = DateTimeOffset.MinValue;
            using var transport = new HttpClientTransport(new StubHandler(async (message, token) =>
            {
                // The once-per-launch settings request is a GET; this test is about the signal batch.
                if (message.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    hungSince = DateTimeOffset.UtcNow;
                    await Task.Delay(Timeout.Infinite, token);   // the request never completes by itself
                }
                recoveredAt = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }));
            var storage = new MemoryStorage();
            var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions(), new PulsePlatformOptions
            { EnvironmentInfo = new DotNetEnvironmentInfo("Windows", "11", "1.2.3", "42", "en-US") });
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                host.Runtime.Track("hung"); var flush = host.Runtime.Flush();
                Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500), "Track and Flush must not block the caller");
                for (var i = 0; i < 500 && Volatile.Read(ref calls) == 0; i++) await Task.Delay(10);
                Assert.Equal(1, Volatile.Read(ref calls));
                await Task.Delay(300);
                var queue = storage.Files.Single(f => f.Key.EndsWith(".bin", StringComparison.Ordinal)).Value;
                Assert.Single(QueueJournal.Decode(queue, 1000));       // still in the persisted queue while the request hangs
                Assert.False(flush.IsCompleted);
                await flush.WaitAsync(TimeSpan.FromSeconds(40));       // timeout (10 s), backoff (about 1 s), then recovery
                Assert.Equal(2, Volatile.Read(ref calls));
                var hung = recoveredAt - hungSince;
                Assert.InRange(hung.TotalSeconds, 10.5, 30);           // bounded by the request timeout, then retried after a backoff
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task OversizedAdviceDoesNotAllocateUnboundedResponseOrLoseStatus()
        {
            using var transport = new HttpClientTransport(new StubHandler((_, __) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(new string('a', 65537)) })));
            var response = await transport.SendAsync(new TransportRequest(new Uri("https://example.invalid"), "synthetic", new byte[] { 1 }, false), CancellationToken.None);
            Assert.Equal(202, response.StatusCode); Assert.Empty(response.Body);
        }

        private static async Task<(Dictionary<string, string> Headers, byte[] Body)> ReadRequest(NetworkStream stream)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length > 16384 || await stream.ReadAsync(one, cancellation.Token) != 1) throw new IOException();
                header.Append((char)one[0]);
            }
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(s => s.Split(new[] { ':' }, 2)).ToDictionary(x => x[0], x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
            headers["request"] = lines[0]; var body = new byte[int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture)];
            var offset = 0;
            while (offset < body.Length)
            {
                var count = await stream.ReadAsync(body.AsMemory(offset), cancellation.Token);
                if (count == 0) throw new EndOfStreamException(); offset += count;
            }
            return (headers, body);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
            internal StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }
    }
}
