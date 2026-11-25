using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WorkDot.Services;

public sealed record BridgeCommand(string Id, string Action, DateTime ExpiresUtc);

public sealed record BridgeCommandResult(
    bool Success,
    bool NeedsLogin,
    string Message,
    string? ProbeJson = null);

/// <summary>
/// Chrome/Edge 확장프로그램과 Windows 위젯 간 로컬 루프백(127.0.0.1) HTTP JSON 통신 브릿지 서버입니다.
/// Windows WSL2/Hyper-V 예약 포트 대역(49152~65535) 충돌을 피하기 위해 안전 대역(38912 등) 다중 폴백 포트를 사용합니다.
/// </summary>
public sealed class ChromeExtensionBridgeService : IDisposable
{
    public const int PrimaryPort = 38912;

    /// <summary>
    /// Windows 동적 예약 포트 대역(49152~65535) 바깥의 안전 대역 우선 + 구버전 호환(49812) 폴백 포트 목록
    /// </summary>
    public static readonly int[] CandidatePorts = [38912, 41812, 45812, 49812];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ConnectedWindow = TimeSpan.FromSeconds(35);

    private readonly ConcurrentQueue<BridgeCommand> _commandQueue = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeCommandResult>> _pendingResults = new();
    private readonly ConcurrentDictionary<string, byte> _cancelledCommandIds = new();

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoopTask;
    private DateTime _lastSeenUtc = DateTime.MinValue;
    private bool _wasConnected;

    public int ActivePort { get; private set; } = PrimaryPort;

    public DateTime LastExtensionSeenUtc => _lastSeenUtc;

    public bool IsExtensionConnected => (DateTime.UtcNow - _lastSeenUtc) <= ConnectedWindow;

    public event Action<string>? ProbeReceived;
    public event Action<bool>? ConnectionStateChanged;

    public static string InstalledExtensionFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDot", "ChromeExtension");

    public static void SyncExtensionFilesToLocalAppData()
    {
        try
        {
            string? sourceDir = ResolveSourceExtensionFolder();
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return;

            string destDir = InstalledExtensionFolderPath;
            Directory.CreateDirectory(destDir);

            foreach (string srcFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(sourceDir, srcFile);
                string dstFile = Path.Combine(destDir, relPath);
                string? dstParent = Path.GetDirectoryName(dstFile);
                if (!string.IsNullOrWhiteSpace(dstParent))
                {
                    Directory.CreateDirectory(dstParent);
                }

                File.Copy(srcFile, dstFile, overwrite: true);
            }
        }
        catch
        {
            // Ignore extension sync copy errors
        }
    }

    private static string? ResolveSourceExtensionFolder()
    {
        string baseCandidate = Path.Combine(AppContext.BaseDirectory, "ChromeExtension");
        if (Directory.Exists(baseCandidate))
            return baseCandidate;

        string cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "src", "WorkDot", "ChromeExtension");
        if (Directory.Exists(cwdCandidate))
            return cwdCandidate;

