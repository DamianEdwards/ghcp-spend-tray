using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GHSpend.App.Native;

internal sealed unsafe class Sparkline : IDisposable
{
    private static readonly Dictionary<nint, Sparkline> Instances = [];
    private static bool _registered;
    internal nint Handle { get; }
    private IReadOnlyList<GraphPoint> _points = [];
    internal Sparkline(nint parent)
    {
        if (!_registered)
        {
            fixed (char* name = "GHSpend.Sparkline")
            {
                var cls = new Win32.WNDCLASSEX
                {
                    cbSize = (uint)sizeof(Win32.WNDCLASSEX), lpfnWndProc = &Procedure,
                    hInstance = Win32.GetModuleHandle(null), lpszClassName = name
                };
                if (Win32.RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("Cannot register the history graph.");
            }
            _registered = true;
        }
        Handle = Win32.CreateWindowEx(0, "GHSpend.Sparkline", "Observed consumption rate in USD per hour; textual equivalent is above",
            Win32.WS_CHILD | Win32.WS_VISIBLE, 0, 0, 10, 10, parent, 0, Win32.GetModuleHandle(null), 0);
        if (Handle == 0) throw new InvalidOperationException("Cannot create the history graph.");
        Instances.Add(Handle, this);
    }
    internal void SetPoints(IReadOnlyList<GraphPoint> points)
    {
        _points = points;
        Win32.InvalidateRect(Handle, 0, 0);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == Win32.WM_PAINT && Instances.TryGetValue(hwnd, out var graph)) { graph.Paint(); return 0; }
            if (message == 0x14) return 1;
        }
        catch (Exception ex) { Diagnostics.Record($"Graph painting failed ({ex.GetType().Name})."); }
        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }
    private void Paint()
    {
        var dc = Win32.BeginPaint(Handle, out var paint);
        nint memory = 0, bitmap = 0, oldBitmap = 0, pen = 0, oldPen = 0;
        try
        {
            Win32.GetClientRect(Handle, out var rect);
            if (rect.right < 1 || rect.bottom < 1) return;
            memory = Win32.CreateCompatibleDC(dc);
            bitmap = Win32.CreateCompatibleBitmap(dc, rect.right, rect.bottom);
            if (memory == 0 || bitmap == 0) throw new InvalidOperationException("Cannot allocate the history graph.");
            oldBitmap = Win32.SelectObject(memory, bitmap);
            Win32.FillRect(memory, ref rect, Win32.GetSysColorBrush(5));
            pen = Win32.CreatePen(0, Math.Max(2, (int)Win32.GetDpiForWindow(Handle) / 48), Win32.GetSysColor(13));
            oldPen = Win32.SelectObject(memory, pen);
            if (_points.Count > 0)
            {
                var max = Math.Max(.01m, _points.Where(p => p.Rate.HasValue).Select(p => p.Rate!.Value).DefaultIfEmpty().Max());
                var start = DateTimeOffset.UtcNow.AddHours(-24);
                var end = DateTimeOffset.UtcNow;
                bool connected = false;
                foreach (var point in _points)
                {
                    if (point.Rate is null || point.Time < start) { connected = false; continue; }
                    var x = 8 + (int)((point.Time - start).TotalSeconds / (end - start).TotalSeconds * Math.Max(1, rect.right - 16));
                    var y = rect.bottom - 8 - (int)(point.Rate.Value / max * Math.Max(1, rect.bottom - 16));
                    if (connected) Win32.LineTo(memory, x, y);
                    else
                    {
                        Win32.MoveToEx(memory, Math.Max(0, x - 2), y, 0);
                        Win32.LineTo(memory, x + 2, y);
                    }
                    connected = true;
                }
            }
            Win32.BitBlt(dc, 0, 0, rect.right, rect.bottom, memory, 0, 0, 0xCC0020);
        }
        finally
        {
            if (oldPen != 0) Win32.SelectObject(memory, oldPen);
            if (pen != 0) Win32.DeleteObject(pen);
            if (oldBitmap != 0) Win32.SelectObject(memory, oldBitmap);
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            if (memory != 0) Win32.DeleteDC(memory);
            Win32.EndPaint(Handle, ref paint);
        }
    }
    public void Dispose()
    {
        Instances.Remove(Handle);
        Win32.DestroyWindow(Handle);
    }
}
