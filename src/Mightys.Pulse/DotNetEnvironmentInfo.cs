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
        /// <summary>Device model supplied by the host. Without one: PC on Windows, Desktop on Linux, the hardware model on macOS (for example Mac14,2, or Mac when unreadable), and empty on Android and iOS.</summary>
        public string DeviceModel { get; }
        /// <summary>UI locale name.</summary>
        public string Locale { get; }
        /// <summary>Always empty.</summary>
        public string BundleId => "";
        /// <summary>Always empty.</summary>
        public string SigningCertSha256 => "";

        /// <summary>Detects the OS, OS version, entry assembly version and UI locale of the running process.</summary>
        public DotNetEnvironmentInfo() : this(CurrentOs(), Environment.OSVersion.Version.ToString(),
            EntryVersion(), "", CultureInfo.CurrentUICulture.Name, null) { }

        /// <summary>Creates a snapshot from values supplied by the host, for example MAUI <c>DeviceInfo</c> and <c>AppInfo</c>.
        /// <param name="osName">Platform name such as Windows, WinUI, macOS, MacCatalyst, Linux, Android or iOS.</param>
        /// <param name="osVersion">OS version string.</param>
        /// <param name="appVersion">Application version.</param>
        /// <param name="appBuildNumber">Application build number.</param>
        /// <param name="locale">UI locale name; defaults to the current UI culture.</param>
        /// <param name="deviceModel">Device model, for example MAUI <c>DeviceInfo.Model</c>; defaults as described for <see cref="DeviceModel"/>.</param></summary>
        public DotNetEnvironmentInfo(string osName, string osVersion, string appVersion = "",
            string appBuildNumber = "", string locale = null, string deviceModel = null)
        {
            OsName = NormalizeOs(osName);
            Platform = OsName == "Android" || OsName == "iOS" ? "mobile" : "desktop";
            OsVersion = SignalJson.OsVersionNumber(osVersion);
            AppVersion = appVersion ?? ""; AppBuildNumber = appBuildNumber ?? "";
            Locale = locale ?? CultureInfo.CurrentUICulture.Name;
            DeviceModel = string.IsNullOrWhiteSpace(deviceModel) ? DefaultModel(OsName) : deviceModel;
        }

        private static string DefaultModel(string osName)
        {
            switch (osName)
            {
                case "Windows": return "PC";
                case "Linux": return "Desktop";
                case "macOS": return MacModel() ?? "Mac";
                default: return "";
            }
        }

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, byte[] value, ref IntPtr length, IntPtr newValue, IntPtr newLength);

        // Hardware model such as Mac14,2, read without any permission. Null when it cannot be read.
        private static string MacModel()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return null;
                var buffer = new byte[128]; var length = (IntPtr)buffer.Length;
                if (SysctlByName("hw.model", buffer, ref length, IntPtr.Zero, IntPtr.Zero) != 0) return null;
                var count = (int)length; if (count > 0 && buffer[count - 1] == 0) count--;
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, count).Trim();
                return text.Length == 0 || text.Length > 64 ? null : text;
            }
            catch (Exception) { return null; }
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
