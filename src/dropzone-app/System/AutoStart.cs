using Microsoft.Win32;

namespace Dropzone.App.SystemIntegration;

/// <summary>Start with Windows through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
public static class AutoStart
{
    public const string BackgroundArgument = "--background";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalSendDropzone";

    /// <summary>Writes <c>"&lt;exe&gt;" --background</c> when enabled, removes the value when not.</summary>
    public static void Apply(bool enabled, string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{exePath}\" {BackgroundArgument}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
