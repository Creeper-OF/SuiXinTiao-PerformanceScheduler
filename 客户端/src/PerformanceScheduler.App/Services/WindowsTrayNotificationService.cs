using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.Core.Abstractions;

namespace PerformanceScheduler.App.Services;

public sealed class WindowsTrayNotificationService : IWindowsTrayNotificationService
{
    private const int CallbackMessageId = 0x8001; // WM_APP + 1
    private const uint TrayIconId = 1001;

    // Win32 Message constants
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    // Shell_NotifyIcon messages
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    // NOTIFYICONDATA flags
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    // NOTIFYICONDATA info flags (balloons)
    private const uint NIIF_NONE = 0x00000000;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;

    // Menu constants
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_ENABLED = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_LEFTBUTTON = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;

    // Menu IDs
    private const int MenuIdShow = 1;
    private const int MenuIdToggleScheduling = 2;
    private const int MenuIdEmergencyRollback = 3;
    private const int MenuIdExit = 4;

    private readonly Action _showMainWindowAction;
    private readonly Func<bool> _isSchedulingActiveFunc;
    private readonly Action _toggleSchedulingAction;
    private readonly Action _emergencyRollbackAction;
    private readonly Action _exitAction;
    private readonly JsonLocalizationService? _localizationService;
    private readonly IAppLogger? _logger;

    private HwndSource? _messageSink;
    private IntPtr _hwnd;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _isDisposed;
    private bool _isCreated;
    private readonly object _syncLock = new();

    public bool IsCreated => _isCreated;

    public WindowsTrayNotificationService(
        Action showMainWindowAction,
        Func<bool> isSchedulingActiveFunc,
        Action toggleSchedulingAction,
        Action emergencyRollbackAction,
        Action exitAction,
        JsonLocalizationService? localizationService = null,
        IAppLogger? logger = null)
    {
        _showMainWindowAction = showMainWindowAction ?? throw new ArgumentNullException(nameof(showMainWindowAction));
        _isSchedulingActiveFunc = isSchedulingActiveFunc ?? throw new ArgumentNullException(nameof(isSchedulingActiveFunc));
        _toggleSchedulingAction = toggleSchedulingAction ?? throw new ArgumentNullException(nameof(toggleSchedulingAction));
        _emergencyRollbackAction = emergencyRollbackAction ?? throw new ArgumentNullException(nameof(emergencyRollbackAction));
        _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));
        _localizationService = localizationService;
        _logger = logger;
    }

    public void Initialize()
    {
        lock (_syncLock)
        {
            if (_isCreated || _isDisposed)
            {
                return;
            }

            try
            {
                var parameters = new HwndSourceParameters("PerformanceScheduler_TraySink")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0x800000 // WS_BORDER (message sink)
                };

                _messageSink = new HwndSource(parameters);
                _messageSink.AddHook(HwndHook);
                _hwnd = _messageSink.Handle;

                _hIcon = LoadApplicationIcon();

                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                nid.uCallbackMessage = CallbackMessageId;
                nid.hIcon = _hIcon;
                nid.szTip = GetDefaultTooltip();

                if (!Shell_NotifyIconW(NIM_ADD, ref nid))
                {
                    _logger?.Warn("Failed to add tray icon via Shell_NotifyIconW.");
                    return;
                }

                _isCreated = true;
                _logger?.Info("Windows system tray icon initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger?.Error("Exception occurred during tray icon initialization.", ex);
            }
        }
    }

    public void ShowNotification(string title, string message, NotificationIconType type = NotificationIconType.Info)
    {
        lock (_syncLock)
        {
            if (!_isCreated || _isDisposed)
            {
                return;
            }

            try
            {
                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_INFO;
                nid.szInfoTitle = title?.Length > 63 ? title[..63] : (title ?? string.Empty);
                nid.szInfo = message?.Length > 255 ? message[..255] : (message ?? string.Empty);
                nid.dwInfoFlags = type switch
                {
                    NotificationIconType.Warning => NIIF_WARNING,
                    NotificationIconType.Error => NIIF_ERROR,
                    NotificationIconType.Info => NIIF_INFO,
                    _ => NIIF_NONE
                };

                Shell_NotifyIconW(NIM_MODIFY, ref nid);
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to display tray balloon notification.", ex);
            }
        }
    }

    public void UpdateTooltip(string tooltipText)
    {
        lock (_syncLock)
        {
            if (!_isCreated || _isDisposed)
            {
                return;
            }

            try
            {
                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_TIP;
                nid.szTip = tooltipText?.Length > 127 ? tooltipText[..127] : (tooltipText ?? string.Empty);
                Shell_NotifyIconW(NIM_MODIFY, ref nid);
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to update tray icon tooltip.", ex);
            }
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessageId)
        {
            var eventCode = lParam.ToInt32();
            switch (eventCode)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _showMainWindowAction();
                    handled = true;
                    break;

                case WM_RBUTTONUP:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        try
        {
            var hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var isSchedulingActive = _isSchedulingActiveFunc();
                var showText = GetText("Tray.ShowMainWindow", "显示主窗口");
                var pauseText = isSchedulingActive
                    ? GetText("Tray.PauseScheduling", "暂停调度")
                    : GetText("Tray.ResumeScheduling", "恢复调度");
                var rollbackText = GetText("Tray.EmergencyRollback", "一键紧急回滚");
                var exitText = GetText("Tray.Exit", "退出随芯调");

                AppendMenuW(hMenu, MF_STRING, (UIntPtr)MenuIdShow, showText);
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)MenuIdToggleScheduling, pauseText);
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)MenuIdEmergencyRollback, rollbackText);
                AppendMenuW(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
                AppendMenuW(hMenu, MF_STRING, (UIntPtr)MenuIdExit, exitText);

                GetCursorPos(out var pt);
                SetForegroundWindow(_hwnd);

                var cmd = TrackPopupMenuEx(
                    hMenu,
                    TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_LEFTBUTTON,
                    pt.X,
                    pt.Y,
                    _hwnd,
                    IntPtr.Zero);

                PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero); // Fix focus release

                switch (cmd)
                {
                    case MenuIdShow:
                        _showMainWindowAction();
                        break;
                    case MenuIdToggleScheduling:
                        _toggleSchedulingAction();
                        break;
                    case MenuIdEmergencyRollback:
                        _emergencyRollbackAction();
                        break;
                    case MenuIdExit:
                        _exitAction();
                        break;
                }
            }
            finally
            {
                DestroyMenu(hMenu);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to display tray context menu.", ex);
        }
    }

    private string GetDefaultTooltip()
    {
        return GetText("App.Title", "随芯调");
    }

    private string GetText(string key, string fallback)
    {
        try
        {
            if (_localizationService is not null)
            {
                var val = _localizationService.Get(key);
                if (!string.IsNullOrWhiteSpace(val) && val != key)
                {
                    return val;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private NOTIFYICONDATA CreateNotifyIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                using var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon.Handle;
                }
            }
        }
        catch
        {
        }

        // Fallback to standard application icon
        return LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_isCreated)
            {
                try
                {
                    var nid = CreateNotifyIconData();
                    Shell_NotifyIconW(NIM_DELETE, ref nid);
                }
                catch (Exception ex)
                {
                    _logger?.Error("Error removing tray icon during disposal.", ex);
                }

                _isCreated = false;
            }

            if (_messageSink is not null)
            {
                try
                {
                    _messageSink.RemoveHook(HwndHook);
                    _messageSink.Dispose();
                }
                catch
                {
                }

                _messageSink = null;
            }
        }
    }

    #region P/Invoke Definitions

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    #endregion
}
