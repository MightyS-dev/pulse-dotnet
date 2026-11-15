using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Mightys.Pulse
{
    /// <summary>Metadata snapshot. MAUI hosts can pass DeviceInfo/AppInfo values without a MAUI dependency.</summary>
    public sealed class DotNetEnvironmentInfo : IEnvironmentInfo, IExtraSystemFields
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
        public DotNetEnvironmentInfo() : this(null, null) { }

        /// <summary>Detects the process the same way and adds values only the host knows, for example screen size or colour scheme.
        /// <param name="hostFields">Fixed values, read once. They win over detected values. Only the documented system field names are sent.</param>
        /// <param name="dynamicHostFields">Optional callback for values that change, read each time a signal is captured. It must be cheap and must not block.</param></summary>
        public DotNetEnvironmentInfo(IDictionary<string, string> hostFields, Func<IDictionary<string, string>> dynamicHostFields = null)
            : this(CurrentOs(), Environment.OSVersion.Version.ToString(),
                EntryVersion(), "", CultureInfo.CurrentUICulture.Name, null, hostFields, dynamicHostFields) { }

        /// <summary>Creates a snapshot from values supplied by the host, for example MAUI <c>DeviceInfo</c> and <c>AppInfo</c>.
        /// <param name="osName">Platform name such as Windows, WinUI, macOS, MacCatalyst, Linux, Android or iOS.</param>
        /// <param name="osVersion">OS version string.</param>
        /// <param name="appVersion">Application version.</param>
        /// <param name="appBuildNumber">Application build number.</param>
        /// <param name="locale">UI locale name; defaults to the current UI culture.</param>
        /// <param name="deviceModel">Device model, for example MAUI <c>DeviceInfo.Model</c>; defaults as described for <see cref="DeviceModel"/>.</param>
        /// <param name="hostFields">Fixed extra system fields the host knows, read once, for example <c>deviceType</c>, <c>screenWidth</c>, <c>osApiLevel</c>. They win over detected values.</param>
        /// <param name="dynamicHostFields">Extra system fields that change, read each time a signal is captured, for example <c>colorScheme</c> or <c>orientation</c>. Must be cheap and must not block.</param></summary>
        public DotNetEnvironmentInfo(string osName, string osVersion, string appVersion = "",
            string appBuildNumber = "", string locale = null, string deviceModel = null,
            IDictionary<string, string> hostFields = null, Func<IDictionary<string, string>> dynamicHostFields = null)
        {
            OsName = NormalizeOs(osName);
            Platform = OsName == "Android" || OsName == "iOS" ? "mobile" : "desktop";
            OsVersion = SignalJson.OsVersionNumber(osVersion);
            AppVersion = appVersion ?? ""; AppBuildNumber = appBuildNumber ?? "";
            Locale = locale ?? CultureInfo.CurrentUICulture.Name;
            DeviceModel = string.IsNullOrWhiteSpace(deviceModel) ? DefaultModel(OsName) : deviceModel;
            this.dynamicHostFields = dynamicHostFields;
            StaticFields = DetectFields.Static(OsName, Locale, hostFields);
        }

        private readonly Func<IDictionary<string, string>> dynamicHostFields;
        internal Func<TimeZoneInfo> ZoneSource = () => TimeZoneInfo.Local;
        internal Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;
        private string zoneId, zoneIana;

        /// <summary>Extra system fields detected once: deviceType (desktop systems), architecture, runtimeVersion, appName,
        /// isDebugBuild, layoutDirection, language, preferredLanguage, region, plus the host's fixed values. A value that cannot be determined is left out.</summary>
        public IReadOnlyDictionary<string, string> StaticFields { get; }

        /// <summary>Extra system fields read at capture time: timeZone (IANA id) and utcOffsetMinutes, plus the host's dynamic values.</summary>
        public IReadOnlyDictionary<string, string> DynamicFields()
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var zone = ZoneSource();
                if (zone != null)
                {
                    if (zone.Id != zoneId) { zoneId = zone.Id; zoneIana = DetectFields.IanaZone(zone.Id); }
                    if (!string.IsNullOrEmpty(zoneIana)) fields["timeZone"] = zoneIana;
                    fields["utcOffsetMinutes"] = ((int)zone.GetUtcOffset(Now()).TotalMinutes).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception) { }
            try
            {
                var host = dynamicHostFields?.Invoke();
                if (host != null) foreach (var pair in host) if (pair.Key != null) fields[pair.Key] = pair.Value;
            }
            catch (Exception) { }
            return fields;
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
