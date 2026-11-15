using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    [CollectionDefinition("BuildMode", DisableParallelization = true)]
    public sealed class BuildModeCollection { }

    // The default is a process-wide value set at Initialize, so these tests run alone.
    [Collection("BuildMode")]
    public sealed class BuildModeTests
    {
        private static Assembly Dynamic(DebuggableAttribute attribute)
        {
            var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BuildModeFake" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            if (attribute != null)
            {
                var ctor = typeof(DebuggableAttribute).GetConstructor(new[] { typeof(DebuggableAttribute.DebuggingModes) });
                builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, new object[] { attribute.DebuggingFlags }));
            }
            return builder;
        }

        [Fact]
        public void DebugBuildOfEntryAssemblyIsDetectedFromDisabledOptimizer()
        {
            var debug = Dynamic(new DebuggableAttribute(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations));
            Assert.True(BuildMode.EntryIsDebugBuild(() => debug));
        }

        [Fact]
        public void ReleaseAttributeWithOptimizationsEnabledIsReal()
        {
            var release = Dynamic(new DebuggableAttribute(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints));
            Assert.False(BuildMode.EntryIsDebugBuild(() => release));
        }

        [Fact]
        public void MissingAttributeMissingAssemblyAndFailuresAreReal()
        {
            var none = Dynamic(null);
            Assert.False(BuildMode.EntryIsDebugBuild(() => none));
            Assert.False(BuildMode.EntryIsDebugBuild(() => null));
            Assert.False(BuildMode.EntryIsDebugBuild(() => throw new InvalidOperationException()));
            Assert.False(BuildMode.EntryIsDebugBuild(() => none, _ => throw new InvalidOperationException()));
        }

        [Fact]
        public void FakeAttributeSeamIsUsed()
        {
            var asm = Dynamic(null);
            Assert.True(BuildMode.EntryIsDebugBuild(() => asm, _ => new DebuggableAttribute(DebuggableAttribute.DebuggingModes.DisableOptimizations)));
        }

        [Fact]
        public void AttachedDebuggerAndEnvironmentDoNotChangeTheDecision()
        {
            var release = Dynamic(new DebuggableAttribute(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints));
            var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
                Assert.False(BuildMode.EntryIsDebugBuild(() => release));
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous);
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            }
        }

        private static async Task<bool> SentDebugFlag(Func<bool> detect, PulseOptions options)
        {
            var transport = new FakeTransport();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, debugDefault: detect, usage: false);
            host.Initialize("synthetic", options, new PulsePlatformOptions
            { EnvironmentInfo = new DotNetEnvironmentInfo("Windows", "11", "1.2.3", "42", "en-US") });
            try
            {
                host.Runtime.Track("probe");
                await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                return Harness.Event(System.Linq.Enumerable.First(transport.Requests)).GetProperty("debug").GetBoolean();
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task DetectedDebugBuildMarksSignalsAsTestWhenOptionIsNotSet()
        {
            Assert.True(await SentDebugFlag(() => true, new PulseOptions()));
            Assert.True(await SentDebugFlag(() => true, null));
        }

        [Fact]
        public async Task DetectedReleaseBuildSendsRealSignalsWhenOptionIsNotSet() =>
            Assert.False(await SentDebugFlag(() => false, new PulseOptions()));

        [Fact]
        public async Task ExplicitOptionAlwaysWinsInBothDirections()
        {
            Assert.False(await SentDebugFlag(() => true, new PulseOptions { Debug = false }));
            Assert.True(await SentDebugFlag(() => false, new PulseOptions { Debug = true }));
        }

        [Fact]
        public async Task DecisionIsTakenOnceAtInitialize()
        {
            var calls = 0;
            var transport = new FakeTransport();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, debugDefault: () => { calls++; return calls == 1; }, usage: false);
            var platform = new PulsePlatformOptions { EnvironmentInfo = new DotNetEnvironmentInfo("Windows", "11", "1", "1", "en-US") };
            host.Initialize("synthetic", new PulseOptions(), platform);
            host.Initialize("synthetic", new PulseOptions(), platform);
            try
            {
                host.Runtime.Track("one"); await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                host.Runtime.Track("two"); await host.Runtime.Flush().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, calls);
                foreach (var request in transport.Requests)
                    foreach (var e in Harness.Events(new[] { request }))
                        Assert.True(e.GetProperty("debug").GetBoolean());
            }
            finally { await host.ShutdownAsync(); }
        }
    }
}
