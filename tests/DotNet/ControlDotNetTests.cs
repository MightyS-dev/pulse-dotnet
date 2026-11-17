using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ControlDotNetTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            internal HttpRequestMessage Last;
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { Last = request; return Reply(request, cancellationToken); }
        }

        private static HttpResponseMessage Json(string body) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [Fact]
        public async Task The_control_request_carries_no_app_key_and_no_identity_header_but_batches_still_do()
        {
            var handler = new Handler { Reply = (r, c) => Task.FromResult(Json(ControlBodies.Rules())) };
            using var transport = new HttpClientTransport(handler);
            var url = new Uri("https://in.mightys.dev/api/v1/control/0123456789abcdef0123456789abcdef");
            var response = await transport.SendAsync(TransportRequest.ForControl(url, "\"5\""), CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
            var names = handler.Last.Headers.Select(h => h.Key).ToArray();
            Assert.Equal(HttpMethod.Get, handler.Last.Method);
            Assert.DoesNotContain("X-App-Key", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Pulse-Batch", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", names, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cookie", names, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("\"5\"", handler.Last.Headers.GetValues("If-None-Match").Single());
            Assert.Equal("application/json", handler.Last.Headers.GetValues("Accept").Single());
            Assert.Null(handler.Last.Content);

            await transport.SendAsync(new TransportRequest(new Uri("https://in.mightys.dev/api/v1/signals"), "key-1", new byte[] { 1 }, false, "0123456789abcdef0123456789abcdef"), CancellationToken.None);
            Assert.Equal("key-1", handler.Last.Headers.GetValues("X-App-Key").Single());
        }

        [Fact]
        public async Task A_control_answer_over_65536_bytes_is_dropped_by_the_transport()
        {
            var handler = new Handler { Reply = (r, c) => Task.FromResult(Json(new string('a', 100000))) };
            using var transport = new HttpClientTransport(handler);
            var response = await transport.SendAsync(TransportRequest.ForControl(new Uri("https://in.mightys.dev/api/v1/control/x"), null), CancellationToken.None);
            Assert.Equal("", response.Body);
        }

        [Fact]
        public async Task A_control_request_without_response_headers_ends_after_three_seconds()
        {
            var handler = new Handler { Reply = async (r, c) => { await Task.Delay(TimeSpan.FromSeconds(20), c); return Json("{}"); } };
            using var transport = new HttpClientTransport(handler);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var response = await transport.SendAsync(TransportRequest.ForControl(new Uri("https://in.mightys.dev/api/v1/control/x"), null), CancellationToken.None);
            Assert.Equal(0, response.StatusCode);
            Assert.InRange(timer.Elapsed.TotalSeconds, 2.5, 4.5);
        }

        [Fact]
        public void The_default_transport_is_marked_for_control()
        {
            using var transport = new HttpClientTransport();
            Assert.IsAssignableFrom<IControlTransport>(transport);
        }

        private static PulsePlatformOptions Ios(Func<string, bool> open = null) => new PulsePlatformOptions
        {
            EnvironmentInfo = new DotNetEnvironmentInfo("iOS", "17.0", "1.0.0", "7", "en-US", "iPhone"), OpenUrl = open
        };

        [Fact]
        public async Task The_host_shows_state_raises_changed_dismisses_and_opens_the_store_through_the_platform_option()
        {
            var transport = new ControlTransport();
            transport.Answer(200, ControlBodies.Rules(update: ControlBodies.SuggestedIos));
            var opened = new List<string>();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            var changes = new List<ControlState>();
            host.Runtime.Control.Changed += (s, state) => { lock (changes) changes.Add(state); };
            Assert.Equal(ControlKind.Normal, host.Runtime.Control.State.Kind);
            host.Initialize("synthetic-key", new PulseOptions { FlushIntervalSeconds = 3600 }, Ios(url => { opened.Add(url); return true; }));
            try
            {
                await ControlRig.Until(() => host.Runtime.Control.State.Kind == ControlKind.UpdateSuggested);
                Assert.Single(changes);
                Assert.True(host.Runtime.Control.OpenStore());
                Assert.Equal(new[] { "https://apps.apple.com/app/id1" }, opened.ToArray());
                host.Runtime.Control.Dismiss();
                Assert.Equal(ControlKind.Normal, host.Runtime.Control.State.Kind);
                await host.Runtime.Control.CheckNowAsync();
                Assert.Equal(ControlKind.Normal, host.Runtime.Control.State.Kind);
                Assert.Equal(2, transport.Count);
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task The_option_off_makes_no_control_request_on_the_host()
        {
            var transport = new ControlTransport();
            transport.Answer(200, ControlBodies.Rules(update: ControlBodies.RequiredIos));
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize("synthetic-key", new PulseOptions { Control = ControlMode.Off }, Ios());
            try
            {
                await host.Runtime.Control.CheckNowAsync();
                host.Runtime.ReportForeground(true);
                await Task.Delay(150);
                Assert.Equal(0, transport.Count);
                Assert.Equal(ControlKind.Normal, host.Runtime.Control.State.Kind);
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task Control_works_with_analytics_opted_out_on_the_host()
        {
            var transport = new ControlTransport();
            transport.Answer(200, ControlBodies.Rules(update: ControlBodies.RequiredIos));
            var storage = new MemoryStorage();
            storage.Files["opt-out"] = new byte[] { 1 };
            var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Initialize("synthetic-key", new PulseOptions(), Ios());
            try
            {
                await ControlRig.Until(() => host.Runtime.Control.State.Kind == ControlKind.UpdateRequired);
                Assert.True(host.Runtime.OptOut);
                Assert.True(transport.Requests.TryPeek(out var request));
                Assert.Null(request.AppKey);
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public void The_static_facade_is_safe_before_initialize()
        {
            Assert.NotNull(Pulse.Control);
            Assert.Equal(ControlKind.Normal, Pulse.Control.State.Kind);
            Assert.False(Pulse.Control.OpenStore());
            Pulse.Control.Dismiss();
            Pulse.Control.OnForeground();
            Assert.True(Pulse.Control.CheckNowAsync().IsCompleted);
        }

        [Fact]
        public async Task Losing_and_gaining_focus_never_throws_with_control_running()
        {
            var transport = new ControlTransport();
            transport.Answer(404);
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize("synthetic-key", new PulseOptions(), Ios());
            try
            {
                await host.FocusChangedAsync(false);
                await host.FocusChangedAsync(true);
            }
            finally { await host.ShutdownAsync(); }
        }
    }
}
