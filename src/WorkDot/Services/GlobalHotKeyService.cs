using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WorkDot.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 9182;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private bool _isRegistered;
    private Action? _onHotKeyTriggered;
    private string _currentHotKeyGesture = string.Empty;

    public bool IsRegistered => _isRegistered;
    public string CurrentHotKeyGesture => _currentHotKeyGesture;

    public void Initialize(Window window, Action onHotKeyTriggered)
    {
        _onHotKeyTriggered = onHotKeyTriggered;

        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(HwndHook);
    }

    public bool Register(string hotKeyGesture)
    {
        Unregister();

        if (string.IsNullOrWhiteSpace(hotKeyGesture) || string.Equals(hotKeyGesture, "None", StringComparison.OrdinalIgnoreCase))
        {
            _currentHotKeyGesture = string.Empty;
            return true;
        }

        if (_hwnd == IntPtr.Zero)
            return false;

        if (!TryParseHotKey(hotKeyGesture, out uint modifiers, out uint vk))
            return false;

        bool success = RegisterHotKey(_hwnd, HotKeyId, modifiers | MOD_NOREPEAT, vk);
        if (success)
        {
            _isRegistered = true;
            _currentHotKeyGesture = hotKeyGesture;
        }
        return success;
    }

    public void Unregister()
    {
        if (_isRegistered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotKeyId);
            _isRegistered = false;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            _onHotKeyTriggered?.Invoke();
        }
        return IntPtr.Zero;
    }

    public static bool TryParseHotKey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        Key targetKey = Key.None;

        foreach (var rawToken in tokens)
        {
            string token = rawToken.Trim().ToLowerInvariant();
            if (token is "ctrl" or "control")
            {
                modifiers |= MOD_CONTROL;
            }
            else if (token is "alt")
            {
                modifiers |= MOD_ALT;
            }
            else if (token is "shift")
            {
                modifiers |= MOD_SHIFT;
            }
            else if (token is "win" or "windows")
            {
                modifiers |= MOD_WIN;
            }
            else
            {
                if (Enum.TryParse<Key>(rawToken, true, out var parsedKey) && parsedKey != Key.None)
                {
                    targetKey = parsedKey;
                }
                else if (rawToken.Length == 1 && char.IsLetterOrDigit(rawToken[0]))
                {
                    char c = char.ToUpperInvariant(rawToken[0]);
                    if (c >= 'A' && c <= 'Z')
                    {
                        targetKey = Key.A + (c - 'A');
                    }
                    else if (c >= '0' && c <= '9')
                    {
                        targetKey = Key.D0 + (c - '0');
                    }
                }
            }
        }

        if (targetKey == Key.None)
            return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(targetKey);
        return vk != 0;
    }

    public static string NormalizeGestureString(ModifierKeys modifiers, Key key)
    {
        if (key == Key.None || key == Key.System ||
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

        string keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.F1 and <= Key.F24 => key.ToString(),
            Key.OemTilde => "`",
            Key.Space => "Space",
            _ => key.ToString()
        };

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Unregister();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource = null;
        }
    }
}
