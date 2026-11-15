using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class InstallIdTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task InstallIdIsCreatedOnceAndReusedAfterReinitializationWithSameStorage()
        {
            var storage = new MemoryStorage();
            var first = await Run(storage, h => h.Runtime.Track("one"));
            var id = StoredId(storage);
            Assert.Matches("^[0-9a-f]{32}$", id);
            Assert.Equal(32, storage.Files["install-id"].Length);
            var second = await Run(storage, h => h.Runtime.Track("two"));
            Assert.Equal(id, StoredId(storage));
            Assert.Equal(first.Single().GetProperty("userHash").GetString(), second.Single().GetProperty("userHash").GetString());
        }

        [Fact]
        public async Task UserHashIsSentWithoutSetUserAndEqualsHashOfInstallId()
        {
            var storage = new MemoryStorage();
            var events = await Run(storage, h => { h.Runtime.Track("a"); h.Runtime.Track("b"); });
            Assert.Equal(2, events.Length);
            Assert.All(events, e => Assert.Equal(SignalJson.Hash(StoredId(storage)), e.GetProperty("userHash").GetString()));
        }

        [Fact]
        public async Task SetUserOverridesInstallIdAndNullRestoresIt()
        {
            var storage = new MemoryStorage();
            var events = await Run(storage, h =>
            {
                h.Runtime.Track("anonymous");
                h.Runtime.SetUser("person-1"); h.Runtime.Track("known");
                h.Runtime.SetUser(null); h.Runtime.Track("restored");
            });
            var installHash = SignalJson.Hash(StoredId(storage));
            Assert.Equal(installHash, events[0].GetProperty("userHash").GetString());
            Assert.Equal(SignalJson.Hash("person-1"), events[1].GetProperty("userHash").GetString());
            Assert.Equal(installHash, events[2].GetProperty("userHash").GetString());
        }

        [Fact]
        public async Task OptOutDeletesInstallIdAndOptOutOffCreatesDifferentOne()
        {
            var storage = new MemoryStorage(); var transport = new FakeTransport();
            var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions(), Platform());
            try
            {
                host.Runtime.Track("before"); await host.InstallId.Idle.WaitAsync(Limit);
                var old = StoredId(storage);
                host.Runtime.OptOut = true; await host.InstallId.Idle.WaitAsync(Limit);
                Assert.False(storage.Files.ContainsKey("install-id"));
                host.Runtime.OptOut = false; host.Runtime.Track("after");
                await host.InstallId.Idle.WaitAsync(Limit); await host.Runtime.Flush().WaitAsync(Limit);
                var fresh = StoredId(storage);
                Assert.Matches("^[0-9a-f]{32}$", fresh); Assert.NotEqual(old, fresh);
                var after = Harness.Events(transport.Requests).Single(e => e.GetProperty("type").GetString() == "after");
                Assert.Equal(SignalJson.Hash(fresh), after.GetProperty("userHash").GetString());
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task UnreadableStorageStillGivesAUserHashAndIsNotRetried()
        {
            var storage = new PickyStorage { FailRead = true, FailWrite = true };
            var events = await Run(storage, h => { h.Runtime.Track("a"); h.Runtime.Track("b"); });
            var hash = events[0].GetProperty("userHash").GetString();
            Assert.Equal(64, hash.Length); Assert.Equal(hash, events[1].GetProperty("userHash").GetString());
            Assert.True(storage.InstallIdWrites <= 1);
        }

        [Fact]
        public async Task UnwritableStorageKeepsOneIdForTheProcess()
        {
            var storage = new PickyStorage { FailWrite = true };
            var events = await Run(storage, h => { h.Runtime.Track("a"); h.Runtime.Track("b"); });
            Assert.Equal(events[0].GetProperty("userHash").GetString(), events[1].GetProperty("userHash").GetString());
            Assert.Equal(1, storage.InstallIdWrites);
        }

        [Fact]
        public async Task DamagedStoredIdIsReplaced()
        {
            var storage = new MemoryStorage(); storage.Files["install-id"] = Encoding.ASCII.GetBytes("not-an-id\n");
            await Run(storage, h => h.Runtime.Track("a"));
            Assert.Matches("^[0-9a-f]{32}$", StoredId(storage));
        }

        [Fact]
        public async Task SignalTrackedBeforeInitializeCarriesInstallIdHash()
        {
            var storage = new MemoryStorage(); var transport = new FakeTransport();
            var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Runtime.Track("early");
            host.Initialize("synthetic", new PulseOptions(), Platform());
            try
            {
                await host.Runtime.Flush().WaitAsync(Limit); await host.InstallId.Idle.WaitAsync(Limit);
                var early = Harness.Event(Assert.Single(transport.Requests));
                Assert.Equal(SignalJson.Hash(StoredId(storage)), early.GetProperty("userHash").GetString());
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public async Task InstallIdFileInRealDirectoryHasNoNewlineAndSurvivesRestart()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, ".tmp", Guid.NewGuid().ToString("N"));
            try
            {
                string id;
                var one = new PulseHost(transport: new FakeTransport(), usage: false);
                one.Initialize("synthetic", new PulseOptions(), new PulsePlatformOptions { StorageDirectory = directory, EnvironmentInfo = Env() });
                try { one.Runtime.Track("a"); await one.InstallId.Idle.WaitAsync(Limit); }
                finally { await one.ShutdownAsync(); }
                var bytes = File.ReadAllBytes(Path.Combine(directory, "install-id"));
                id = Encoding.ASCII.GetString(bytes);
                Assert.Matches("^[0-9a-f]{32}$", id);
                var two = new PulseHost(transport: new FakeTransport(), usage: false);
                two.Initialize("synthetic", new PulseOptions(), new PulsePlatformOptions { StorageDirectory = directory, EnvironmentInfo = Env() });
                try { two.Runtime.Track("b"); await two.InstallId.Idle.WaitAsync(Limit); }
                finally { await two.ShutdownAsync(); }
                Assert.Equal(id, Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(directory, "install-id"))));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Fact]
        public async Task FirstSignalAfterInitializeCarriesFullSystemValues()
        {
            var transport = new FakeTransport();
            var host = new PulseHost(storage: new MemoryStorage(), transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions());
            try
            {
                host.Runtime.Track("first"); await host.Runtime.Flush().WaitAsync(Limit);
                var system = Harness.Event(Assert.Single(transport.Requests)).GetProperty("system");
                foreach (var field in new[] { "platform", "osName", "osVersion", "appVersion", "deviceModel", "locale" })
                    Assert.False(string.IsNullOrEmpty(system.GetProperty(field).GetString()), field + " is empty");
            }
            finally { await host.ShutdownAsync(); }
        }

        [Fact]
        public void DeviceModelIsSuppliedByTheHostOrDefaultsPerPlatform()
        {
            Assert.Equal("Pixel 9", new DotNetEnvironmentInfo("Android", "Android OS 15", "1.0", "1", "en-US", "Pixel 9").DeviceModel);
            Assert.Equal("PC", new DotNetEnvironmentInfo("Windows", "11", "1.0", "1", "en-US").DeviceModel);
            Assert.Equal("Desktop", new DotNetEnvironmentInfo("Linux", "6.8", "1.0", "1", "en-US").DeviceModel);
            Assert.Equal("", new DotNetEnvironmentInfo("Android", "Android OS 15", "1.0", "1", "en-US").DeviceModel);
            Assert.Equal("", new DotNetEnvironmentInfo("iOS", "18.0", "1.0", "1", "en-US").DeviceModel);
            var mac = new DotNetEnvironmentInfo("macOS", "15.0", "1.0", "1", "en-US").DeviceModel;
            Assert.False(string.IsNullOrEmpty(mac));
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                Assert.Matches("^[A-Za-z]+[0-9]+,[0-9]+$", mac);
            Assert.NotEmpty(new DotNetEnvironmentInfo().DeviceModel);
        }

        private static DotNetEnvironmentInfo Env() => new DotNetEnvironmentInfo("Windows", "11", "1.2.3", "42", "en-US");
        private static PulsePlatformOptions Platform() => new PulsePlatformOptions { EnvironmentInfo = Env() };
        private static string StoredId(MemoryStorage storage) => Encoding.ASCII.GetString(storage.Files["install-id"]);

        private static async Task<System.Text.Json.JsonElement[]> Run(IStorage storage, Action<PulseHost> act)
        {
            var transport = new FakeTransport(); var host = new PulseHost(storage: storage, transport: transport, usage: false);
            host.Initialize("synthetic", new PulseOptions(), Platform());
            try
            {
                act(host); await host.Runtime.Flush().WaitAsync(Limit); await host.InstallId.Idle.WaitAsync(Limit);
                return Harness.Events(transport.Requests);
            }
            finally { await host.ShutdownAsync(); }
        }

        private sealed class PickyStorage : IStorage
        {
            private readonly MemoryStorage inner = new MemoryStorage();
            internal bool FailRead, FailWrite;
            internal int InstallIdWrites;
            private static bool Id(string name) => name == "install-id";
            public byte[] Read(string name) { if (Id(name) && FailRead) throw new IOException("unreadable"); return inner.Read(name); }
            public void Append(string name, byte[] bytes) => inner.Append(name, bytes);
            public void Replace(string name, byte[] bytes)
            {
                if (Id(name)) { InstallIdWrites++; if (FailWrite) throw new IOException("unwritable"); }
                inner.Replace(name, bytes);
            }
            public void Delete(string name) => inner.Delete(name);
        }
    }
}
