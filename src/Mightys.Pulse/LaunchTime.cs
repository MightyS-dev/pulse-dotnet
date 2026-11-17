using System;
using System.Diagnostics;

namespace Mightys.Pulse
{
    // Launch time of a .NET app: from the start of the process to the first signal. A host that knows its first frame time can do better
    // on other platforms; here the first signal is the best moment the runtime can see. Never throws.
    internal static class LaunchTime
    {
        // A value above this is not a launch (the SDK was started long after the process), so nothing is sent.
        internal const long MaxLaunchMs = 10 * 60 * 1000;

        internal static long? SinceProcessStart(DateTimeOffset firstSignal)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                    return Between(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero), firstSignal);
            }
            catch (Exception) { return null; }
        }

        internal static long? Between(DateTimeOffset processStart, DateTimeOffset firstSignal)
        {
            var ms = (long)(firstSignal - processStart).TotalMilliseconds;
            return ms < 0 || ms > MaxLaunchMs ? (long?)null : ms;
        }
    }
}
