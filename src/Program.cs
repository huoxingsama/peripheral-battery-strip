using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("外设电量条")]
[assembly: AssemblyDescription("在 Windows 任务栏上显示外接设备的电量")]
[assembly: AssemblyProduct("外设电量条")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    private const string HostName = "Peripheral Battery Strip Background";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValue = "PeripheralBatteryStrip";
    internal const int RefreshMessage = 0x8101;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1)
        {
            WriteLine("参数过多。使用 --help 查看用法。", true);
            return 64;
        }

        string mode = args.Length == 0 ? "" : args[0];
        switch (mode)
        {
            case "--json":
                return ReadJson();
            case "--refresh":
                if (Signal(RefreshMessage)) return 0;
                WriteLine("外设电量条尚未运行。", true);
                return 1;
            case "--quit":
                Signal(0x0010);
                return 0;
            case "--version":
                WriteLine("1.0.0");
                return 0;
            case "--help":
            case "-h":
                WriteLine("外设电量条 1.0.0\n"
                    + "  无参数       打开任务栏电量条\n"
                    + "  --json       扫描设备并输出 UTF-8 JSON\n"
                    + "  --refresh    请求运行中的电量条立即刷新\n"
                    + "  --quit       关闭电量条\n"
                    + "  --version    显示版本");
                return 0;
            case "":
            case "--prepare":
                break;
            default:
                WriteLine("未知参数。使用 --help 查看用法。", true);
                return 64;
        }

        bool own;
        using (var mutex = new Mutex(true, @"Local\PeripheralBatteryStrip", out own))
        {
            if (!own) return 0;
            bool preparation = mode == "--prepare";
            if (!preparation && !ProviderInstalled())
            {
                MessageBox.Show("找不到电量读取组件。请在安装包中运行「安装.cmd」。",
                    "外设电量条", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            using (var context = new BatteryStrip(preparation, HostName))
                Application.Run(context);
            return 0;
        }
    }

    private static bool Signal(int message)
    {
        IntPtr window = Native.FindWindow(null, HostName);
        return window != IntPtr.Zero && Native.PostMessage(window, (uint)message, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool ProviderInstalled()
    {
        return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BatteryHub.exe"));
    }

    private static int ReadJson()
    {
        if (!ProviderInstalled())
        {
            WriteLine("找不到 BatteryHub 电量读取组件。请先运行安装程序。", true);
            return 1;
        }
        Device[] devices = BatteryStrip.ReadDevices();
        if (devices == null)
        {
            WriteLine("扫描设备失败。请稍后重试。", true);
            return 1;
        }
        string json = new JavaScriptSerializer().Serialize(new
        {
            schemaVersion = 1,
            devices = devices.Select(device => new
            {
                name = device.Name,
                kind = device.PublicKind,
                percent = device.Percent >= 0 ? (int?)device.Percent : null,
                charging = device.Charging,
                connected = device.Connected,
                stale = device.ReportedAsleep && device.Percent >= 0
            }).ToArray()
        });
        WriteLine(json);
        return 0;
    }

    private static void WriteLine(string text, bool error = false)
    {
        int channel = error ? -12 : -11;
        IntPtr handle = Native.GetStdHandle(channel);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            Native.AttachConsole(uint.MaxValue);
            handle = Native.GetStdHandle(channel);
        }
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        string line = text.Replace("\n", "\r\n") + "\r\n";
        uint unused;
        if (Native.GetConsoleMode(handle, out unused))
        {
            Native.WriteConsole(handle, line, (uint)line.Length, out unused, IntPtr.Zero);
            return;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        Native.WriteFile(handle, bytes, (uint)bytes.Length, out unused, IntPtr.Zero);
    }
}

internal sealed class BatteryStrip : ApplicationContext
{
    private const int MaxSlots = 10;
    private const uint SlotBase = 0x4700;
    private const uint TrayMessage = 0x8100;
    private readonly bool preparation;
    private readonly HostWindow host;
    private readonly Icon blank;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 850 };
    private readonly Dictionary<string, KeyValuePair<DateTime, Device>> lastReadings =
        new Dictionary<string, KeyValuePair<DateTime, Device>>(StringComparer.OrdinalIgnoreCase);
    private Device[] devices = new Device[0];
    private Task<Device[]> query;
    private DateTime nextQuery;
    private DateTime lastSuccess;
    private bool loaded;
    private bool redraw = true;
    private int activeSlots;
    private StripLayer layer;
    private Rectangle lastBounds;
    private bool lightTheme;
    private int[] previousSlotOrder = new int[0];

    internal BatteryStrip(bool preparation, string hostName)
    {
        this.preparation = preparation;
        blank = EmptyIcon();
        host = new HostWindow(this, hostName);
        menu.Items.Add("立即刷新", null, delegate { nextQuery = DateTime.MinValue; Tick(); });
        menu.Items.Add("登录 Windows 时启动", null, delegate { ToggleStartup(); });
        menu.Items.Add("退出", null, delegate { ExitThread(); });
        menu.Opening += delegate
        {
            ((ToolStripMenuItem)menu.Items[1]).Checked = StartupEnabled();
        };
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();
    }

    private void Tick()
    {
        if (preparation)
        {
            ReconcileSlots(MaxSlots);
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (query != null && query.IsCompleted)
        {
            Device[] result = query.Status == TaskStatus.RanToCompletion ? query.Result : null;
            query = null;
            if (result != null)
            {
                result = result.Take(MaxSlots / 2).ToArray();
                for (int index = 0; index < result.Length; index++)
                    result[index] = ApplyLastReading(result[index], now);
                foreach (string stale in lastReadings.Where(item => now - item.Value.Key > TimeSpan.FromMinutes(5))
                    .Select(item => item.Key).ToArray())
                    lastReadings.Remove(stale);
                if (!loaded || !devices.SequenceEqual(result))
                {
                    devices = result;
                    redraw = true;
                }
                loaded = true;
                lastSuccess = now;
            }
            else if (loaded && now - lastSuccess > TimeSpan.FromMinutes(2))
            {
                devices = new Device[0];
                loaded = false;
                redraw = true;
            }
        }
        if (query == null && now >= nextQuery)
        {
            query = Task.Run(new Func<Device[]>(ReadDevices));
            nextQuery = now.AddSeconds(30);
        }

        int desired = Math.Min(MaxSlots, Math.Max(2, devices.Length * 2));
        ReconcileSlots(desired);
        PositionLayer();
    }

    private Device ApplyLastReading(Device current, DateTime now)
    {
        if (current.Percent >= 0 && !current.Dimmed)
        {
            lastReadings[current.Key] = new KeyValuePair<DateTime, Device>(now, current);
            return current;
        }
        KeyValuePair<DateTime, Device> last;
        if (current.Percent < 0 && current.GraceSeconds > 0 && lastReadings.TryGetValue(current.Key, out last) &&
            now - last.Key <= TimeSpan.FromSeconds(current.GraceSeconds))
        {
            current.Percent = last.Value.Percent;
            current.Level = last.Value.Level;
            current.Dimmed = true;
        }
        return current;
    }

    private void ReconcileSlots(int desired)
    {
        while (activeSlots > desired)
        {
            activeSlots--;
            SendSlot(2, activeSlots, null);
        }
        while (activeSlots < desired)
        {
            if (!SendSlot(0, activeSlots, blank, SlotName(activeSlots))) break;
            SendSlot(4, activeSlots, null);
            activeSlots++;
            redraw = true;
        }
    }

    private bool SendSlot(uint action, int index, Icon icon, string tooltip = null)
    {
        var entry = new Native.NotifyIconData();
        entry.Size = Marshal.SizeOf(typeof(Native.NotifyIconData));
        entry.Window = host.Handle;
        entry.Id = SlotBase + (uint)index;
        entry.Version = 4;
        if (action == 0 || action == 1)
        {
            entry.Flags = 0x01 | 0x02 | 0x04 | 0x80;
            entry.Callback = TrayMessage;
            entry.Icon = icon.Handle;
            entry.Tooltip = tooltip;
        }
        return Native.Shell_NotifyIcon(action, ref entry);
    }

    private static string SlotName(int index)
    {
        return "外设电量位置 " + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string SlotTooltip(Device device, int index)
    {
        if (device == null) return SlotName(index) + "\n外设电量";
        string level = device.Level == "×" ? "未连接" : device.Level == "?" ? "电量未知" : device.Level;
        string label = SlotName(index) + "\n" + device.Name + " " + level;
        return label.Length > 127 ? label.Substring(0, 127) : label;
    }

    private void PositionLayer()
    {
        IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
        Native.Rect root;
        Rectangle bounds = Rectangle.Empty;
        int[] slotOrder = new int[0];
        bool ready = bar != IntPtr.Zero && Native.IsWindowVisible(bar) &&
            Native.GetWindowRect(bar, out root) && !HasFullscreenForeground(bar) &&
            TryVisibleBounds(root.Rectangle, out bounds, out slotOrder);
        if (!ready)
        {
            if (layer != null) Native.ShowWindow(layer.Handle, 0);
            lastBounds = Rectangle.Empty;
            return;
        }

        if (layer == null)
        {
            layer = new StripLayer();
            redraw = true;
        }

        int dpi = (int)Native.GetDpiForWindow(bar);
        bool light = Convert.ToInt32(Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0), CultureInfo.InvariantCulture) != 0;
        if (redraw || light != lightTheme || bounds.Size != lastBounds.Size)
        {
            layer.Paint(bounds.Size, dpi > 0 ? dpi : 96, devices, loaded, light);
            lightTheme = light;
        }
        if (redraw || !previousSlotOrder.SequenceEqual(slotOrder))
        {
            for (int offset = 0; offset < slotOrder.Length; offset++)
            {
                int slot = slotOrder[offset];
                Device device = devices.Length > 0 ? devices[offset * devices.Length / slotOrder.Length] : null;
                SendSlot(1, slot, blank, SlotTooltip(device, slot));
            }
            previousSlotOrder = slotOrder;
        }
        Native.SetWindowPos(layer.Handle, new IntPtr(-1), bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0x0010 | 0x0040);
        lastBounds = bounds;
        redraw = false;
    }

    private static bool HasFullscreenForeground(IntPtr bar)
    {
        IntPtr window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero || window == bar || window == Native.GetShellWindow()) return false;
        Native.Rect rect;
        if (!Native.GetWindowRect(window, out rect) ||
            !rect.Rectangle.Contains(Screen.FromHandle(bar).Bounds)) return false;
        var className = new StringBuilder(64);
        Native.GetClassName(window, className, className.Capacity);
        return className.ToString() != "Progman" && className.ToString() != "WorkerW";
    }

    private bool TryVisibleBounds(Rectangle bar, out Rectangle bounds, out int[] order)
    {
        bounds = Rectangle.Empty;
        order = new int[0];
        if (activeSlots == 0 || bar.Width < bar.Height) return false;
        var slots = new List<KeyValuePair<int, Rectangle>>(activeSlots);
        for (int index = 0; index < activeSlots; index++)
        {
            var id = new Native.IconIdentifier
            {
                Size = Marshal.SizeOf(typeof(Native.IconIdentifier)),
                Window = host.Handle,
                Id = SlotBase + (uint)index
            };
            Native.Rect rect;
            if (Native.Shell_NotifyIconGetRect(ref id, out rect) != 0 ||
                !bar.Contains(rect.Rectangle) || rect.Rectangle.Height < bar.Height / 2 || rect.Rectangle.Width < 12)
                return false;
            slots.Add(new KeyValuePair<int, Rectangle>(index, rect.Rectangle));
        }
        slots.Sort(delegate(KeyValuePair<int, Rectangle> left, KeyValuePair<int, Rectangle> right)
        {
            return left.Value.Left.CompareTo(right.Value.Left);
        });
        bounds = slots[0].Value;
        for (int index = 1; index < slots.Count; index++)
        {
            Rectangle next = slots[index].Value;
            if (next.Top != bounds.Top || next.Bottom != bounds.Bottom || Math.Abs(next.Left - bounds.Right) > 2)
                return false;
            bounds = Rectangle.Union(bounds, next);
        }
        order = slots.Select(slot => slot.Key).ToArray();
        return true;
    }

    internal static Device[] ReadDevices()
    {
        try
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(directory, "BatteryHub.exe"),
                Arguments = "--json",
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            }))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(25000))
                {
                    process.Kill();
                    return null;
                }
                if (process.ExitCode != 0 || !output.Wait(1000)) return null;
                var raw = new JavaScriptSerializer().Deserialize<Dictionary<string, object>[]>(output.Result);
                return raw.Select(Device.From).Where(device => device != null)
                    .OrderBy(device => device.Order).ThenBy(device => device.Name, StringComparer.CurrentCulture)
                    .ToArray();
            }
        }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (ArgumentException) { return null; }
        catch (AggregateException) { return null; }
    }

    private static Icon EmptyIcon()
    {
        using (var image = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            IntPtr raw = image.GetHicon();
            using (var borrowed = Icon.FromHandle(raw))
            {
                var copy = (Icon)borrowed.Clone();
                Native.DestroyIcon(raw);
                return copy;
            }
        }
    }

    private static string StartupCommand { get { return "\"" + Application.ExecutablePath + "\""; } }
    private static bool StartupEnabled()
    {
        return string.Equals((string)Registry.GetValue("HKEY_CURRENT_USER\\" + Program.RunKey,
            Program.RunValue, ""), StartupCommand, StringComparison.OrdinalIgnoreCase);
    }
    private static void ToggleStartup()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.RunKey, true))
        {
            if (StartupEnabled()) key.DeleteValue(Program.RunValue, false);
            else key.SetValue(Program.RunValue, StartupCommand, RegistryValueKind.String);
        }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        ReconcileSlots(0);
        if (layer != null) layer.DestroyHandle();
        host.DestroyHandle();
        menu.Dispose();
        timer.Dispose();
        blank.Dispose();
        base.ExitThreadCore();
    }

    private sealed class HostWindow : NativeWindow
    {
        private readonly BatteryStrip owner;
        private readonly uint explorerCreated = Native.RegisterWindowMessage("TaskbarCreated");

        internal HostWindow(BatteryStrip owner, string name)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Caption = name, ExStyle = 0x80 });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0010)
            {
                owner.ExitThread();
                return;
            }
            if (message.Msg == TrayMessage && ((int)message.LParam & 0xffff) == 0x007B)
            {
                owner.menu.Show(Cursor.Position);
                return;
            }
            if (message.Msg == Program.RefreshMessage)
            {
                owner.nextQuery = DateTime.MinValue;
                owner.Tick();
                return;
            }
            if (message.Msg == explorerCreated)
            {
                owner.activeSlots = 0;
                owner.lastBounds = Rectangle.Empty;
                owner.redraw = true;
            }
            base.WndProc(ref message);
        }
    }
}