        return null;
    }

    public void Start()
    {
        if (_listener != null)
            return;

        SyncExtensionFilesToLocalAppData();

        _cts = new CancellationTokenSource();

        // WSL2/Hyper-V 예약 포트(excludedportrange) 또는 타 프로세스 점유 시 다음 안전 대역 포트로 자동 폴백
        for (int round = 0; round < 2 && _listener == null; round++)
        {
            foreach (int port in CandidatePorts)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start();
                    _listener = listener;
                    ActivePort = port;
                    break;
                }
                catch (SocketException)
                {
                    // 예약 포트(AccessDenied) 또는 점유 중(AddressAlreadyInUse) -> 다음 후보 포트로 이동
                }
            }

            if (_listener == null && round == 0)
            {
                Thread.Sleep(250);
            }
        }

        if (_listener != null)
        {
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
    }

    public void EnqueueFireAndForget(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        if (action == "REFRESH" && _commandQueue.Any(c => c.Action == "REFRESH" && c.ExpiresUtc > DateTime.UtcNow))
            return;

        _commandQueue.Enqueue(new BridgeCommand(Guid.NewGuid().ToString("N"), action, DateTime.UtcNow.AddSeconds(15)));
    }

    public async Task<BridgeCommandResult> SendCommandAsync(string action, TimeSpan? timeout = null)
    {
        string id = Guid.NewGuid().ToString("N");
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(22);
        var tcs = new TaskCompletionSource<BridgeCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResults[id] = tcs;
        _commandQueue.Enqueue(new BridgeCommand(id, action, DateTime.UtcNow.Add(effectiveTimeout)));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(effectiveTimeout));
        if (completed == tcs.Task)
        {
            return await tcs.Task;
        }

        // 타임아웃된 명령이 큐에 남아 있다가 브라우저 실행 직후 뒤늦게 실행(중복 탭 오픈 등)되지 않도록 즉시 취소 마킹
        _cancelledCommandIds[id] = 1;
        _pendingResults.TryRemove(id, out _);
        return new BridgeCommandResult(false, false, "크롬 확장프로그램 응답 시간이 초과되었습니다.");
    }

    public void CheckConnectionTimeout()
    {
        if (_wasConnected && !IsExtensionConnected)
        {
            _wasConnected = false;
            ConnectionStateChanged?.Invoke(false);
        }
    }

    private void MarkExtensionSeen()
    {
        _lastSeenUtc = DateTime.UtcNow;
        if (!_wasConnected)
        {
            _wasConnected = true;
            ConnectionStateChanged?.Invoke(true);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using NetworkStream stream = client.GetStream();

                var (method, path, body) = await ReadHttpRequestAsync(stream, ct);
                if (string.IsNullOrEmpty(method))
                    return;

                if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 204, "No Content", string.Empty);
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/api/probe", StringComparison.OrdinalIgnoreCase))
                {
                    MarkExtensionSeen();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        ProbeReceived?.Invoke(body);
                    }
                    await WriteHttpResponseAsync(stream, 200, "OK", "{\"ok\":true}");
                    return;
                }

                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/api/command", StringComparison.OrdinalIgnoreCase))
                {
                    MarkExtensionSeen();
                    string appVersion = AppSettingsService.AppVersion.TrimStart('v', 'V');
                    BridgeCommand? validCmd = null;
                    DateTime nowUtc = DateTime.UtcNow;

                    while (_commandQueue.TryDequeue(out BridgeCommand? candidate))
                    {
                        if (_cancelledCommandIds.TryRemove(candidate.Id, out _))
                            continue;
                        if (nowUtc > candidate.ExpiresUtc)
                            continue;

                        validCmd = candidate;
                        break;
                    }

                    if (validCmd != null)
                    {
                        string json = JsonSerializer.Serialize(new { id = validCmd.Id, action = validCmd.Action, version = appVersion });
                        await WriteHttpResponseAsync(stream, 200, "OK", json);
                    }
                    else
                    {
                        string json = JsonSerializer.Serialize(new { id = (string?)null, action = "NONE", version = appVersion });
                        await WriteHttpResponseAsync(stream, 200, "OK", json);
                    }
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/api/command-result", StringComparison.OrdinalIgnoreCase))
                {
                    MarkExtensionSeen();
                    ProcessCommandResultBody(body);
                    await WriteHttpResponseAsync(stream, 200, "OK", "{\"ok\":true}");
                    return;
                }

                await WriteHttpResponseAsync(stream, 404, "Not Found", "{\"error\":\"not_found\"}");
            }
            catch
            {
                // Ignore malformed or aborted client connections
            }
        }
    }

    private void ProcessCommandResultBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            bool success = root.TryGetProperty("success", out var sucProp) && sucProp.ValueKind == JsonValueKind.True;
            bool needsLogin = root.TryGetProperty("needsLogin", out var nlProp) && nlProp.ValueKind == JsonValueKind.True;
            string message = root.TryGetProperty("message", out var msgProp) ? (msgProp.GetString() ?? string.Empty) : string.Empty;

            string? probeJson = null;
            if (root.TryGetProperty("probe", out var probeProp) && probeProp.ValueKind == JsonValueKind.Object)
            {
                probeJson = probeProp.GetRawText();
                ProbeReceived?.Invoke(probeJson);
            }

            if (!string.IsNullOrWhiteSpace(id) && _pendingResults.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(new BridgeCommandResult(success, needsLogin, message, probeJson));
            }
        }
        catch
        {
            // Ignore malformed command result
        }
    }

    private static async Task<(string Method, string Path, string Body)> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int headerEndIndex = -1;

        while (ms.Length < 65536)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                break;

            ms.Write(buffer, 0, read);
            byte[] current = ms.GetBuffer();
            int len = (int)ms.Length;
            headerEndIndex = FindHeaderTerminator(current, len);
            if (headerEndIndex >= 0)
                break;
        }

        if (headerEndIndex < 0)
            return (string.Empty, string.Empty, string.Empty);

        byte[] allBytes = ms.ToArray();
        string headerText = Encoding.ASCII.GetString(allBytes, 0, headerEndIndex);
        string[] lines = headerText.Split(["\r\n"], StringSplitOptions.None);
        if (lines.Length == 0)
            return (string.Empty, string.Empty, string.Empty);

        string[] reqParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (reqParts.Length < 2)
            return (string.Empty, string.Empty, string.Empty);

        string method = reqParts[0].Trim();
        string rawPath = reqParts[1].Trim();
        int qIdx = rawPath.IndexOf('?');
        string path = qIdx >= 0 ? rawPath[..qIdx] : rawPath;

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i][..colon].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(lines[i][(colon + 1)..].Trim(), out contentLength);
                break;
            }
        }

        if (contentLength <= 0)
            return (method, path, string.Empty);

        // Cap body size to 2MB for safety
        contentLength = Math.Min(contentLength, 2 * 1024 * 1024);

        int bodyStart = headerEndIndex + 4;
        int alreadyReadBody = Math.Max(0, allBytes.Length - bodyStart);
        byte[] bodyBytes = new byte[contentLength];
        int copied = Math.Min(alreadyReadBody, contentLength);
        if (copied > 0)
        {
            Buffer.BlockCopy(allBytes, bodyStart, bodyBytes, 0, copied);
        }

        int offset = copied;
        while (offset < contentLength)
        {
            int read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct);
            if (read <= 0)
                break;
            offset += read;
        }

        string body = Utf8NoBom.GetString(bodyBytes, 0, offset);
        return (method, path, body);
    }

    private static int FindHeaderTerminator(byte[] buf, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, int statusCode, string reasonPhrase, string jsonBody)
    {
        byte[] bodyBytes = string.IsNullOrEmpty(jsonBody) ? [] : Utf8NoBom.GetBytes(jsonBody);
        string headers =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type, Access-Control-Request-Private-Network\r\n" +
            "Access-Control-Expose-Headers: X-WorkDot-Bridge\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "X-WorkDot-Bridge: 1\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        foreach (var kv in _pendingResults)
        {
            if (_pendingResults.TryRemove(kv.Key, out var tcs))
            {
                tcs.TrySetResult(new BridgeCommandResult(false, false, "브릿지 서버가 종료되었습니다."));
            }
        }

        try { _cts?.Dispose(); } catch { }
        _listener = null;
        _cts = null;
    }
}
