using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Mightys.Pulse.Tests
{
    // The four shared App Control tables, copied byte for byte next to the test binaries. A checkout without the tables
    // (the standalone public tree) skips the tests that read them, with a visible message.
    internal static class ControlTables
    {
        internal const string VersionCasesSha = "027cc69450b95177d4c50ee2a9026df3dfe05e2b6f46d4e449dca872b4ab4009";
        internal const string DecisionCasesSha = "fdc4ef65c3bf22e1a5775e756d5660cf3608085eb2a5977c683b25affd15c622";
        internal const string ControlIdCasesSha = "f236a1c986276ea69aeca69ccafff368ba8a07a749269986814e57b256d01b2f";
        internal const string TextsSha = "39aac628bda36bbb86fb4a091dad3252be82dd2a6353af9ea1db574a5264dbf4";

        internal const string MissingMessage = "The shared App Control tables are not present in this tree.";
        internal static bool Present => File.Exists(PathOf("decision-cases.json"));

        internal static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "control-v1", name);
        internal static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));
        internal static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(PathOf(name), Encoding.UTF8));

        internal static string Sha256(string name)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Bytes(name));
                var b = new StringBuilder();
                foreach (var x in hash) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }
    }

    public sealed class TablesFactAttribute : FactAttribute
    {
        public TablesFactAttribute() { if (!ControlTables.Present) Skip = ControlTables.MissingMessage; }
    }

    public sealed class TablesTheoryAttribute : TheoryAttribute
    {
        public TablesTheoryAttribute() { if (!ControlTables.Present) Skip = ControlTables.MissingMessage; }
    }
}
