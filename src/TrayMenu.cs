using System;
using System.Runtime.InteropServices;

internal enum TrayCommand
{
    None,
    Refresh,
    Autostart,
    Exit
}

internal static class TrayMenu
{
    private const uint MenuChecked = 0x0008;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackRightAlign = 0x0008;
    private const uint TrackNoNotify = 0x0080;
    private const uint TrackReturnCommand = 0x0100;
    private const int MenuDropAlignment = 40;
    private const uint NullMessage = 0;

    internal static TrayCommand Show(IntPtr owner, bool startupEnabled)
    {
        Point cursor;
        if (owner == IntPtr.Zero || !GetCursorPos(out cursor))
            return TrayCommand.None;

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return TrayCommand.None;

        try
        {
            if (!AppendMenuW(menu, 0, new UIntPtr((uint)TrayCommand.Refresh), "立即刷新") ||
                !AppendMenuW(menu, startupEnabled ? MenuChecked : 0, new UIntPtr((uint)TrayCommand.Autostart), "登录 Windows 时启动") ||
                !AppendMenuW(menu, 0, new UIntPtr((uint)TrayCommand.Exit), "退出"))
                return TrayCommand.None;

            // A foreground owner lets Windows dismiss the menu on clicks outside it.
            if (!SetForegroundWindow(owner) && GetForegroundWindow() != owner)
                return TrayCommand.None;

            uint flags = TrackReturnCommand | TrackNoNotify | TrackRightButton;
            if (GetSystemMetrics(MenuDropAlignment) != 0)
                flags |= TrackRightAlign;

            uint chosen = TrackPopupMenuEx(menu, flags, cursor.X, cursor.Y, owner, IntPtr.Zero);
            // Posting a benign message lets the notification-area menu reopen normally.
            PostMessageW(owner, NullMessage, IntPtr.Zero, IntPtr.Zero);
            return (TrayCommand)chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr itemId, string text);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr param1, IntPtr param2);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyMenu(IntPtr menu);
}
