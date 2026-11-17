using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mightys.Pulse
{
    // Values the .NET runtime can give without any permission. Nothing is guessed: a value that cannot be read is left out.
    internal static class DetectFields
    {
        internal static Dictionary<string, string> Static(string osName, string locale, IDictionary<string, string> host)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            Put(fields, "deviceType", osName == "Windows" || osName == "macOS" || osName == "Linux" ? "desktop" : null);
            Put(fields, "architecture", ArchitectureName());
            Put(fields, "runtimeVersion", RuntimeVersion());
            Put(fields, "appName", ProductName());
            fields["isDebugBuild"] = PulseInfo.DebugBuild ? "true" : "false";
            var ui = Culture(locale) ?? Safe(() => CultureInfo.CurrentUICulture);
            Put(fields, "layoutDirection", ui == null ? null : ui.TextInfo.IsRightToLeft ? "rtl" : "ltr");
            Put(fields, "language", Language(ui));
            Put(fields, "preferredLanguage", Language(Safe(() => CultureInfo.InstalledUICulture)));
            Put(fields, "region", Region(Safe(() => CultureInfo.CurrentCulture)));
            if (host != null) foreach (var pair in host) if (pair.Key != null) fields[pair.Key] = pair.Value;
            return fields;
        }

        private static void Put(Dictionary<string, string> fields, string key, string value)
        { if (!string.IsNullOrEmpty(value)) fields[key] = value; }

        private static T Safe<T>(Func<T> read) where T : class { try { return read(); } catch (Exception) { return null; } }

        private static CultureInfo Culture(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try { return CultureInfo.GetCultureInfo(name); } catch (Exception) { return null; }
        }

        internal static string Language(CultureInfo culture)
        {
            if (culture == null || culture.Name.Length == 0) return null;
            var code = culture.TwoLetterISOLanguageName;
            return string.IsNullOrEmpty(code) || code == "iv" ? null : code;
        }

        internal static string Region(CultureInfo culture)
        {
            try
            {
                if (culture == null || culture.Name.Length == 0 || culture.IsNeutralCulture) return null;
                var code = new RegionInfo(culture.Name).TwoLetterISORegionName;
                return string.IsNullOrEmpty(code) || code.Length != 2 ? null : code;
            }
            catch (Exception) { return null; }
        }

        internal static string ArchitectureName() => ArchitectureName(Safe0(() => RuntimeInformation.ProcessArchitecture));

        internal static string ArchitectureName(Architecture? architecture)
        {
            switch (architecture)
            {
                case System.Runtime.InteropServices.Architecture.X64: return "x86_64";
                case System.Runtime.InteropServices.Architecture.Arm64: return "arm64";
                case System.Runtime.InteropServices.Architecture.Arm: return "armv7";
                case System.Runtime.InteropServices.Architecture.X86: return "x86";
                default: return null;
            }
        }

        private static Architecture? Safe0(Func<Architecture> read) { try { return read(); } catch (Exception) { return null; } }

        // ".NET 9.0.7" for .NET 5 and later. Other runtimes (.NET Framework, Mono) are left out.
        internal static string RuntimeVersion() =>
            RuntimeVersion(Safe(() => RuntimeInformation.FrameworkDescription), Safe(() => Environment.Version));

        internal static string RuntimeVersion(string description, Version version)
        {
            if (description == null || version == null || !description.StartsWith(".NET ", StringComparison.Ordinal) ||
                description.StartsWith(".NET Framework", StringComparison.Ordinal) ||
                description.StartsWith(".NET Native", StringComparison.Ordinal) || version.Major < 5) return null;
            return ".NET " + version.ToString(3);
        }

        private static string ProductName()
        {
            try
            {
                var product = (AssemblyProductAttribute)Attribute.GetCustomAttribute(Assembly.GetEntryAssembly(), typeof(AssemblyProductAttribute));
                var name = product?.Product?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception) { return null; }
        }

        // IANA time zone id. Linux and macOS already report one; Windows reports its own id, converted when the runtime can.
        internal static string IanaZone(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return null;
                return IanaZone(id, RuntimeInformation.IsOSPlatform(OSPlatform.Windows), ConvertWindowsId, CanonicalIana);
            }
            catch (Exception) { return null; }
        }

        internal static string IanaZone(string id, bool windows, Func<string, string> convert, Func<string, string> canonical = null)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (!windows)
            {
                if (id.Length > 64) return null;
                // Area/Location ids and UTC are sent as they are. A legacy alias such as "Egypt" or "Japan" (what
                // the system reports when the zone file is a link) is mapped to its canonical id, or left out.
                if (id.IndexOf('/') >= 0 || id == "UTC") return id;
                var mapped = canonical?.Invoke(id);
                return !string.IsNullOrEmpty(mapped) && mapped.IndexOf('/') >= 0 && mapped.Length <= 64 ? mapped : null;
            }
            var iana = convert(id);
            return string.IsNullOrEmpty(iana) ? null : iana;
        }

        // Alias to canonical through the runtime's own zone tables: alias -> Windows id -> IANA id.
        private static string CanonicalIana(string id)
        {
            var toWindows = typeof(TimeZoneInfo).GetMethod("TryConvertIanaIdToWindowsId", new[] { typeof(string), typeof(string).MakeByRefType() });
            if (toWindows == null) return null;
            var args = new object[] { id, null };
            if (!(bool)toWindows.Invoke(null, args)) return null;
            return ConvertWindowsId((string)args[1]);
        }

        private static string ConvertWindowsId(string id)
        {
            var method = typeof(TimeZoneInfo).GetMethod("TryConvertWindowsIdToIanaId", new[] { typeof(string), typeof(string).MakeByRefType() });
            if (method == null) return null;
            var args = new object[] { id, null };
            return (bool)method.Invoke(null, args) ? (string)args[1] : null;
        }
    }
}
