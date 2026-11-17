// A minimal console app that sends three signals. Run it with your own app key:
//   PULSE_APP_KEY=your-app-key dotnet run --project samples/Quickstart
using Mightys.Pulse;

var key = Environment.GetEnvironmentVariable("PULSE_APP_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Set PULSE_APP_KEY to the app key from your MightyS Pulse dashboard.");
    return 2;
}

// A Debug build (the default for dotnet run) marks these signals as test data; a Release build sends real ones.
// Set PulseOptions.Debug to choose yourself.
// App Control (required updates, maintenance windows) runs by itself; this only prints when its state changes.
Pulse.Control.Changed += (_, state) => Console.WriteLine("App Control: " + state.Kind);
Pulse.Initialize(key, new PulseOptions(), new PulsePlatformOptions { Logger = Console.WriteLine });

Pulse.Track("app_start");
Pulse.Track("level_complete", 42);
Pulse.Track("purchase", 4.99, new Dictionary<string, object> { ["sku"] = "gems_100" });

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
await Pulse.Flush(timeout.Token);
await Pulse.ShutdownAsync();
Console.WriteLine("Done.");
return 0;
