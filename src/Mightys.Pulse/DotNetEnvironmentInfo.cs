using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mightys.Pulse
{
    /// <summary>Metadata snapshot. MAUI hosts can pass DeviceInfo/AppInfo values without a MAUI dependency.</summary>
    public sealed class DotNetEnvironmentInfo : IEnvironmentInfo
    {
        /// <summary>Platform class: mobile for Android and iOS, otherwise desktop.</summary>
        public string Platform { get; }
        /// <summary>Normalized OS name: Windows, macOS, Linux, Android, iOS or empty.</summary>
        public string OsName { get; }
        /// <summary>Numeric OS version, at most three parts.</summary>
        public string OsVersion { get; }
        /// <summary>Application version.</summary>
        public string AppVersion { get; }
        /// <summary>Application build number, empty when unknown.</summary>
        public string AppBuildNumber { get; }
        /// <summary>Always empty; no device model is collected.</summary>
        public string DeviceModel => "";
        /// <summary>UI locale name.</summary>
        public string Locale { get; }
        /// <summary>Always empty.</summary>
        public string BundleId => "";
        /// <summary>Always empty.</summary>
        public string SigningCertSha256 => "";

        /// <summary>Detects the OS, OS version, entry assembly version and UI locale of the running process.</summary>
        public DotNetEnvironmentInfo() : this(CurrentOs(), Environment.OSVersion.Version.ToString(),
            EntryVersion(), "", CultureInfo.CurrentUICulture.Name) { }

        /// <summary>Creates a snapshot from values supplied by the host, for example MAUI <c>DeviceInfo</c> and <c>AppInfo</c>.
        /// <param name="osName">Platform name such as Windows, WinUI, macOS, MacCatalyst, Linux, Android or iOS.</param>
        /// <param name="osVersion">OS version string.</param>
        /// <param name="appVersion">Application version.</param>
        /// <param name="appBuildNumber">Application build number.</param>
        /// <param name="locale">UI locale name; defaults to the current UI culture.</param></summary>
        public DotNetEnvironmentInfo(string osName, string osVersion, string appVersion = "",
            string appBuildNumber = "", string locale = null)
        {
            OsName = NormalizeOs(osName);
            Platform = OsName == "Android" || OsName == "iOS" ? "mobile" : "desktop";
            OsVersion = SignalJson.OsVersionNumber(osVersion);
            AppVersion = appVersion ?? ""; AppBuildNumber = appBuildNumber ?? "";
            Locale = locale ?? CultureInfo.CurrentUICulture.Name;
        }

        private static string NormalizeOs(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "winui": case "windows": return "Windows";
                case "macos": case "maccatalyst": case "osx": return "macOS";
                case "linux": return "Linux";
                case "android": return "Android";
                case "ios": return "iOS";
                default: return "";
            }
        }

        private static string CurrentOs()
        {
#if NET8_0_OR_GREATER
            if (OperatingSystem.IsAndroid()) return "Android";
            if (OperatingSystem.IsIOS()) return "iOS";
            if (OperatingSystem.IsMacCatalyst()) return "macOS";
#endif
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
            return "";
        }

        private static string EntryVersion()
        { try { return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? ""; } catch (Exception) { return ""; } }
    }

    /// <summary>Default storage location.</summary>
    public static class PulseStorage
    {
        /// <summary>App-scoped directory below the operating system's local application data folder.</summary>
        public static string DefaultDirectory
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root)) throw new IOException("Local application data is unavailable; supply StorageDirectory.");
                var app = Assembly.GetEntryAssembly()?.GetName().Name ?? "application";
                return Path.Combine(root, "MightyS", "Pulse", SignalJson.Hash(app));
            }
        }
    }
}
