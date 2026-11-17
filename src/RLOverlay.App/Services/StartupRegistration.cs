using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RLOverlay.App.Services;

/// <summary>
/// "Run at Windows startup": a value under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (per user, no
/// admin rights). The key path can be changed for testing.
/// </summary>
public sealed class StartupRegistration
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RLOverlay";

    private readonly string _keyPath;
    private readonly string _command;
    private readonly ILogger _logger;

    public StartupRegistration(ILogger<StartupRegistration> logger, string keyPath = DefaultKeyPath, string? exePath = null)
    {
        _logger = logger;
        _keyPath = keyPath;
        _command = $"\"{exePath ?? Environment.ProcessPath}\"";
    }

    /// <summary>True when the value exists and points at this exe.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                return key?.GetValue(ValueName) is string value && string.Equals(value, _command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the value. Returns false (and logs) if the registry refused.</summary>
    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, _command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            _logger.LogInformation("Run at startup {State}", enabled ? "on" : "off");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not change the startup entry");
            return false;
        }
    }
}