internal sealed class Device : IEquatable<Device>
{
    internal string Key;
    internal string Name;
    internal string Kind;
    internal string Level;
    internal int Percent;
    internal int GraceSeconds;
    internal bool Charging;
    internal bool Dimmed;
    internal bool Connected;
    internal bool ReportedAsleep;

    internal int Order { get { return Kind == "headset" ? 0 : Kind == "mouse" ? 1 : 2; } }
    internal string PublicKind
    {
        get
        {
            switch (Kind)
            {
                case "headset":
                case "mouse":
                case "keyboard":
                case "gamepad":
                case "phone":
                case "microphone":
                    return Kind;
                default:
                    return "other";
            }
        }
    }

    internal static Device From(Dictionary<string, object> raw)
    {
        string name = Text(raw, "name");
        string key = Text(raw, "key");
        int percent = Number(raw, "percent", -1);
        if (percent < 0 || percent > 100) percent = -1;
        bool connected = Flag(raw, "connected");
        bool asleep = Flag(raw, "asleep");
        string level = Text(raw, "level");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) return null;
        if (percent >= 0 && percent <= 100) level = percent.ToString(CultureInfo.InvariantCulture) + "%";
        if (string.IsNullOrWhiteSpace(level)) level = connected ? "?" : "×";
        return new Device { Key = key, Name = name, Kind = Text(raw, "kind"), Level = level, Percent = percent,
            GraceSeconds = Math.Min(300, Number(raw, "graceSec", 0)),
            Charging = Flag(raw, "charging"), Dimmed = asleep || !connected,
            Connected = connected, ReportedAsleep = asleep };
    }

    private static string Text(Dictionary<string, object> raw, string key)
    {
        object value;
        return raw.TryGetValue(key, out value) ? value as string ?? "" : "";
    }
    private static bool Flag(Dictionary<string, object> raw, string key)
    {
        object value;
        return raw.TryGetValue(key, out value) && value is bool && (bool)value;
    }
    private static int Number(Dictionary<string, object> raw, string key, int fallback)
    {
        object value;
        return raw.TryGetValue(key, out value) && value is int ? (int)value : fallback;
    }

    public bool Equals(Device other)
    {
        return other != null && Key == other.Key && Name == other.Name && Kind == other.Kind && Level == other.Level &&
            Percent == other.Percent && Charging == other.Charging && Dimmed == other.Dimmed;
    }
}

