using OrionCopy.Watchdog.Cli;

// All commands are dispatched in CliApp. `start` hosts the monitoring engine
// (console / Windows Service / systemd); the rest are local/CLI operations.
return await CliApp.RunAsync(args).ConfigureAwait(false);
