using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GHSpend.App.Native;

internal abstract unsafe class NativeWindow : IDisposable
{
    private static readonly Dictionary<nint, NativeWindow> Windows = [];
    private static bool _registered;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly List<nint> _controls = [];
    private nint _font;
    private bool _disposed;
    private bool _shown;
    private readonly int _minimumWidth, _minimumHeight;
    internal nint Handle { get; private set; }
    internal event Action<NativeWindow>? Closed;
    protected int Dpi => (int)Win32.GetDpiForWindow(Handle);
    protected int Scale(int value) => value * Dpi / 96;
    protected int DefaultCommand { get; set; }

    protected NativeWindow(string title, int width, int height, nint owner = 0)
    {
        _minimumWidth = width;
        _minimumHeight = height;
        if (!_registered)
        {
            fixed (char* name = "GHSpend.NativeWindow")
            {
                var cls = new Win32.WNDCLASSEX
                {
                    cbSize = (uint)sizeof(Win32.WNDCLASSEX),
                    lpfnWndProc = &WindowProc,
                    hInstance = Win32.GetModuleHandle(null),
                    hCursor = Win32.LoadCursor(0, 32512),
                    hbrBackground = 16,
                    lpszClassName = name,
                    hIcon = Win32.LoadImage(Win32.GetModuleHandle(null), 32512, 1, 0, 0, 0x8000)
                };
                if (Win32.RegisterClassEx(ref cls) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot register the GHSpend window.");
            }
            _registered = true;
        }
        Handle = Win32.CreateWindowEx(0x10000, "GHSpend.NativeWindow", title, Win32.WS_OVERLAPPEDWINDOW,
            unchecked((int)0x80000000), unchecked((int)0x80000000), width, height, owner, 0,
            Win32.GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create a native window.");
        Windows.Add(Handle, this);
        UpdateFont();
    }

    protected nint Control(string className, string text, int id, uint extraStyle = 0)
    {
        var hwnd = Win32.CreateWindowEx(className == "EDIT" ? 0x200u : 0, className, text,
            Win32.WS_CHILD | Win32.WS_VISIBLE | extraStyle, 0, 0, 10, 10, Handle, id,
            Win32.GetModuleHandle(null), 0);
        if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Cannot create {className} control.");
        _controls.Add(hwnd);
        Win32.SendMessage(hwnd, 0x30, (nuint)_font, 1);
        return hwnd;
    }
    protected nint Label(string text, int id = 0) => Control("STATIC", text, id);
    protected nint Button(string text, int id) => Control("BUTTON", text, id, Win32.WS_TABSTOP);
    protected nint Edit(string text, int id) => Control("EDIT", text, id, Win32.WS_TABSTOP | 0x80);
    protected void Place(nint control, int x, int y, int width, int height) =>
        Win32.MoveWindow(control, Scale(x), Scale(y), Math.Max(1, Scale(width)), Math.Max(1, Scale(height)), 1);
    protected (int Width, int Height) ClientSize()
    {
        Win32.GetClientRect(Handle, out var rect);
        return (rect.right * 96 / Math.Max(96, Dpi), rect.bottom * 96 / Math.Max(96, Dpi));
    }
    internal static string Text(nint control)
    {
        var length = Win32.GetWindowTextLength(control);
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
            Win32.GetWindowText(control, p, buffer.Length);
        return new string(buffer, 0, length);
    }
    internal void Show()
    {
        if (!_shown)
        {
            Win32.SetWindowPos(Handle, 0, 0, 0, Scale(_minimumWidth), Scale(_minimumHeight), 0x6);
            _shown = true;
        }
        Win32.ShowWindow(Handle, 9);
        Win32.SetForegroundWindow(Handle);
        Layout();
    }
    internal void Hide() => Win32.ShowWindow(Handle, 0);
    internal void Post(Action action)
    {
        if (_disposed) return;
        _work.Enqueue(action);
        if (Win32.PostMessage(Handle, Win32.WM_APP_WORK, 0, 0) == 0 && !_disposed)
            Diagnostics.Record("UI dispatch failed.");
    }
    internal void Error(string message) => Win32.MessageBox(Handle, message, "GHSpend", Win32.MB_ICONERROR);
    protected void RunOperation(Func<Task> operation, Action? success = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
                if (success is not null) Post(success);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Diagnostics.Record($"Operation failed ({ex.GetType().Name}).");
                var message = ex is AppOperationException ? ex.Message :
                    "The operation failed. Check connectivity, account access, and data-folder permissions. See the diagnostic log for the error category.";
                Post(() => Error(message));
            }
        });
    }
    protected abstract void Layout();
    protected virtual void Command(int id, int notification) { }
    protected virtual void Closing() => Hide();
    protected virtual nint? Message(uint message, nuint wParam, nint lParam) => null;
    protected virtual void ReleaseResources() { }
    private void UpdateFont()
    {
        var metrics = new Win32.NONCLIENTMETRICS { cbSize = (uint)sizeof(Win32.NONCLIENTMETRICS) };
        if (Win32.SystemParametersInfoForDpi(0x29, metrics.cbSize, ref metrics, 0, (uint)Dpi) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the system UI font.");
        var font = Win32.CreateFontIndirect(ref metrics.messageFont);
        if (font == 0) throw new Win32Exception("Cannot create the UI font.");
        foreach (var control in _controls) Win32.SendMessage(control, 0x30, (nuint)font, 1);
        if (_font != 0) Win32.DeleteObject(_font);
        _font = font;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window))
            {
                if (message == Win32.WM_APP_WORK)
                {
                    while (window._work.TryDequeue(out var work)) work();
                    return 0;
                }
                if (message == Win32.WM_COMMAND)
                {
                    var id = (int)(wParam & 0xFFFF);
                    if (lParam == 0 && id == 2) window.Closing();
                    else window.Command(lParam == 0 && id == 1 && window.DefaultCommand != 0 ?
                        window.DefaultCommand : id, (int)((wParam >> 16) & 0xFFFF));
                    return 0;
                }
                if (message == Win32.WM_CLOSE) { window.Closing(); return 0; }
                if (message == 0x24)
                {
                    var points = (Win32.POINT*)lParam;
                    points[3].x = window.Scale(window._minimumWidth);
                    points[3].y = window.Scale(window._minimumHeight);
                    return 0;
                }
                if (message == Win32.WM_SIZE) { window.Layout(); return 0; }
                if (message is 0x1A or 0x31A)
                {
                    window.UpdateFont();
                    window.Layout();
                    Win32.InvalidateRect(hwnd, 0, 1);
                }
                if (message == Win32.WM_DPICHANGED)
                {
                    var rect = *(Win32.RECT*)lParam;
                    Win32.SetWindowPos(hwnd, 0, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, 0x14);
                    window.UpdateFont();
                    window.Layout();
                    return 0;
                }
                var result = window.Message(message, wParam, lParam);
                if (result.HasValue) return result.Value;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Native callback failure ({ex.GetType().Name}).");
            Win32.MessageBox(hwnd, "An operation failed. Check the diagnostic log; no credentials are logged.",
                "GHSpend", Win32.MB_ICONERROR);
        }
        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }
    internal static int RunLoop()
    {
        int result;
        while ((result = Win32.GetMessage(out var message, 0, 0, 0)) > 0)
        {
            bool handled = false;
            foreach (var window in Windows.Values.ToArray())
            {
                if (Win32.IsDialogMessage(window.Handle, ref message) != 0) { handled = true; break; }
            }
            if (!handled)
            {
                Win32.TranslateMessage(ref message);
                Win32.DispatchMessage(ref message);
            }
        }
        if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Native message loop failed.");
        return 0;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
        Windows.Remove(Handle);
        Win32.DestroyWindow(Handle);
        if (_font != 0) Win32.DeleteObject(_font);
        Handle = 0;
        Closed?.Invoke(this);
        GC.SuppressFinalize(this);
    }
}
