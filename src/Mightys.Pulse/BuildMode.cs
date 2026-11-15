using System;
using System.Diagnostics;
using System.Reflection;

namespace Mightys.Pulse
{
    /// <summary>Decides the default for <see cref="PulseOptions.Debug"/> from how the application was built.</summary>
    internal static class BuildMode
    {
        /// <summary>True only when the entry assembly carries a <see cref="DebuggableAttribute"/> with the JIT optimizer
        /// disabled, which is what a Debug build produces. A missing entry assembly, a missing attribute or any
        /// failure while reading it returns false. A debugger, environment variables and the configuration of this
        /// library are never consulted.</summary>
        internal static bool EntryIsDebugBuild(Func<Assembly> entryAssembly = null, Func<Assembly, DebuggableAttribute> readAttribute = null)
        {
            try
            {
                var assembly = (entryAssembly ?? Assembly.GetEntryAssembly)();
                if (assembly == null) return false;
                var attribute = (readAttribute ?? (a => (DebuggableAttribute)Attribute.GetCustomAttribute(a, typeof(DebuggableAttribute))))(assembly);
                return attribute != null && attribute.IsJITOptimizerDisabled;
            }
            catch (Exception) { return false; }
        }
    }
}
