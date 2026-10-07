using System.Runtime.InteropServices;
using Projecao.Models;

namespace Projecao.Desktop;

/// <summary>
/// Interop mínimo com Win32 (user32/kernel32) para o equivalente Windows do <see cref="Gtk"/>.
/// Ao contrário do Linux, o Photino expõe <c>PhotinoWindow.WindowHandle</c> (HWND) no Windows,
/// por isso não é preciso procurar a janela pelo título.
/// </summary>
internal static class Win32
{
    /// <summary>Move/redimensiona a janela para cobrir o monitor que contém o ponto (x, y).</summary>
    public static bool FullscreenOnMonitorAt(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var point = new POINT { X = x, Y = y };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var r = info.rcMonitor;
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);
        return true;
    }

    /// <summary>Lista os monitores ligados. <paramref name="panelHwnd"/> (opcional) marca o do painel.</summary>
    public static IReadOnlyList<MonitorInfo> QueryMonitors(IntPtr panelHwnd)
    {
        var panelMonitor = panelHwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(panelHwnd, MONITOR_DEFAULTTONEAREST);
        var result = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref info))
                return true;

            var r = info.rcMonitor;
            var isPanel = panelMonitor != IntPtr.Zero ? hMonitor == panelMonitor : (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            var device = info.szDevice.TrimEnd('\0');

            var refreshHz = 60;
            if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref _devMode) && _devMode.dmDisplayFrequency > 1)
                refreshHz = (int)_devMode.dmDisplayFrequency;

            result.Add(new MonitorInfo(
                Id: $"{device}@{r.Left},{r.Top}",
                DeviceName: device,
                FriendlyName: string.Empty,
                X: r.Left,
                Y: r.Top,
                Width: r.Right - r.Left,
                Height: r.Bottom - r.Top,
                RefreshRate: refreshHz,
                IsPrimary: isPanel));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return result;
    }

    // DEVMODE é grande; mantemos uma instância estática só para reutilizar a struct entre chamadas.
    private static DEVMODE _devMode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITORINFOF_PRIMARY = 1;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
