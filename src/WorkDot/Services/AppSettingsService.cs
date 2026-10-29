using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace WorkDot.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetThemeMode
{
    System, // 윈도우 테마에 따라 자동 조정
    Dark,   // 다크 고정
    Light   // 라이트 고정
}

public sealed class AppSettings
{
    public WidgetThemeMode ThemeMode { get; set; } = WidgetThemeMode.System;
    public string WorkingColorHex { get; set; } = "#10B981"; // 기본 네온 에메랄드 그린
    public string OffWorkColorHex { get; set; } = "#EF4444"; // 기본 루비 레드
    public double WidgetOpacity { get; set; } = 0.95;        // 0.25 ~ 1.0
    public double WidgetScale { get; set; } = 1.0;           // 0.70 ~ 1.60
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = false;         // 작업표시줄 아이콘 표시 여부
    public bool RunAtStartup { get; set; } = false;
    public bool AutoUpdateEnabled { get; set; } = true;      // GitHub Release 자동 업데이트 활성화 여부
}

public sealed class AppSettingsService : IDisposable
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppRegistryName = "WorkDot";

    private readonly string _settingsFilePath;

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? SettingsChanged;

    public static string AppVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            return ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.2.0";
        }
    }

    public AppSettingsService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkDot");
        Directory.CreateDirectory(folder);
        _settingsFilePath = Path.Combine(folder, "settings.json");

        Load();
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    Current = Normalize(loaded);
                }
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        Current.RunAtStartup = IsStartupRegistered();
    }

    public void SaveAndApply(Action<AppSettings>? mutate = null)
    {
        mutate?.Invoke(Current);
        Current = Normalize(Current);

        ApplyStartupRegistry(Current.RunAtStartup);

        try
        {
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Ignore IO errors
        }

        SettingsChanged?.Invoke(Current);
    }

    public void ResetToDefaults()
    {
        Current = new AppSettings();
        SaveAndApply();
    }

    public bool IsEffectiveLightTheme()
    {
        return Current.ThemeMode switch
        {
            WidgetThemeMode.Light => true,
            WidgetThemeMode.Dark => false,
            _ => IsWindowsSystemLightTheme()
        };
    }

    public static bool IsWindowsSystemLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            object? val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal)
            {
                return intVal != 0;
            }
        }
        catch
        {
            // Default to dark if unreadable
        }
        return false;
    }

    public static System.Windows.Media.Color ParseColorOrDefault(string? hex, string fallbackHex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                string candidate = hex.Trim();
                if (!candidate.StartsWith('#'))
                    candidate = "#" + candidate;

                if (System.Windows.Media.ColorConverter.ConvertFromString(candidate) is System.Windows.Media.Color c)
                    return c;
            }
            catch
            {
                // Fall through
            }
        }

        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallbackHex);
    }

    public static bool TryParseColor(string? hex, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        try
        {
            string candidate = hex.Trim();
            if (!candidate.StartsWith('#'))
                candidate = "#" + candidate;

            if (System.Windows.Media.ColorConverter.ConvertFromString(candidate) is System.Windows.Media.Color c)
            {
                color = c;
                return true;
            }
        }
        catch
        {
            // Invalid color
        }
        return false;
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.WidgetOpacity = Math.Clamp(Math.Round(s.WidgetOpacity, 2), 0.25, 1.0);
        s.WidgetScale = Math.Clamp(Math.Round(s.WidgetScale, 2), 0.70, 1.60);

        if (!TryParseColor(s.WorkingColorHex, out _))
            s.WorkingColorHex = "#10B981";
        else if (!s.WorkingColorHex.StartsWith('#'))
            s.WorkingColorHex = "#" + s.WorkingColorHex.Trim();

        if (!TryParseColor(s.OffWorkColorHex, out _))
            s.OffWorkColorHex = "#EF4444";
        else if (!s.OffWorkColorHex.StartsWith('#'))
            s.OffWorkColorHex = "#" + s.OffWorkColorHex.Trim();

        return s;
    }

    private static bool IsStartupRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false);
            string? val = key?.GetValue(AppRegistryName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyStartupRegistry(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true);
            if (key == null)
                return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppRegistryName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(AppRegistryName) != null)
                {
                    key.DeleteValue(AppRegistryName, false);
                }
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Current.ThemeMode == WidgetThemeMode.System)
        {
            SettingsChanged?.Invoke(Current);
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
    }
}
