using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ErrorHooksTests
    {
        private static PulsePlatformOptions Platform() => new PulsePlatformOptions { EnvironmentInfo = new FakeEnvironment() };
        private static Exception Thrown() { try { throw new InvalidOperationException("hook"); } catch (Exception e) { return e; } }

        private static PulseHost Start(PulseOptions options, MemoryStorage storage = null)
        {
            var host = new PulseHost(storage: storage ?? new MemoryStorage(), transport: new FakeTransport(), usage: false);
            host.Initialize("synthetic", options, Platform());
            return host;
        }

        [Fact]
        public async Task HooksAreInstalledByDefaultAndRemovedAtShutdown()
        {
            var host = Start(new PulseOptions());
            Assert.True(host.Hooks.Installed);
            await host.ShutdownAsync();
            Assert.False(host.Hooks.Installed);
        }

        [Fact]
        public async Task CaptureErrorsFalseInstallsNothing()
        {
            var host = Start(new PulseOptions { CaptureErrors = false });
            try { Assert.False(host.Hooks.Installed); } finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task TerminatingUnhandledExceptionIsSavedAsACrashBeforeItReturns()
        {
            var storage = new MemoryStorage(); var host = Start(new PulseOptions(), storage);
            try
            {
                // Let the first launch finish loading its files, so the crash below is the one a later launch finds.
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                host.Hooks.OnUnhandled(this, new UnhandledExceptionEventArgs(Thrown(), true));
                Assert.Contains(storage.Files.Keys, k => k.EndsWith(".crash", StringComparison.Ordinal));
            }
            finally { await host.ShutdownAsync(); }
            var transport = new FakeTransport();
            var next = new PulseHost(storage: storage, transport: transport, usage: false);
            next.Initialize("synthetic", new PulseOptions(), Platform());
            try
            {
                await next.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                var error = Harness.Events(transport.Requests).Single(e => e.GetProperty("type").GetString() == "pulse.error").GetProperty("error");
                Assert.Equal("crash", error.GetProperty("kind").GetString());
                Assert.Equal("InvalidOperationException", error.GetProperty("type").GetString());
            }
            finally { await next.ShutdownAsync(); }
        }

        [Fact]
        public async Task UnobservedTaskExceptionIsKindErrorAndIsNotMarkedObserved()
        {
            var transport = new FakeTransport();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions(), Platform());
            try
            {
                var args = new UnobservedTaskExceptionEventArgs(new AggregateException(Thrown()));
                host.Hooks.OnUnobserved(this, args);
                Assert.False(args.Observed);
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("error", Harness.Events(transport.Requests).Single(e => e.GetProperty("type").GetString() == "pulse.error")
                    .GetProperty("error").GetProperty("kind").GetString());
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task HandlersNeverThrowWhenStorageFailsOrArgumentsAreOdd()
        {
            var storage = new MemoryStorage(); var host = Start(new PulseOptions(), storage);
            try
            {
                storage.Fail = true;
                host.Hooks.OnUnhandled(this, new UnhandledExceptionEventArgs(Thrown(), true));
                host.Hooks.OnUnhandled(this, new UnhandledExceptionEventArgs("not an exception", true));
                host.Hooks.OnUnhandled(this, null); host.Hooks.OnUnobserved(this, null);
            }
            finally { storage.Fail = false; await host.ShutdownAsync(); }
        }
    }
}
