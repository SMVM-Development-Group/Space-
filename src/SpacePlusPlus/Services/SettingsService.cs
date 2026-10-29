using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Models;

namespace SpacePlusPlus.Services;

public sealed class SettingsService(ILogger<SettingsService> logger)
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpacePlusPlus");

    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string LogDirectory => Path.Combine(DataDir, "logs");
    public string HistoryPath => Path.Combine(DataDir, "history.json");

    public AppSettings Current { get; private set; } = LoadOrDefault();

    private static AppSettings LoadOrDefault()
        => JsonStore.Load<AppSettings>(Path.Combine(DataDir, "settings.json")) ?? new AppSettings();

    public void Save()
    {
        if (JsonStore.Save(SettingsPath, Current))
            logger.LogDebug("Settings saved to {Path}", SettingsPath);
        else
            logger.LogWarning("Failed to save settings to {Path}", SettingsPath);
    }

    public bool SetStartWithWindows(bool enabled)
    {
        try
        {
            const string keyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(keyName, writable: true);
            if (key is null) return false;
            if (enabled)
                key.SetValue("SpacePlusPlus", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValueNames().Contains("SpacePlusPlus"))
                key.DeleteValue("SpacePlusPlus", false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update startup registration");
            return false;
        }
    }

    public bool GetStartWithWindowsActual()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("SpacePlusPlus") is string;
        }
        catch
        {
            return false;
        }
    }
}
