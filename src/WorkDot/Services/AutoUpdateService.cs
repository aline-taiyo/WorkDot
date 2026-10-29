using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace WorkDot.Services;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersionTag,
    string LatestVersionTag,
    string? DownloadUrl,
    string? AssetName,
    string Message);

/// <summary>
/// GitHub Releases (https://github.com/aline-taiyo/WorkDot/releases) 기반 버전 확인 및 자동 업데이트를 수행합니다.
/// </summary>
public sealed class AutoUpdateService : IDisposable
{
    public const string GitHubRepoUrl = "https://github.com/aline-taiyo/WorkDot";
    public const string GitHubReleasesUrl = "https://github.com/aline-taiyo/WorkDot/releases";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/aline-taiyo/WorkDot/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly AppSettingsService _settingsService;
    private readonly DispatcherTimer _periodicTimer;
    private bool _isUpdating;

    public string LastStatusMessage { get; private set; } = "최신 버전 확인 대기";

    public event Action<string>? StatusChanged;

    public AutoUpdateService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;

        _periodicTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(4)
        };
        _periodicTimer.Tick += async (_, _) => await RunBackgroundAutoUpdateIfEnabledAsync();
    }

    public void StartBackgroundSchedule()
    {
        _periodicTimer.Start();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await RunBackgroundAutoUpdateIfEnabledAsync();
            });
        });
    }

    public static void OpenGitHubRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(GitHubRepoUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch error
        }
    }

    public async Task RunBackgroundAutoUpdateIfEnabledAsync()
    {
        if (!_settingsService.Current.AutoUpdateEnabled || _isUpdating)
            return;

        var result = await CheckForUpdateAsync();
        if (result.IsUpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.AssetName))
        {
            await DownloadAndApplyUpdateAsync(result.DownloadUrl, result.AssetName);
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        string currentTag = AppSettingsService.AppVersion;
        Version currentVer = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        try
        {
            UpdateStatus("GitHub 릴리스 확인 중...");
            using var response = await Http.GetAsync(LatestReleaseApiUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                string msg = $"현재 최신 버전 ({currentTag}) · 배포된 릴리스 없음";
                UpdateStatus(msg);
                return new UpdateCheckResult(false, currentTag, currentTag, null, null, msg);
            }

            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? currentTag) : currentTag;
            Version? latestVer = ParseVersionFromTag(tagName);

            string? downloadUrl = null;
            string? assetName = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                    string? url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        downloadUrl = url;
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            break; // zip 패키지 우선
                    }
                }
            }

            if (latestVer != null && IsNewerVersion(latestVer, currentVer))
            {
                string msg = downloadUrl != null
                    ? $"새 버전 발견: {tagName} (현재 {currentTag})"
                    : $"새 태그 {tagName} 발견 (릴리스 첨부 파일 없음)";
                UpdateStatus(msg);
                return new UpdateCheckResult(true, currentTag, tagName, downloadUrl, assetName, msg);
            }

            string upToDateMsg = $"최신 버전 사용 중 ({currentTag})";
            UpdateStatus(upToDateMsg);
            return new UpdateCheckResult(false, currentTag, tagName, null, null, upToDateMsg);
        }
        catch (Exception ex)
        {
            string errMsg = $"업데이트 확인 실패: {ex.Message}";
            UpdateStatus(errMsg);
            return new UpdateCheckResult(false, currentTag, currentTag, null, null, errMsg);
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, string assetName)
    {
        if (_isUpdating)
            return false;

        _isUpdating = true;
        try
        {
            UpdateStatus($"업데이트 다운로드 중 ({assetName})...");

            string updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkDot",
                "update");

            if (Directory.Exists(updateRoot))
            {
                try { Directory.Delete(updateRoot, true); } catch { }
            }
            Directory.CreateDirectory(updateRoot);

            string downloadedFile = Path.Combine(updateRoot, assetName);
            using (var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(downloadedFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
            }

            string stagingDir = Path.Combine(updateRoot, "staging");
            Directory.CreateDirectory(stagingDir);

            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStatus("업데이트 압축 해제 중...");
                ZipFile.ExtractToDirectory(downloadedFile, stagingDir, overwriteFiles: true);
            }
            else
            {
                File.Copy(downloadedFile, Path.Combine(stagingDir, "WorkDot.exe"), overwrite: true);
            }

            // 하위 폴더 하나 안에 압축된 경우 루트로 보정
            string[] exeFiles = Directory.GetFiles(stagingDir, "WorkDot.exe", SearchOption.AllDirectories);
            string sourceDir = exeFiles.Length > 0
                ? (Path.GetDirectoryName(exeFiles[0]) ?? stagingDir)
                : stagingDir;

            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentExe = Environment.ProcessPath ?? Path.Combine(appDir, "WorkDot.exe");
            int currentPid = Environment.ProcessId;

            string scriptPath = Path.Combine(updateRoot, "apply-update.cmd");
            string scriptContent = $"""
                @echo off
                chcp 65001 >nul
                timeout /t 2 /nobreak >nul
                taskkill /PID {currentPid} /F >nul 2>&1
                timeout /t 1 /nobreak >nul
                xcopy "{sourceDir}\*" "{appDir}\" /E /Y /I /Q >nul 2>&1
                start "" "{currentExe}"
                exit
                """;

            File.WriteAllText(scriptPath, scriptContent, new UTF8Encoding(false));

            UpdateStatus("업데이트 적용 및 재시작 중...");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = updateRoot
            });

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus($"업데이트 적용 실패: {ex.Message}");
            _isUpdating = false;
            return false;
        }
    }

    private void UpdateStatus(string msg)
    {
        LastStatusMessage = msg;
        StatusChanged?.Invoke(msg);
    }

    private static bool IsNewerVersion(Version latest, Version current)
    {
        int latestBuild = Math.Max(0, latest.Build);
        int currentBuild = Math.Max(0, current.Build);
        var normLatest = new Version(latest.Major, latest.Minor, latestBuild);
        var normCurrent = new Version(current.Major, current.Minor, currentBuild);
        return normLatest > normCurrent;
    }

    private static Version? ParseVersionFromTag(string tag)
    {
        string clean = tag.Trim().TrimStart('v', 'V');
        int dashIdx = clean.IndexOf('-');
        if (dashIdx > 0)
            clean = clean[..dashIdx];

        return Version.TryParse(clean, out Version? v) ? v : null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorkDot-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public void Dispose()
    {
        _periodicTimer.Stop();
    }
}
