using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ControlClientTests
    {
        private static readonly string Locked = ControlBodies.Rules(update: ControlBodies.RequiredIos);

        // ---- group 3: seven failures ----

        public static IEnumerable<object[]> Failures()
        {
            yield return new object[] { "server not running", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(0))) };
            yield return new object[] { "DNS error", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => throw new HttpRequestException("name not resolved")) };
            yield return new object[] { "TLS error", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromException<TransportResponse>(new AuthenticationException("bad certificate"))) };
            yield return new object[] { "HTTP 500", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(500, "oops"))) };
            yield return new object[] { "HTTP 404", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(404))) };
            yield return new object[] { "HTTP 403", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(403, Locked))) };
            yield return new object[] { "not JSON", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(200, "<html>maintenance</html>"))) };
            yield return new object[] { "JSON array", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(200, "[1,2]"))) };
            yield return new object[] { "format 2", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(200, Locked.Replace("\"format\":1", "\"format\":2")))) };
            yield return new object[] { "10 MB body", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(200, new string('x', 10 * 1024 * 1024)))) };
            yield return new object[] { "unknown field type", (Func<TransportRequest, CancellationToken, Task<TransportResponse>>)((r, c) => Task.FromResult(new TransportResponse(200, Locked.Replace("\"off\":false", "\"off\":\"maybe\"")))) };
        }

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task A_failed_check_without_a_cache_leaves_the_state_normal_and_shows_nothing(string name, Func<TransportRequest, CancellationToken, Task<TransportResponse>> handler)
        {
            var rig = new ControlRig();
            rig.Transport.Handler = handler;
            await rig.Run();
            Assert.True(rig.Client.State.Kind == ControlKind.Normal, name);
            Assert.Equal(0, rig.ChangeCount);
            Assert.Equal(1, rig.Transport.Count);
            await AssertNoRetryUntilNextForeground(rig);
        }

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task A_failed_check_keeps_the_last_known_lock_from_the_cache(string name, Func<TransportRequest, CancellationToken, Task<TransportResponse>> handler)
        {
            var rig = new ControlRig();
            rig.SeedCache(Locked, TimeSpan.FromDays(1));
            rig.Transport.Handler = handler;
            await rig.Run();
            Assert.True(rig.Client.State.Kind == ControlKind.UpdateRequired, name);
            Assert.Equal(1, rig.Transport.Count);
        }

        private static async Task AssertNoRetryUntilNextForeground(ControlRig rig)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(10));
            rig.Client.OnForeground();
            await Task.Delay(150);
            Assert.Equal(1, rig.Transport.Count);
            rig.Clock.Advance(TimeSpan.FromSeconds(61));
            rig.Client.OnForeground();
            await ControlRig.Until(() => rig.Transport.Count == 2);
        }

        [Fact]
        public async Task A_check_that_never_answers_is_cut_off_after_five_seconds_and_the_state_stays_known()
        {
            var rig = new ControlRig();
            rig.SeedCache(Locked, TimeSpan.FromDays(1));
            rig.Transport.Handler = async (r, c) => { await Task.Delay(TimeSpan.FromSeconds(30), c); return new TransportResponse(200, ControlBodies.Rules()); };
            var clock = Stopwatch.StartNew();
            await rig.Run();
            clock.Stop();
            Assert.InRange(clock.Elapsed.TotalSeconds, 4.5, 8);
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            Assert.Equal(1, rig.Transport.Count);
        }

        [Fact]
        public async Task A_transport_that_ignores_cancellation_cannot_keep_the_check_alive()
        {
            var rig = new ControlRig();
            var never = new TaskCompletionSource<TransportResponse>();
            rig.Transport.Handler = (r, c) => never.Task;
            var clock = Stopwatch.StartNew();
            await rig.Run();
            Assert.InRange(clock.Elapsed.TotalSeconds, 4.5, 8);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
        }

        [Fact]
        public async Task The_request_is_a_get_to_the_control_id_with_no_identity_and_the_timeouts_of_the_contract()
        {
            var rig = new ControlRig();
            await rig.Run(ControlBodies.Rules());
            Assert.True(rig.Transport.Requests.TryPeek(out var request));
            Assert.Equal("https://in.mightys.dev/api/v1/control/" + ControlIds.ForKey(ControlRig.Key), request.Url.AbsoluteUri);
            Assert.True(request.IsGet);
            Assert.True(request.IsControl);
            Assert.Null(request.AppKey);
            Assert.Null(request.BatchId);
            Assert.Null(request.Body);
            Assert.Null(request.IfNoneMatch);
            Assert.Equal(3, request.ResponseHeadersSeconds);
            Assert.Equal(5, request.TimeoutSeconds);
        }

        // ---- group 4: cache ----

        [Fact]
        public async Task The_first_launch_without_network_and_without_a_cache_is_normal()
        {
            var rig = new ControlRig();
            await rig.Run(status: 0);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            Assert.Null(ControlCache.Read(rig.Storage, ControlRig.CacheName).Body);
        }

        [Fact]
        public async Task A_cache_older_than_30_days_ignores_the_locking_rules_and_one_younger_applies_them()
        {
            var old = new ControlRig();
            old.SeedCache(Locked, TimeSpan.FromDays(31));
            await old.Run(status: 500);
            Assert.Equal(ControlKind.Normal, old.Client.State.Kind);

            var young = new ControlRig();
            young.SeedCache(Locked, TimeSpan.FromDays(29));
            await young.Run(status: 500);
            Assert.Equal(ControlKind.UpdateRequired, young.Client.State.Kind);

            var blocked = new ControlRig();
            blocked.SeedCache(ControlBodies.Rules(blocked: "[{\"version\":\"1.0.0\",\"platforms\":null,\"allowContinue\":false}]"), TimeSpan.FromDays(31));
            await blocked.Run(status: 500);
            Assert.Equal(ControlKind.Normal, blocked.Client.State.Kind);
        }

        [Fact]
        public async Task An_expired_maintenance_window_from_the_cache_is_ignored_and_none_lasts_past_72_hours()
        {
            var rig = new ControlRig();
            var now = rig.Clock.UtcNow;
            rig.SeedCache(ControlBodies.Rules(maintenance: ControlBodies.Maintenance(now - TimeSpan.FromHours(3), now - TimeSpan.FromHours(1))), TimeSpan.FromHours(4));
            await rig.Run(status: 500);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);

            var inside = new ControlRig();
            inside.SeedCache(ControlBodies.Rules(maintenance: ControlBodies.Maintenance(now - TimeSpan.FromHours(1), now + TimeSpan.FromHours(1))), TimeSpan.FromHours(2));
            await inside.Run(status: 500);
            Assert.Equal(ControlKind.Maintenance, inside.Client.State.Kind);

            var tooLong = new ControlRig();
            tooLong.SeedCache(ControlBodies.Rules(maintenance: ControlBodies.Maintenance(now - TimeSpan.FromHours(80), now + TimeSpan.FromHours(100))), TimeSpan.FromHours(81));
            await tooLong.Run(status: 500);
            Assert.Equal(ControlKind.Normal, tooLong.Client.State.Kind);
        }

        [Fact]
        public async Task A_304_keeps_the_cache_and_refreshes_the_time_of_success()
        {
            var rig = new ControlRig();
            // 40 days old: the lock would be ignored, until the server confirms the rules.
            rig.SeedCache(Locked, TimeSpan.FromDays(40));
            rig.Transport.Answer(304);
            Assert.Equal(0, rig.Client.Attempts);
            await rig.Run();
            Assert.True(rig.Transport.Requests.TryPeek(out var request));
            Assert.Equal("\"5\"", request.IfNoneMatch);
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            await rig.Client.Idle;
            var saved = ControlCache.Read(rig.Storage, ControlRig.CacheName);
            Assert.Equal(Locked, saved.Body);
            Assert.Equal(rig.Clock.UtcNow, saved.OkAt);
        }

        [Fact]
        public async Task A_200_replaces_the_cache_and_a_later_launch_starts_from_it_without_network()
        {
            var first = new ControlRig();
            await first.Run(Locked);
            Assert.Equal(ControlKind.UpdateRequired, first.Client.State.Kind);
            await first.Client.Idle;
            var name = ControlRig.CacheName;
            Assert.Matches("^control-[0-9a-f]{16}$", name);
            Assert.True(first.Storage.Files.ContainsKey(name));

            var second = new ControlRig(storage: first.Storage);
            await second.Run(status: 0);
            Assert.Equal(ControlKind.UpdateRequired, second.Client.State.Kind);
        }

        [Fact]
        public async Task A_damaged_cache_file_is_ignored()
        {
            var rig = new ControlRig();
            rig.Storage.Files[ControlRig.CacheName] = Encoding.UTF8.GetBytes("{not json");
            await rig.Run(status: 500);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            var failing = new ControlRig();
            failing.Storage.Fail = true;
            await failing.Run(status: 500);
            Assert.Equal(ControlKind.Normal, failing.Client.State.Kind);
        }

        // ---- group 5: switched off from the server ----

        [Fact]
        public async Task A_disabled_sdk_version_or_the_global_switch_turns_a_shown_lock_off_at_the_next_check()
        {
            var sdk = PulseInfo.SdkName + "@" + PulseInfo.Version;
            var rig = new ControlRig();
            await rig.Run(Locked);
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            rig.Transport.Answer(200, ControlBodies.Rules(update: ControlBodies.RequiredIos, disabled: "[\"" + sdk + "\"]", version: 6));
            await rig.Client.CheckNowAsync();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);

            rig.Transport.Answer(200, Locked);
            await rig.Client.CheckNowAsync();
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            rig.Transport.Answer(200, ControlBodies.Rules(update: ControlBodies.RequiredIos, off: true, version: 7));
            await rig.Client.CheckNowAsync();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            Assert.Equal(new[] { ControlKind.UpdateRequired, ControlKind.Normal, ControlKind.UpdateRequired, ControlKind.Normal }, rig.Changes.Select(c => c.Kind).ToArray());

            var other = new ControlRig();
            await other.Run(ControlBodies.Rules(update: ControlBodies.RequiredIos, disabled: "[\"pulse-other@0.9.7\"]"));
            Assert.Equal(ControlKind.UpdateRequired, other.Client.State.Kind);
        }

        // ---- group 6: loop guard ----

        [Fact]
        public async Task A_store_version_equal_to_the_installed_one_gives_a_suggestion_that_can_continue()
        {
            var rig = new ControlRig(appVersion: "2.0.0");
            var update = "{\"ios\":{\"min\":\"3.0.0\",\"latest\":\"3.0.0\",\"mode\":\"required\",\"storeVersion\":\"2.0.0\",\"storeUrl\":\"https://apps.apple.com/app/id1\"}}";
            await rig.Run(ControlBodies.Rules(update: update));
            Assert.Equal(ControlKind.UpdateSuggested, rig.Client.State.Kind);
            Assert.True(rig.Client.State.AllowContinue);
            Assert.False(rig.Client.State.IsLocking);
        }

        [Fact]
        public async Task Three_returns_from_the_store_on_the_same_version_turn_a_required_update_into_a_suggestion()
        {
            var rig = new ControlRig();
            var update = "{\"ios\":{\"min\":\"2.0.0\",\"latest\":\"2.0.0\",\"mode\":\"required\",\"storeVersion\":null,\"storeUrl\":\"https://apps.apple.com/app/id1\"}}";
            await rig.Run(ControlBodies.Rules(update: update));
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            for (var i = 1; i <= 3; i++)
            {
                Assert.Equal("https://apps.apple.com/app/id1", rig.Client.NoteStorePress());
                rig.Client.OnForeground();
                Assert.Equal(i < 3 ? ControlKind.UpdateRequired : ControlKind.UpdateSuggested, rig.Client.State.Kind);
            }
            await rig.Client.Idle;
            Assert.Equal(3, ControlCache.Read(rig.Storage, ControlRig.CacheName).Returns);
        }

        [Fact]
        public async Task A_foreground_without_a_press_is_not_a_return()
        {
            var rig = new ControlRig();
            await rig.Run(Locked);
            for (var i = 0; i < 5; i++) { rig.Client.OnForeground(); rig.Clock.Advance(TimeSpan.FromSeconds(70)); }
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
        }

        [Fact]
        public async Task A_required_update_with_no_store_link_at_all_becomes_a_suggestion()
        {
            var rig = new ControlRig(platform: "windows");
            var update = "{\"windows\":{\"min\":\"2.0.0\",\"latest\":\"2.0.0\",\"mode\":\"required\",\"storeVersion\":null,\"storeUrl\":null}}";
            await rig.Run(ControlBodies.Rules(update: update));
            Assert.Equal(ControlKind.UpdateSuggested, rig.Client.State.Kind);
            var android = new ControlRig(platform: "android");
            await android.Run(ControlBodies.Rules(update: update.Replace("windows", "android")));
            Assert.Equal(ControlKind.UpdateRequired, android.Client.State.Kind);
            Assert.Equal("https://play.google.com/store/apps/details?id=dev.example.synthetic", android.Client.State.Url);
        }

        [Fact]
        public async Task A_platform_with_no_store_update_turns_the_lock_into_a_suggestion()
        {
            var rig = new ControlRig(platform: "android");
            rig.Client.PlayUpdateNotAvailable = true;
            var update = "{\"android\":{\"min\":\"2.0.0\",\"latest\":\"2.0.0\",\"mode\":\"required\",\"storeVersion\":null,\"storeUrl\":null}}";
            await rig.Run(ControlBodies.Rules(update: update));
            Assert.Equal(ControlKind.UpdateSuggested, rig.Client.State.Kind);
            Assert.True(rig.Client.State.AllowContinue);
        }

        [Fact]
        public async Task A_debug_build_shows_the_lock_as_a_preview_that_can_be_continued_and_stays_continued()
        {
            var rig = new ControlRig(debug: true);
            await rig.Run(Locked);
            var state = rig.Client.State;
            Assert.Equal(ControlKind.UpdateRequired, state.Kind);
            Assert.True(state.IsTestBuild);
            Assert.True(state.AllowContinue);
            rig.Client.Dismiss();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            rig.Transport.Answer(200, Locked);
            await rig.Client.CheckNowAsync();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
        }

        [Fact]
        public async Task A_release_build_lock_cannot_be_dismissed()
        {
            var rig = new ControlRig();
            await rig.Run(Locked);
            rig.Client.Dismiss();
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            Assert.False(rig.Client.State.AllowContinue);
        }

        // ---- cards, suggestions and their memory ----

        [Fact]
        public async Task A_dismissed_suggestion_stays_away_for_24_hours_and_the_memory_survives_a_restart()
        {
            var rig = new ControlRig();
            await rig.Run(ControlBodies.Rules(update: ControlBodies.SuggestedIos));
            Assert.Equal(ControlKind.UpdateSuggested, rig.Client.State.Kind);
            Assert.True(rig.Client.State.CanDismiss);
            rig.Client.Dismiss();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            await rig.Client.Idle;

            var restarted = new ControlRig(storage: rig.Storage);
            restarted.Clock.Set(rig.Clock.UtcNow.AddHours(2));
            await restarted.Run(status: 0);
            Assert.Equal(ControlKind.Normal, restarted.Client.State.Kind);

            // After 24 hours the clock alone brings it back.
            await ControlRig.Until(() => restarted.Clock.Waiters > 0);
            await restarted.AdvanceTo(TimeSpan.FromHours(23), ControlKind.UpdateSuggested);
        }

        [Fact]
        public async Task An_announcement_is_shown_until_dismissed_and_never_again_on_this_device()
        {
            var announcement = "{\"id\":\"a7\",\"title\":\"Winter event\",\"body\":\"Snow is here\",\"url\":null,\"buttonText\":null,\"until\":\"2027-01-01T00:00:00Z\",\"platforms\":null}";
            var rig = new ControlRig();
            await rig.Run(ControlBodies.Rules(announcement: announcement));
            Assert.Equal(ControlKind.Announcement, rig.Client.State.Kind);
            Assert.Equal("Winter event", rig.Client.State.Title);
            rig.Client.Dismiss();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            await rig.Client.Idle;
            var again = new ControlRig(storage: rig.Storage);
            await again.Run(ControlBodies.Rules(announcement: announcement));
            Assert.Equal(ControlKind.Normal, again.Client.State.Kind);
        }

        [Fact]
        public async Task Whats_new_shows_once_after_the_app_was_updated_to_that_version()
        {
            var whatsNew = "{\"version\":\"1.1.0\",\"body\":\"Dark mode\"}";
            var rig = new ControlRig(appVersion: "1.1.0");
            rig.SeedCache(ControlBodies.Rules(), TimeSpan.FromHours(1), seen: "1.0.0");
            await rig.Run(ControlBodies.Rules(whatsNew: whatsNew, version: 6));
            Assert.Equal(ControlKind.WhatsNew, rig.Client.State.Kind);
            Assert.Equal("Dark mode", rig.Client.State.Body);
            rig.Client.Dismiss();
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);

            var fresh = new ControlRig(appVersion: "1.1.0");
            await fresh.Run(ControlBodies.Rules(whatsNew: whatsNew));
            Assert.Equal(ControlKind.Normal, fresh.Client.State.Kind);
        }

        // ---- group 7: analytics opt-out ----

        [Fact]
        public async Task Control_keeps_checking_when_analytics_is_opted_out_and_sends_no_identity()
        {
            var clock = new FakeClock();
            var transport = new ControlTransport();
            transport.Answer(200, Locked);
            var storage = new MemoryStorage();
            PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut),
                null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.OptOut = true;
            runtime.Initialize("synthetic-key");
            Assert.True(client.IsOptOut);
            await client.Control.Startup;
            Assert.Equal(1, transport.Count);
            Assert.True(transport.Requests.TryPeek(out var request));
            Assert.Null(request.AppKey);
            Assert.Null(request.BatchId);
            Assert.Null(request.ContentType);
            Assert.Null(request.IfNoneMatch);
            Assert.Equal(ControlKind.Normal, client.Control.State.Kind);
            Assert.Equal(ControlKind.Normal, runtime.Control.State.Kind);
            // The platform here is Windows (the fake environment), so the iOS rule does not apply.
            Assert.DoesNotContain(storage.Files.Keys, k => k.StartsWith("queue", StringComparison.Ordinal));
            client.Control.Stop();
        }

        [Fact]
        public async Task The_opt_out_does_not_touch_the_control_cache()
        {
            var clock = new FakeClock();
            var transport = new ControlTransport();
            transport.Answer(200, ControlBodies.Rules(update: "{\"windows\":{\"min\":\"9.0.0\",\"latest\":\"9.0.0\",\"mode\":\"required\",\"storeVersion\":null,\"storeUrl\":\"https://example.com/get\"}}"));
            var storage = new MemoryStorage();
            PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut),
                null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            await client.Control.Startup;
            Assert.Equal(ControlKind.UpdateRequired, runtime.Control.State.Kind);
            runtime.OptOut = true;
            await Task.Delay(100);
            Assert.Equal(ControlKind.UpdateRequired, runtime.Control.State.Kind);
            await client.Control.Idle;
            Assert.True(storage.Files.ContainsKey(ControlIds.CacheName("synthetic-key")));
            client.Control.Stop();
        }

        // ---- group 8: off ----

        [Fact]
        public async Task Off_makes_no_request_and_reads_nothing()
        {
            var rig = new ControlRig(mode: ControlMode.Off);
            rig.SeedCache(Locked, TimeSpan.FromHours(1));
            rig.Client.Start();
            await rig.Client.CheckNowAsync();
            rig.Client.OnForeground();
            await Task.Delay(100);
            Assert.Equal(0, rig.Transport.Count);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            Assert.Equal(0, rig.Client.Attempts);
        }

        [Fact]
        public async Task A_transport_that_is_not_marked_for_control_gets_no_control_request()
        {
            var plain = new FakeTransport();
            var rig = new ControlRig(transport: plain);
            rig.Client.Start();
            await rig.Client.CheckNowAsync();
            await Task.Delay(50);
            Assert.Empty(plain.Requests);
            Assert.Empty(plain.ConfigRequests);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
        }

        [Fact]
        public async Task The_option_off_in_the_runtime_sends_nothing_and_manual_checks_like_automatic()
        {
            foreach (var mode in new[] { ControlMode.Off, ControlMode.Manual, ControlMode.Automatic })
            {
                var clock = new FakeClock();
                var transport = new ControlTransport();
                transport.Answer(404);
                var storage = new MemoryStorage();
                PulseClient client = null;
                var runtime = new PulseRuntime(clock);
                runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut),
                    null, () => PulseClient.ReadStoredOptOut(storage));
                runtime.Initialize("synthetic-key", new PulseOptions { Control = mode });
                await client.Control.Startup;
                Assert.Equal(mode == ControlMode.Off ? 0 : 1, transport.Count);
                client.Control.Stop();
            }
        }

        // ---- group 9: scheduled maintenance by the clock ----

        [Fact]
        public async Task A_scheduled_window_changes_the_state_at_each_boundary_by_the_clock_alone()
        {
            var rig = new ControlRig();
            var now = rig.Clock.UtcNow;
            rig.SeedCache(ControlBodies.Rules(maintenance: ControlBodies.Maintenance(now.AddHours(3), now.AddHours(4), 60)), TimeSpan.Zero);
            await rig.Run(status: 0);
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            await ControlRig.Until(() => rig.Clock.Waiters > 0);
            var clock = Stopwatch.StartNew();
            await rig.AdvanceTo(TimeSpan.FromHours(2), ControlKind.MaintenanceUpcoming);
            await rig.AdvanceTo(TimeSpan.FromHours(1), ControlKind.Maintenance);
            Assert.False(rig.Client.State.AllowContinue);
            Assert.Equal(now.AddHours(4), rig.Client.State.EndsAt);
            await rig.AdvanceTo(TimeSpan.FromHours(1), ControlKind.Normal);
            Assert.True(clock.Elapsed.TotalSeconds < 5, "each change must follow the clock within 5 seconds");
            Assert.Equal(new[] { ControlKind.MaintenanceUpcoming, ControlKind.Maintenance, ControlKind.Normal }, rig.Changes.Select(c => c.Kind).ToArray());
            Assert.Equal(1, rig.Transport.Count);
        }

        [Fact]
        public async Task A_window_that_arrives_from_the_server_is_scheduled_too()
        {
            var rig = new ControlRig();
            var now = rig.Clock.UtcNow;
            await rig.Run(ControlBodies.Rules(maintenance: ControlBodies.Maintenance(now.AddHours(2), now.AddHours(3), 30)));
            Assert.Equal(ControlKind.Normal, rig.Client.State.Kind);
            await ControlRig.Until(() => rig.Clock.Waiters > 0);
            await rig.AdvanceTo(TimeSpan.FromMinutes(95), ControlKind.MaintenanceUpcoming);
            await rig.AdvanceTo(TimeSpan.FromMinutes(25), ControlKind.Maintenance);
            await rig.AdvanceTo(TimeSpan.FromHours(1), ControlKind.Normal);
        }

        // ---- checks: gap, joins, validators ----

        [Fact]
        public async Task Automatic_checks_are_at_least_60_seconds_apart_and_check_now_ignores_the_gap()
        {
            var rig = new ControlRig();
            await rig.Run(ControlBodies.Rules());
            rig.Clock.Advance(TimeSpan.FromSeconds(59));
            rig.Client.OnForeground();
            await Task.Delay(100);
            Assert.Equal(1, rig.Transport.Count);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            rig.Client.OnForeground();
            await ControlRig.Until(() => rig.Transport.Count == 2);
            // The first call may join the check of the foreground that is still running; the second one is a check of its own.
            await rig.Client.CheckNowAsync();
            await rig.Client.CheckNowAsync();
            Assert.InRange(rig.Transport.Count, 3, 4);
            Assert.True(rig.Transport.Requests.Last().IfNoneMatch == "\"5\"");
        }

        [Fact]
        public async Task A_check_now_during_a_running_check_joins_it()
        {
            var rig = new ControlRig();
            var gate = new TaskCompletionSource<TransportResponse>();
            rig.Transport.Handler = (r, c) => gate.Task;
            rig.Client.Start();
            var first = rig.Client.CheckNowAsync();
            var second = rig.Client.CheckNowAsync();
            await ControlRig.Until(() => rig.Transport.Count >= 1);
            await Task.Delay(50);
            Assert.Equal(1, rig.Transport.Count);
            gate.SetResult(new TransportResponse(200, Locked));
            await Task.WhenAll(first, second);
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
            Assert.Equal(1, rig.Transport.Count);
        }

        [Fact]
        public async Task The_host_option_decides_where_the_check_goes()
        {
            var clock = new FakeClock();
            var transport = new ControlTransport();
            PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, new MemoryStorage(), new FakeEnvironment(), null, false, () => 0.5, optOut), null);
            runtime.Initialize("synthetic-key", new PulseOptions { Host = "https://example.invalid/" });
            await client.Control.Startup;
            Assert.True(transport.Requests.TryPeek(out var request));
            Assert.Equal("https://example.invalid/api/v1/control/" + ControlIds.ForKey("synthetic-key"), request.Url.AbsoluteUri);
            client.Control.Stop();
        }

        [Fact]
        public async Task A_handler_that_throws_never_disturbs_the_client()
        {
            var rig = new ControlRig();
            rig.Client.Changed += s => throw new InvalidOperationException("handler bug");
            await rig.Run(Locked);
            Assert.Equal(ControlKind.UpdateRequired, rig.Client.State.Kind);
        }

        [Fact]
        public void The_public_facade_is_safe_before_anything_starts()
        {
            var control = new PulseControl();
            Assert.Equal(ControlKind.Normal, control.State.Kind);
            Assert.False(control.OpenStore());
            control.Dismiss();
            control.OnForeground();
            Assert.True(control.CheckNowAsync().IsCompleted);
        }

        [Fact]
        public async Task Open_store_hands_the_address_to_the_platform_opener_and_counts_the_press()
        {
            var rig = new ControlRig();
            await rig.Run(Locked);
            var control = new PulseControl();
            control.Attach(rig.Client);
            string opened = null;
            control.Opener = url => { opened = url; return true; };
            Assert.True(control.OpenStore());
            Assert.Equal("https://apps.apple.com/app/id1", opened);
            await rig.Client.Idle;
            Assert.True(ControlCache.Read(rig.Storage, ControlRig.CacheName).Pending);
            control.Opener = null;
            Assert.False(control.OpenStore());
        }

        // ---- initialize never waits ----

        [Fact]
        public async Task Initialize_returns_in_under_5_ms_while_the_control_request_hangs_for_30_seconds()
        {
            var total = TimeSpan.Zero;
            const int runs = 20;
            // One untimed run so the one-time cost of loading code is not counted.
            await InitializeOnce();
            for (var i = 0; i < runs; i++) total += await InitializeOnce();
            var average = total.TotalMilliseconds / runs;
            Console.WriteLine("control init average ms over " + runs + ": " + average.ToString("F3"));
            Assert.True(average < 5, "average " + average + " ms");
        }

        private static async Task<TimeSpan> InitializeOnce()
        {
            var clock = new FakeClock();
            var transport = new ControlTransport();
            transport.Handler = async (r, c) => { await Task.Delay(TimeSpan.FromSeconds(30), c); return new TransportResponse(404); };
            var storage = new MemoryStorage();
            PulseClient client = null;
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => client = new PulseClient(k, o, clock, transport, storage, new FakeEnvironment(), null, false, () => 0.5, optOut), null);
            var timer = Stopwatch.StartNew();
            runtime.Initialize("synthetic-key");
            timer.Stop();
            await ControlRig.Until(() => transport.Count == 1);
            client.Control.Stop();
            return timer.Elapsed;
        }
    }
}
