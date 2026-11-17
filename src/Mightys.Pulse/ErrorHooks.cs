using System;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    // Process-wide error hooks of one SDK instance. Both are events, so every handler that was there before still runs;
    // nothing is replaced, swallowed or marked as observed.
    internal sealed class ErrorHooks
    {
        private readonly PulseRuntime runtime;
        private readonly object gate = new object();
        private bool installed;
        internal ErrorHooks(PulseRuntime runtime) { this.runtime = runtime; }
        internal bool Installed { get { lock (gate) return installed; } }

        internal void Install()
        {
            try
            {
                lock (gate)
                {
                    if (installed) return;
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                    TaskScheduler.UnobservedTaskException += OnUnobserved;
                    installed = true;
                }
            }
            catch (Exception) { }
        }

        internal void Uninstall()
        {
            try
            {
                lock (gate)
                {
                    if (!installed) return;
                    AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
                    TaskScheduler.UnobservedTaskException -= OnUnobserved;
                    installed = false;
                }
            }
            catch (Exception) { }
        }

        // The process is ending (a terminating event): the crash is written to disk before this returns.
        internal void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
        {
            try
            {
                if (args?.ExceptionObject is Exception exception)
                    runtime.CaptureUnhandled(exception, args.IsTerminating ? ErrorKinds.Crash : ErrorKinds.Error);
            }
            catch (Exception) { }
        }

        // The app goes on. SetObserved is never called, so the runtime's own policy for the task is unchanged.
        internal void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs args)
        {
            try { if (args?.Exception != null) runtime.CaptureUnhandled(args.Exception, ErrorKinds.Error); }
            catch (Exception) { }
        }
    }
}
