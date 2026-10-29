using System.Runtime.InteropServices;
using GHCPSpendTray.App.Platform;

namespace GHCPSpendTray.App.Native;

internal sealed unsafe partial class NativeMenuTheme : IDisposable
{
    internal enum AppMode { Default, AllowDark, ForceDark, ForceLight }

    private static readonly delegate* unmanaged[Stdcall]<AppMode, AppMode> SetPreferredAppMode;
    private static readonly delegate* unmanaged[Stdcall]<nint, byte, byte> AllowDarkModeForWindow;
    private static readonly delegate* unmanaged[Stdcall]<void> FlushMenuThemes;
    private readonly nint _owner;
    private readonly AppMode _previousMode;
    private bool _disposed;

    static NativeMenuTheme()
    {
        // Win32 popup dark mode is exposed only by ordinal. Resolve all exports before
        // opting in, and retain the System32 module for the lifetime of these pointers.
        var module = NativeLibrary.Load("uxtheme.dll", typeof(NativeMenuTheme).Assembly, DllImportSearchPath.System32);
        SetPreferredAppMode = (delegate* unmanaged[Stdcall]<AppMode, AppMode>)GetProcAddress(module, 135);
        AllowDarkModeForWindow = (delegate* unmanaged[Stdcall]<nint, byte, byte>)GetProcAddress(module, 133);
        FlushMenuThemes = (delegate* unmanaged[Stdcall]<void>)GetProcAddress(module, 136);
        if (SetPreferredAppMode == null || AllowDarkModeForWindow == null || FlushMenuThemes == null)
            Diagnostics.Record("Native menu theme APIs are unavailable; Windows will use its default menu appearance.");
    }

    private NativeMenuTheme(nint owner, AppMode mode)
    {
        _owner = owner;
        _previousMode = SetPreferredAppMode(mode);
        AllowDarkModeForWindow(owner, mode == AppMode.ForceDark ? (byte)1 : (byte)0);
        FlushMenuThemes();
    }

    internal static NativeMenuTheme? Apply(nint owner) =>
        Apply(owner, TrayAppearance.IsLightTheme(), Win32.IsHighContrast());

    internal static NativeMenuTheme? Apply(nint owner, bool lightTheme, bool highContrast)
    {
        if (SetPreferredAppMode == null || AllowDarkModeForWindow == null || FlushMenuThemes == null)
            return null;
        return new(owner, Mode(lightTheme, highContrast));
    }

    internal static AppMode Mode(bool lightTheme, bool highContrast) =>
        highContrast ? AppMode.Default : lightTheme ? AppMode.ForceLight : AppMode.ForceDark;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AllowDarkModeForWindow(_owner, 0);
        // Do not force the taskbar's theme on other native menus in the WinUI app.
        SetPreferredAppMode(_previousMode);
        FlushMenuThemes();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    private static partial nint GetProcAddress(nint module, nint ordinal);
}