internal sealed class StripLayer : NativeWindow
{
    internal StripLayer()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Peripheral Battery Strip Layer",
            Style = unchecked((int)0x80000000),
            ExStyle = 0x80000 | 0x08000000 | 0x0020 | 0x0080,
            Width = 1,
            Height = 1
        });
    }

    internal void Paint(Size size, int dpi, Device[] devices, bool loaded, bool light)
    {
        using (var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var glyphFont = new Font("Segoe Fluent Icons", 16f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textFont = new Font("Segoe UI Variable Text", 13f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var compactFont = new Font("Segoe UI Variable Text", 11f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center })
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Color foreground = light ? Color.Black : Color.White;
                if (devices.Length == 0)
                {
                    using (var brush = new SolidBrush(Color.FromArgb(190, foreground)))
                    {
                        format.Alignment = StringAlignment.Center;
                        g.DrawString(loaded ? "—" : "…", textFont, brush, new RectangleF(0, 0, size.Width, size.Height), format);
                    }
                }
                else
                {
                    float cell = (float)size.Width / devices.Length;
                    for (int index = 0; index < devices.Length; index++)
                        DrawDevice(g, new RectangleF(index * cell, 0, cell, size.Height), devices[index],
                            glyphFont, textFont, compactFont, format, foreground, dpi);
                }
            }
            Present(bitmap);
        }
    }

    private static void DrawDevice(Graphics g, RectangleF cell, Device device, Font glyphFont, Font textFont,
        Font compactFont, StringFormat format, Color foreground, int dpi)
    {
        string glyph;
        switch (device.Kind)
        {
            case "headset": glyph = "\uE7F6"; break;
            case "mouse": glyph = "\uE962"; break;
            case "keyboard": glyph = "\uE765"; break;
            case "gamepad": glyph = "\uE7FC"; break;
            case "phone": glyph = "\uE8EA"; break;
            case "microphone": glyph = "\uE720"; break;
            default: glyph = "\uE772"; break;
        }
        float gap = 5f * dpi / 96f;
        float glyphWidth = g.MeasureString(glyph, glyphFont, 100, format).Width;
        float textWidth = g.MeasureString(device.Level, textFont, 100, format).Width;
        float chargingWidth = device.Charging ? 10f * dpi / 96f : 0;
        Font chosen = textFont;
        if (glyphWidth + gap + textWidth + chargingWidth > cell.Width - 4)
        {
            chosen = compactFont;
            textWidth = g.MeasureString(device.Level, chosen, 100, format).Width;
            gap /= 2;
        }
        float total = glyphWidth + gap + textWidth + chargingWidth;
        float x = cell.Left + Math.Max(1, (cell.Width - total) / 2f);
        int alpha = device.Dimmed ? 140 : 255;
        var clip = g.Save();
        g.SetClip(cell);
        using (var symbol = new SolidBrush(Color.FromArgb(alpha, foreground)))
        using (var value = new SolidBrush(Color.FromArgb(alpha,
            device.Percent >= 0 && device.Percent <= 15 && !device.Charging ? Color.FromArgb(242, 65, 77) : foreground)))
        {
            g.DrawString(glyph, glyphFont, symbol, new RectangleF(x, 0, glyphWidth + 2, cell.Height), format);
            g.DrawString(device.Level, chosen, value, new RectangleF(x + glyphWidth + gap, 0, textWidth + 2, cell.Height), format);
        }
        if (device.Charging)
        {
            using (var bolt = new SolidBrush(device.Percent == 100 ? Color.MediumSeaGreen : Color.DeepSkyBlue))
                g.DrawString(device.Percent == 100 ? "\u2713" : "\u26A1", compactFont, bolt,
                    new RectangleF(x + glyphWidth + gap + textWidth, 0, chargingWidth + 2, cell.Height), format);
        }
        g.Restore(clip);
    }

    private void Present(Bitmap bitmap)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memory = Native.CreateCompatibleDC(screen);
        var header = new Native.BitmapHeader { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, Bits = 32 };
        IntPtr buffer;
        IntPtr dib = Native.CreateDIBSection(memory, ref header, 0, out buffer, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
            return;
        }
        IntPtr previous = Native.SelectObject(memory, dib);
        try
        {
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = new byte[bitmap.Width * 4];
                for (int row = 0; row < bitmap.Height; row++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes, 0, bytes.Length);
                    Marshal.Copy(bytes, 0, IntPtr.Add(buffer, row * bytes.Length), bytes.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }
            var size = new Native.Pair { X = bitmap.Width, Y = bitmap.Height };
            var source = new Native.Pair();
            var blend = new Native.Blend { Alpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally
        {
            Native.SelectObject(memory, previous);
            Native.DeleteObject(dib);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    internal static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", CharSet = CharSet.Unicode)]
    internal static extern bool WriteConsole(IntPtr output, string text, uint length, out uint written, IntPtr reserved);
    [DllImport("kernel32.dll")]
    internal static extern bool WriteFile(IntPtr output, byte[] buffer, uint length, out uint written, IntPtr overlapped);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Pair { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blend { internal byte Operation, Flags, Alpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int X, Y;
        internal uint Colors, Important;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal int Size;
        internal IntPtr Window;
        internal uint Id, Flags, Callback;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tooltip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Title;
        internal uint InfoFlags;
        internal Guid Guid;
        internal IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IconIdentifier
    {
        internal int Size;
        internal IntPtr Window;
        internal uint Id;
        internal Guid Guid;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    internal static extern bool Shell_NotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("shell32.dll")]
    internal static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]
    internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, IntPtr point, ref Pair size,
        IntPtr memory, ref Pair source, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr reference);
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr dc);
}
