using System.Runtime.CompilerServices;

namespace Insolvia.Api.Tests.Support;

/// <summary>
/// Turns off the hosts' configuration reload for the whole assembly, before
/// any <c>WebApplicationFactory</c> builds one.
/// </summary>
/// <remarks>
/// Every host the default builder makes watches its content root
/// (<c>src/Insolvia.Api</c>) for <c>appsettings*.json</c> — files the API does
/// not have. On macOS each watcher is an FSEvents stream, and hundreds of
/// hosts are alive at once here; a change anywhere under that directory set
/// every one reloading, re-registering and reloading again, until host
/// construction stalled and the run never finished (the last ~300 tests never
/// reported). Linux's inotify hid it in CI. Nothing is lost: there is no file
/// to reload, and production reads environment variables only.
/// </remarks>
internal static class NoConfigReload
{
    [ModuleInitializer]
    internal static void Disable() => Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
