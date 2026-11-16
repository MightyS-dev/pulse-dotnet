using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    // Flush with nothing to deliver completes at once, with the background worker running as in an app.
    public sealed class FlushEmptyQueueTests
    {
        private static async Task AssertCompletesQuickly(Task flush)
        {
            var watch = Stopwatch.StartNew();
            var finished = await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(flush, finished);
            Assert.True(watch.ElapsedMilliseconds < 100, "Flush took " + watch.ElapsedMilliseconds + " ms");
        }

        private static (PulseRuntime Runtime, FakeTransport Transport) Start(MemoryStorage storage = null)
        {
            var clock = new FakeClock(); var transport = new FakeTransport(); storage = storage ?? new MemoryStorage();
            var runtime = new PulseRuntime(clock);
            runtime.InstallPlatform((k, o, optOut) => new PulseClient(k, o, clock, transport, storage, new ExtrasEnvironment(), null, true,
                () => 0.5, optOut, null), null, () => PulseClient.ReadStoredOptOut(storage));
            runtime.Initialize("synthetic-key");
            return (runtime, transport);
        }

        [Fact]
        public async Task FlushOnAnEmptyQueueCompletesAtOnce()
        {
            var (runtime, _) = Start();
            await AssertCompletesQuickly(runtime.Flush());
        }

        [Fact]
        public async Task FlushAfterEverythingWasDeliveredCompletesAtOnce()
        {
            var (runtime, transport) = Start();
            runtime.Track("a");
            await Task.WhenAny(runtime.Flush(), Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.NotEmpty(transport.Requests);
            await AssertCompletesQuickly(runtime.Flush());
        }

        [Fact]
        public async Task FlushBeforeInitializeCompletesAtOnce()
        {
            var runtime = new PulseRuntime(new FakeClock());
            await AssertCompletesQuickly(runtime.Flush());
        }

        [Fact]
        public async Task FlushOnAnEmptyQueueCompletesAtOnceWhenStorageFails()
        {
            var (runtime, _) = Start(new MemoryStorage { Fail = true });
            await AssertCompletesQuickly(runtime.Flush());
        }
    }
}
