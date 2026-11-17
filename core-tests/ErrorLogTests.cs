using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Mightys.Pulse.Tests
{
    public sealed class ErrorLogTests
    {
        private static readonly string[] Skip = { "Engine.Debug", "Engine.Logger" };

        private const string MonoTrace =
            "Engine.DebugLogHandler:Internal_LogException(Exception, Object)\n" +
            "Engine.DebugLogHandler:LogException(Exception, Object)\n" +
            "Engine.Logger:LogException(Exception, Object)\n" +
            "Engine.Debug:LogException(Exception)\n" +
            "Game.Player.Move (Engine.Vector3 dir) (at Assets/Scripts/Player.cs:42)\n" +
            "Game.Player.Update () (at Assets/Scripts/Player.cs:17)\n";

        [Fact]
        public void MonoTraceDropsLoggerFramesAndKeepsFileAndLine()
        {
            var e = ErrorReport.FromLog("NullReferenceException: Object reference not set to an instance of an object", MonoTrace, "error", Skip);
            Assert.Equal("NullReferenceException", e.Type);
            Assert.Equal("Object reference not set to an instance of an object", e.Message);
            Assert.Equal(new[] { "Game.Player.Move() Player.cs:42", "Game.Player.Update() Player.cs:17" }, e.Frames);
            Assert.True(e.Symbolicated);
            Assert.Equal("error", e.Kind);
        }

        [Fact]
        public void ColonStyleTraceWithAndWithoutFile()
        {
            var trace = "Engine.Debug:LogError(Object)\r\n" +
                        "Game.Player:Move(Vector3) (at Assets/Scripts/Player.cs:42)\r\n" +
                        "Game.Player:Update()\r\n" +
                        "Engine.Debug:Log(Object)\r\n";
            var e = ErrorReport.FromLog("InvalidOperationException: bad", trace, "error", Skip);
            // Only the leading logger frames go; a later one in the middle of the stack stays.
            Assert.Equal(new[] { "Game.Player.Move() Player.cs:42", "Game.Player.Update()", "Engine.Debug.Log()" }, e.Frames);
            Assert.True(e.Symbolicated);
        }

        [Fact]
        public void Il2CppTraceWithoutFileInfo()
        {
            var trace = "Game.Player.Move (Engine.Vector3 dir) (at <00000000000000000000000000000000>:0)\n" +
                        "Game.Player.Update () [0x00012] in <8f2c1d0e4b5a49d3a1b2c3d4e5f60718>:0 \n" +
                        "Game.Player.Fire () [0x0001a] in /app/Assets/Scripts/Player.cs:88 \n";
            var e = ErrorReport.FromLog("ArgumentException: x", trace, "error", Skip);
            Assert.Equal(new[] { "Game.Player.Move()", "Game.Player.Update()", "Game.Player.Fire() Player.cs:88" }, e.Frames);
            Assert.True(e.Symbolicated);
        }

        [Fact]
        public void NestedAndCompilerGeneratedNamesShowTheDeveloperMethod()
        {
            var trace = "Game.Spawner+<Run>d__3:MoveNext() (at Assets/Spawner.cs:9)\n" +
                        "Game.Spawner+<>c:<Start>b__0() (at Assets/Spawner.cs:5)\n" +
                        "Game.Util`1[T]:Do(T) (at Assets/Util.cs:3)\n" +
                        "Game.Spawner.Make<T> (T x) (at Assets/Spawner.cs:2)\n" +
                        "Game.Holder+Inner:.ctor() (at Assets/Holder.cs:1)\n";
            var e = ErrorReport.FromLog("Exception: x", trace, "error", Skip);
            Assert.Equal(new[]
            {
                "Game.Spawner.Run() Spawner.cs:9", "Game.Spawner.Start() Spawner.cs:5", "Game.Util.Do() Util.cs:3",
                "Game.Spawner.Make() Spawner.cs:2", "Game.Holder.Inner..ctor() Holder.cs:1",
            }, e.Frames);
        }

        [Fact]
        public void NoMethodNameMeansNotSymbolicated()
        {
            var e = ErrorReport.FromLog("Error: x", "0x00007ff61234abcd\n(wrapper native)\n#00 pc 0000000000123456 libil2cpp.so\n", "crash");
            Assert.Empty(e.Frames); Assert.False(e.Symbolicated);
            var none = ErrorReport.FromLog("Error: x", "", "error");
            Assert.Empty(none.Frames); Assert.False(none.Symbolicated);
            var nil = ErrorReport.FromLog("Error: x", null, "error");
            Assert.Empty(nil.Frames); Assert.False(nil.Symbolicated);
        }

        [Theory]
        [InlineData("NullReferenceException: boom", "NullReferenceException", "boom")]
        [InlineData("System.IO.IOException: disk: full", "IOException", "disk: full")]
        [InlineData("MyGame.Errors.FatalError: x", "FatalError", "x")]
        [InlineData("Failed to load level", "LogError", "Failed to load level")]
        [InlineData("Assertion failed: x is null", "LogError", "Assertion failed: x is null")]
        [InlineData("Loading 5: done", "LogError", "Loading 5: done")]
        [InlineData(": odd", "LogError", ": odd")]
        [InlineData("StackOverflowException", "StackOverflowException", "")]
        public void ConditionParsing(string condition, string type, string message)
        {
            var e = ErrorReport.FromLog(condition, null, "error");
            Assert.Equal(type, e.Type); Assert.Equal(message, e.Message);
        }

        [Fact]
        public void MessageIsFilteredAndFramesUseTheFrameLineExemption()
        {
            var e = ErrorReport.FromLog("IOException: open /home/jane/a.json for jane@example.com order 1234567 at MyApp+0x1234567",
                "Game.Io.Read () (at /app/Assets/Io.cs:1234567)\nGame.Io.Open() (at Assets/Io.cs:42)", "error");
            Assert.Equal("open a.json for <filtered> order <filtered> at MyApp+0x1234567", e.Message);
            Assert.Equal("Game.Io.Read() Io.cs:1234567", e.Frames[0]);
            Assert.Equal("Game.Io.Open() Io.cs:42", e.Frames[1]);
        }

        [Fact]
        public void LimitsThirtyFramesThreeHundredCharsFiveHundredMessage()
        {
            var lines = Enumerable.Range(0, 60).Select(i => $"Game.Deep.Call{i} () (at Assets/Deep.cs:{i + 1})");
            var e = ErrorReport.FromLog("Exception: " + new string('m', 900), string.Join("\n", lines), "error");
            Assert.Equal(30, e.Frames.Count); Assert.Equal(500, e.Message.Length);
            Assert.Equal("Game.Deep.Call0() Deep.cs:1", e.Frames[0]);
            var longFrame = ErrorReport.FromLog("Exception: x", "Game." + new string('A', 900) + ":Run() (at Assets/A.cs:1)", "error");
            Assert.Equal(300, longFrame.Frames[0].Length);
            var type = ErrorReport.FromLog(new string('T', 900) + ": x", null, "error");
            Assert.True(type.Type.Length <= 200);
        }

        [Fact]
        public void NeverThrowsOnOddInput()
        {
            var odd = new[] { "\0", new string('(', 5000), "a:b:c (at ", "(at x:y)", "[0x1] in", "\uD83D", new string('x', 200000) };
            foreach (var c in odd) foreach (var t in odd)
                { var e = ErrorReport.FromLog(c, t, "error"); Assert.NotNull(e); }
        }

        private static JsonErrorOf Single(Harness h) => new JsonErrorOf(h);
        private sealed class JsonErrorOf
        {
            internal readonly System.Text.Json.JsonElement[] Errors;
            internal JsonErrorOf(Harness h) =>
                Errors = Harness.Events(h.Transport.Requests).Where(e => e.GetProperty("type").GetString() == "pulse.error").Select(e => e.GetProperty("error")).ToArray();
        }

        [Fact]
        public async Task CaptureLogSendsAnErrorSignalAndSharesTheRateLimitWithCaptureUnhandled()
        {
            var h = new Harness();
            h.Runtime.CaptureLog("InvalidOperationException: from log", MonoTrace, "error", Skip);
            for (var i = 0; i < 6; i++) h.Runtime.CaptureUnhandled(new InvalidOperationException("e"), "error");
            for (var i = 0; i < 6; i++) h.Runtime.CaptureLog("InvalidOperationException: again", null, "error");
            h.Runtime.CaptureLog("Plain message", null, "error");
            await h.Client.ProcessAsync(true);
            var errors = Single(h).Errors;
            Assert.Equal(10, errors.Count(e => e.GetProperty("type").GetString() == "InvalidOperationException"));
            Assert.Single(errors, e => e.GetProperty("type").GetString() == "LogError");
            Assert.Equal("Game.Player.Move() Player.cs:42", errors[0].GetProperty("frames")[0].GetString());
            Assert.True(errors[0].GetProperty("symbolicated").GetBoolean());
        }

        [Fact]
        public async Task TypeOnlyOmitsMessageAndFrames()
        {
            var h = new Harness(new PulseOptions { ErrorDetail = PulseErrorDetail.TypeOnly });
            h.Runtime.CaptureLog("NullReferenceException: secret", MonoTrace, "error", Skip); await h.Client.ProcessAsync(true);
            var error = Single(h).Errors.Single();
            Assert.Equal("NullReferenceException", error.GetProperty("type").GetString());
            Assert.False(error.TryGetProperty("message", out _)); Assert.False(error.TryGetProperty("frames", out _));
            Assert.False(error.TryGetProperty("symbolicated", out _));
        }

        [Fact]
        public void CaptureErrorsFalseOptOutAndEmptyInputsSendNothing()
        {
            var off = new Harness(new PulseOptions { CaptureErrors = false });
            off.Runtime.CaptureLog("Exception: x", MonoTrace, "error", Skip); off.Runtime.CaptureLog("Exception: x", MonoTrace, "crash", Skip);
            Assert.Equal(0, off.Client.Count); Assert.False(off.Storage.Files.ContainsKey(off.Client.CrashName));
            var h = new Harness();
            h.Runtime.CaptureLog(null, MonoTrace, "error", Skip); h.Runtime.CaptureLog("", MonoTrace, "error", Skip); h.Runtime.CaptureLog("  ", null, "error");
            Assert.Equal(0, h.Client.Count);
            h.Runtime.OptOut = true; h.Runtime.CaptureLog("Exception: x", MonoTrace, "error", Skip);
            Assert.Equal(0, h.Client.Count);
        }

        [Fact]
        public async Task CrashKindIsSavedToDiskAndSentAtTheNextLaunch()
        {
            var first = new Harness();
            first.Runtime.CaptureLog("Exception: died", MonoTrace, "crash", Skip);
            Assert.True(first.Storage.Files.ContainsKey(first.Client.CrashName));
            var second = new Harness(storage: first.Storage); await second.Client.ProcessAsync(true);
            Assert.Equal("crash", Single(second).Errors.Single().GetProperty("kind").GetString());
        }

        [Fact]
        public void NeverThrowsWhenStorageFailsAndPublicFacadeExists()
        {
            var h = new Harness(); h.Storage.Fail = true;
            h.Runtime.CaptureLog("Exception: x", MonoTrace, "crash", Skip); h.Runtime.CaptureLog("Exception: x", MonoTrace, "error", Skip);
            h.Runtime.CaptureLog("Exception: x", MonoTrace, "nonsense", Skip);
            Pulse.CaptureLog("Exception: x", MonoTrace, "error", Skip);
            Pulse.CaptureLog(null, null, null);
        }
    }
}
