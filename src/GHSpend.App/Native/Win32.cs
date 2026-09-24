using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GHSpend.App.Native;

internal static unsafe partial class Win32
{
    internal const uint WM_DESTROY = 2, WM_SIZE = 5, WM_CLOSE = 0x10, WM_PAINT = 0xF,
        WM_COMMAND = 0x111, WM_TIMER = 0x113, WM_DPICHANGED = 0x2E0,
        WM_POWERBROADCAST = 0x218, WM_APP_WORK = 0x8001, WM_TRAY = 0x8002;
    internal const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x10000,
        WS_BORDER = 0x800000, WS_VSCROLL = 0x200000, WS_OVERLAPPEDWINDOW = 0xCF0000;
    internal const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    internal const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10,
        NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    internal const uint NIIF_INFO = 1, NIIF_RESPECT_QUIET_TIME = 0x80;
    internal const uint MB_OK = 0, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_YESNO = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEX
    {
        internal uint cbSize, style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> lpfnWndProc;
        internal int cbClsExtra, cbWndExtra;
        internal nint hInstance, hIcon, hCursor, hbrBackground;
        internal char* lpszMenuName;
        internal char* lpszClassName;
        internal nint hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { internal int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { internal int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NOTIFYICONIDENTIFIER
    {
        internal uint cbSize;
        internal nint hWnd;
        internal uint uID;
        internal Guid guidItem;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        internal uint cbSize;
        internal RECT monitor, work;
        internal uint flags;
    }
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    internal static partial int ShellNotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromRect(ref RECT rect, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    internal static partial int GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal nint hwnd;
        internal uint message;
        internal nuint wParam;
        internal nint lParam;
        internal uint time;
        internal POINT pt;
        internal uint lPrivate;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        internal nint hdc;
        internal int fErase;
        internal RECT rcPaint;
        internal int fRestore, fIncUpdate;
        internal fixed byte rgbReserved[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct INITCOMMONCONTROLSEX { internal uint dwSize, dwICC; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOGFONT
    {
        internal int height, width, escapement, orientation, weight;
        internal byte italic, underline, strikeout, charSet, outPrecision, clipPrecision, quality, pitchAndFamily;
        internal fixed char faceName[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct NONCLIENTMETRICS
    {
        internal uint cbSize;
        internal int borderWidth, scrollWidth, scrollHeight, captionWidth, captionHeight;
        internal LOGFONT captionFont;
        internal int smallCaptionWidth, smallCaptionHeight;
        internal LOGFONT smallCaptionFont;
        internal int menuWidth, menuHeight;
        internal LOGFONT menuFont, statusFont, messageFont;
        internal int paddedBorderWidth;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        internal uint cbSize;
        internal nint hWnd;
        internal uint uID, uFlags, uCallbackMessage;
        internal nint hIcon;
        internal fixed char szTip[128];
        internal uint dwState, dwStateMask;
        internal fixed char szInfo[256];
        internal uint uTimeoutOrVersion;
        internal fixed char szInfoTitle[64];
        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHELLEXECUTEINFO
    {
        internal uint cbSize, fMask;
        internal nint hwnd;
        internal char* lpVerb;
        internal char* lpFile;
        internal char* lpParameters;
        internal char* lpDirectory;
        internal int nShow;
        internal nint hInstApp, lpIDList;
        internal char* lpClass;
        internal nint hkeyClass;
        internal uint dwHotKey;
        internal nint hIconOrMonitor, hProcess;
    }
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);
    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();
    [LibraryImport("user32.dll")]
    internal static partial uint GetGuiResources(nint process, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(ref WNDCLASSEX value);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    internal static partial int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")]
    internal static partial int TranslateMessage(ref MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(ref MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    internal static partial int IsDialogMessage(nint hwnd, ref MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    internal static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SendText(nint hwnd, uint message, nuint wParam, string text);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowText(nint hwnd, string text);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    internal static partial int GetWindowText(nint hwnd, char* text, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    internal static partial int GetWindowTextLength(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial nint GetDlgItem(nint hwnd, int id);
    [LibraryImport("user32.dll")]
    internal static partial int ShowWindow(nint hwnd, int show);
    [LibraryImport("user32.dll")]
    internal static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial int EnableWindow(nint hwnd, int enable);
    [LibraryImport("user32.dll")]
    internal static partial int MoveWindow(nint hwnd, int x, int y, int width, int height, int repaint);
    [LibraryImport("user32.dll")]
    internal static partial int GetClientRect(nint hwnd, out RECT rect);
    [LibraryImport("user32.dll")]
    internal static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    internal static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursor(nint instance, nint name);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    internal static partial nint LoadImage(nint instance, nint name, uint type, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    internal static partial int DestroyIcon(nint icon);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string message);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBox(nint owner, string text, string caption, uint type);
    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [LibraryImport("user32.dll")]
    internal static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int AppendMenu(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [LibraryImport("user32.dll")]
    internal static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")]
    internal static partial int GetCursorPos(out POINT point);
    [LibraryImport("comctl32.dll")]
    internal static partial int InitCommonControlsEx(ref INITCOMMONCONTROLSEX value);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    internal static partial int ShellNotifyIcon(uint message, ref NOTIFYICONDATA value);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    internal static partial int ShellExecuteEx(ref SHELLEXECUTEINFO value);
    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);
    [LibraryImport("user32.dll")]
    internal static partial int EndPaint(nint hwnd, ref PAINTSTRUCT paint);
    [LibraryImport("user32.dll")]
    internal static partial int InvalidateRect(nint hwnd, nint rect, int erase);
    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int index);
    [LibraryImport("user32.dll")]
    internal static partial nint GetSysColorBrush(int index);
    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint dc, ref RECT rect, nint brush);
    [LibraryImport("gdi32.dll")]
    internal static partial nint GetStockObject(int id);
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoForDpi", SetLastError = true)]
    internal static partial int SystemParametersInfoForDpi(uint action, uint parameter, ref NONCLIENTMETRICS metrics, uint flags, uint dpi);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontIndirectW")]
    internal static partial nint CreateFontIndirect(ref LOGFONT font);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFont(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision,
        uint clipPrecision, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleBitmap(nint dc, int width, int height);
    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint dc, nint item);
    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteObject(nint item);
    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")]
    internal static partial int BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")]
    internal static partial int MoveToEx(nint dc, int x, int y, nint previous);
    [LibraryImport("gdi32.dll")]
    internal static partial int LineTo(nint dc, int x, int y);
    [LibraryImport("user32.dll")]
    internal static partial int OpenClipboard(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial int EmptyClipboard();
    [LibraryImport("user32.dll")]
    internal static partial nint SetClipboardData(uint format, nint handle);
    [LibraryImport("user32.dll")]
    internal static partial int CloseClipboard();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint size);
    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalLock(nint handle);
    [LibraryImport("kernel32.dll")]
    internal static partial int GlobalUnlock(nint handle);
    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalFree(nint handle);
}
