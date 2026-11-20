using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class GlyphHover : IDisposable
{
    private const uint AddTool = 0x0432;
    private const uint UpdateTipText = 0x0439;
    private const uint TrackActivate = 0x0411;
    private const uint TrackPosition = 0x0412;
    private const uint SetMaxTipWidth = 0x0418;
    private const uint GetBubbleSize = 0x041e;
    private const uint Off = 0;
    private const uint On = 1;

    private readonly IntPtr owner;
    private readonly Timer timer;
    private ToolInfo tool;
    private IntPtr window;
    private string text;
    private Point anchor;
    private bool visible;
    private bool leaveTracked;
    private bool disposed;

    internal GlyphHover(IntPtr owner)
    {
        this.owner = owner;
        timer = new Timer { Interval = 400 };
        timer.Tick += ShowAfterDelay;
    }

    internal void Move(string nextText, Point screenAnchor)
    {
        if (disposed) return;
        if (string.IsNullOrEmpty(nextText))
        {
            Clear();
            return;
        }

        bool anchorChanged = anchor != screenAnchor;
        anchor = screenAnchor;
        if (string.Equals(text, nextText, StringComparison.Ordinal))
        {
            if (visible && anchorChanged) PositionTooltip();
            return;
        }

        HideTooltip();
        text = nextText;
        timer.Stop();
        timer.Start();
    }

    internal void Clear()
    {
        if (disposed) return;
        leaveTracked = false;
        timer.Stop();
        text = null;
        HideTooltip();
    }

    internal void TrackLeave()
    {
        if (disposed || leaveTracked) return;
        var tracking = new MouseTracking
        {
            Size = Marshal.SizeOf(typeof(MouseTracking)),
            Flags = 0x02, // TME_LEAVE
            Window = owner
        };
        leaveTracked = TrackMouseEvent(ref tracking);
    }

    private void ShowAfterDelay(object sender, EventArgs args)
    {
        timer.Stop();
        if (text == null || !EnsureTooltip()) return;

        IntPtr previous = tool.Text;
        tool.Text = Marshal.StringToHGlobalUni(text);
        SendToolMessage(window, UpdateTipText, Off, ref tool);
        Marshal.FreeHGlobal(previous);
        PositionTooltip();
        SendToolMessage(window, TrackActivate, On, ref tool);
        visible = true;
    }

    private void HideTooltip()
    {
        if (!visible) return;
        SendToolMessage(window, TrackActivate, Off, ref tool);
        visible = false;
    }

    private bool EnsureTooltip()
    {
        if (window != IntPtr.Zero) return true;
        var controls = new CommonControls { Size = Marshal.SizeOf(typeof(CommonControls)), Classes = 0xff };
        if (!InitCommonControlsEx(ref controls)) return false;

        const uint topmostToolWindow = 0x08000088; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        const uint tooltipStyle = 0x80000003; // WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP
        window = CreateWindowEx(topmostToolWindow, "tooltips_class32", null, tooltipStyle,
            0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) return false;

        tool = new ToolInfo
        {
            Size = Marshal.SizeOf(typeof(ToolInfo)),
            Flags = 0xa1, // TTF_IDISHWND | TTF_TRACK | TTF_ABSOLUTE
            Window = owner,
            Id = owner,
            Text = Marshal.StringToHGlobalUni(" ")
        };
        if (SendToolMessage(window, AddTool, Off, ref tool) != IntPtr.Zero) return true;

        DestroyTooltip();
        return false;
    }

    private void PositionTooltip()
    {
        int dpi = (int)GetDpiForWindow(owner);
        if (dpi <= 0) dpi = 96;
        int gap = Math.Max(1, 6 * dpi / 96);
        Rectangle work = Screen.FromPoint(anchor).WorkingArea;
        int maxWidth = Math.Min(work.Width, 320 * dpi / 96);
        SendMessage(window, SetMaxTipWidth, IntPtr.Zero, new IntPtr(maxWidth));

        long size = SendToolMessage(window, GetBubbleSize, Off, ref tool).ToInt64();
        int width = (int)(size & 0xffff);
        int height = (int)((size >> 16) & 0xffff);
        if (width <= 0 || height <= 0)
        {
            Size measured = TextRenderer.MeasureText(text, SystemFonts.StatusFont,
                new Size(maxWidth, work.Height), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            width = measured.Width + gap * 2;
            height = measured.Height + gap;
        }

        int x = Math.Max(work.Left, Math.Min(anchor.X - width / 2, work.Right - width));
        int y = anchor.Y - gap - height;
        if (y < work.Top) y = anchor.Y + gap;
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        int packed = unchecked((int)(((uint)y << 16) | ((uint)x & 0xffff)));
        SendMessage(window, TrackPosition, IntPtr.Zero, new IntPtr(packed));
    }

    private void DestroyTooltip()
    {
        if (window != IntPtr.Zero) DestroyWindow(window);
        window = IntPtr.Zero;
        Marshal.FreeHGlobal(tool.Text);
        tool.Text = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear();
        disposed = true;
        timer.Dispose();
        DestroyTooltip();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToolInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Window, Id;
        public int Left, Top, Right, Bottom;
        public IntPtr Instance, Text, Param, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonControls
    {
        public int Size;
        public uint Classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        public int Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref CommonControls controls);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendToolMessage(IntPtr hwnd, uint message, uint wParam, ref ToolInfo toolInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref MouseTracking tracking);
}
