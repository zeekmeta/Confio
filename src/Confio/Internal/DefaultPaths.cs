using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Confio.Internal;

internal static class DefaultPaths
{
    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static string Configuration(ConfigurationFormat format) =>
        Path.Combine(Folder(configuration: true), "Confio", ApplicationName(), "appsettings." + format.ToString().ToLowerInvariant());

    internal static string Key() => Path.Combine(Folder(configuration: false), "Confio", "keys", ApplicationName() + ".key");

    private static string ApplicationName()
    {
        var name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("The application name is unavailable. Specify the configuration and automatic key file paths explicitly.");
        foreach (var character in Path.GetInvalidFileNameChars()) name = name!.Replace(character, '_');
        return name!;
    }

    private static string Folder(bool configuration)
    {
        if (IsWindows) return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return Path.Combine(user, "Library", "Application Support");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var xdg = Environment.GetEnvironmentVariable(configuration ? "XDG_CONFIG_HOME" : "XDG_DATA_HOME");
            return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) ? xdg! :
                Path.Combine(user, configuration ? ".config" : ".local/share");
        }
        throw new PlatformNotSupportedException("Specify file paths and an explicit key or protector on this platform.");
    }
}
