using System.Runtime.InteropServices;

namespace AirGrab.Core;

/// <summary>
/// 系统通知区域（托盘）图标。
///
/// WinUI 3 没有托盘图标 API，只能走 Win32 的 <c>Shell_NotifyIcon</c>。
/// 这里用 <c>SetWindowSubclass</c> 给主窗口挂一层子类过程来接收托盘回调消息 —— 
/// 相比 <c>SetWindowLongPtr</c> 直接替换 WndProc，子类化不会破坏 WinUI 内部的消息处理，
/// 出问题时也能干净地 <c>RemoveWindowSubclass</c> 撤回。
///
/// 图标取系统默认应用程序图标，因此不需要随包分发 .ico 资源。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int IDM_SHOW = 1;
    private const int IDM_QUIT = 2;

    private const int IDI_APPLICATION = 32512;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private readonly IntPtr _hwnd;
    private readonly uint _iconId;
    private readonly SUBCLASSPROC _subclassProc;
    private readonly IntPtr _hIcon;

    private bool _installed;
    private bool _disposed;

    /// <summary>用户选择「打开主窗口」。</summary>
    public event Action? ShowRequested;

    /// <summary>用户选择「退出 AirGrab」—— 这是真正退出的唯一入口。</summary>
    public event Action? QuitRequested;

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _iconId = 1;
        // 委托必须由字段持有，否则会被 GC 回收，回调时直接崩。
        _subclassProc = OnSubclassMessage;
        // 图标只取一次并缓存：每次 Shell_NotifyIcon 都重新 LoadIcon 会持续泄漏句柄。
        _hIcon = LoadAppIcon();
    }

    /// <summary>
    /// 取 exe 自身嵌入的应用图标，与任务栏 / 开始菜单保持一致。
    /// 取不到时退回系统默认图标，避免托盘出现空白项。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        string? processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(processPath))
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];

            if (ExtractIconExW(processPath, 0, large, small, 1) > 0)
            {
                if (small[0] != IntPtr.Zero)
                {
                    if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                    return small[0];
                }

                if (large[0] != IntPtr.Zero) return large[0];
            }
        }

        return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    public void Install(string tooltip)
    {
        if (_installed) return;

        _installed = SetWindowSubclass(_hwnd, _subclassProc, (UIntPtr)_iconId, UIntPtr.Zero);

        var data = CreateData(tooltip, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_installed) return;
        var data = CreateData(tooltip, NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATAW CreateData(string tooltip, uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = _iconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _hIcon,
        szTip = tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr OnSubclassMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (msg == WM_TRAYICON)
        {
            int notification = lParam.ToInt32();

            switch (notification)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Restore();
                    return IntPtr.Zero;

                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_SHOW, "打开主窗口");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenuW(menu, MF_STRING, (UIntPtr)IDM_QUIT, "退出 AirGrab");

            GetCursorPos(out POINT point);

            // 必须先把窗口提到前台，否则点击菜单外部时菜单不会消失。
            SetForegroundWindow(_hwnd);

            int command = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            switch (command)
            {
                case IDM_SHOW:
                    Restore();
                    break;

                case IDM_QUIT:
                    QuitRequested?.Invoke();
                    break;
            }

            // 菜单关闭后把这条消息补发一次，修复托盘菜单偶发不消失的问题。
            PostMessageW(_hwnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>从托盘恢复主窗口。</summary>
    public void Restore()
    {
        ShowWindow(_hwnd, SW_SHOW);
        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
        ShowRequested?.Invoke();
    }

    /// <summary>隐藏主窗口到托盘，进程继续在后台运行。</summary>
    public void HideToTray() => ShowWindow(_hwnd, SW_HIDE);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_installed)
        {
            var data = CreateData(string.Empty, 0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            RemoveWindowSubclass(_hwnd, _subclassProc, (UIntPtr)_iconId);
            _installed = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
        }
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
