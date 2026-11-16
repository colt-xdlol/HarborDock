using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Windows.Devices.Radios;

namespace HarborDock
{
    internal sealed class StatusSnapshot
    {
        public bool WifiPresent;
        public bool WifiConnected;
        public bool WifiOff;
        public bool EthernetConnected;
        public int Signal = -1;
        public string WifiName = "Wi-Fi: проверка состояния";
        public string BluetoothName = "Bluetooth: проверка состояния";
        public bool BluetoothOn;
    }

    internal sealed class KeyboardChoice
    {
        public IntPtr Layout;
        public string Name;
    }

    internal static class SystemStatus
    {
        private const uint WlanVersion = 2;
        private const int CurrentConnectionOpcode = 7;
        private const int ConnectedState = 1;
        private const int InputLanguageRequest = 0x50;
        private const int MaxInterfaces = 64;
        private static IReadOnlyList<Radio> radios;
        private static DateTime lastRadioRefresh = DateTime.MinValue;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterface
        {
            public Guid Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public int State;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Ssid
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Association
        {
            public Ssid Ssid;
            public int BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public int PhyType;
            public uint PhyIndex;
            public uint Quality;
            public uint RxRate;
            public uint TxRate;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Security
        {
            public int SecurityEnabled;
            public int OneXEnabled;
            public int AuthAlgorithm;
            public int CipherAlgorithm;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Connection
        {
            public int State;
            public int Mode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
            public Association Association;
            public Security Security;
        }

        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int type);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

        public static void InvalidateRadios() { lastRadioRefresh = DateTime.MinValue; }

        public static async Task<StatusSnapshot> ReadAsync()
        {
            StatusSnapshot result = ReadWifi();
            try
            {
                if (radios == null || (DateTime.UtcNow - lastRadioRefresh).TotalSeconds > Config.RadioRefreshSeconds)
                {
                    var query = Radio.GetRadiosAsync();
                    DateTime started = DateTime.UtcNow;
                    while (query.Status == Windows.Foundation.AsyncStatus.Started && (DateTime.UtcNow - started).TotalMilliseconds < Config.StatusTimeoutMs)
                        await Task.Delay(40).ConfigureAwait(false);
                    if (query.Status == Windows.Foundation.AsyncStatus.Completed) radios = query.GetResults();
                    else query.Cancel();
                    query.Close();
                    lastRadioRefresh = DateTime.UtcNow;
                }
                if (radios != null)
                {
                    var bluetooth = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();
                    result.BluetoothOn = bluetooth.Any(r => r.State == RadioState.On);
                    result.BluetoothName = result.BluetoothOn ? "Bluetooth: включён" : bluetooth.Count > 0
                        ? "Bluetooth: выключен" : "Bluetooth: адаптер не найден";
                    var wifi = radios.Where(r => r.Kind == RadioKind.WiFi).ToList();
                    if (wifi.Count > 0 && wifi.All(r => r.State == RadioState.Off || r.State == RadioState.Disabled))
                    {
                        result.WifiOff = true;
                        result.WifiConnected = false;
                        result.WifiName = "Wi-Fi: выключен";
                    }
                }
                else result.BluetoothName = "Bluetooth: состояние недоступно";
            }
            catch { result.BluetoothName = "Bluetooth: состояние недоступно"; }
            return result;
        }

        private static StatusSnapshot ReadWifi()
        {
            StatusSnapshot result = new StatusSnapshot { WifiName = "Wi-Fi: адаптер не найден" };
            IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                result.EthernetConnected = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.OperationalStatus == OperationalStatus.Up);
                uint version;
                if (WlanOpenHandle(WlanVersion, IntPtr.Zero, out version, out client) != 0)
                {
                    result.WifiName = "Wi-Fi: служба недоступна";
                    return result;
                }
                if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return result;
                int count = Math.Min(MaxInterfaces, Math.Max(0, Marshal.ReadInt32(list)));
                int stride = Marshal.SizeOf(typeof(WlanInterface));
                int header = sizeof(uint) * 2;
                for (int index = 0; index < count; index++)
                {
                    WlanInterface adapter = (WlanInterface)Marshal.PtrToStructure(IntPtr.Add(list, header + index * stride), typeof(WlanInterface));
                    result.WifiPresent = true;
                    if (!result.WifiConnected) result.WifiName = "Wi-Fi: не подключён";
                    if (adapter.State != ConnectedState) continue;
                    result.WifiConnected = true;
                    IntPtr data = IntPtr.Zero;
                    try
                    {
                        uint length; int type;
                        if (WlanQueryInterface(client, ref adapter.Id, CurrentConnectionOpcode, IntPtr.Zero, out length, out data, out type) != 0 || length < Marshal.SizeOf(typeof(Connection)))
                        {
                            result.WifiName = "Wi-Fi: подключён · сведения недоступны";
                            continue;
                        }
                        Connection connection = (Connection)Marshal.PtrToStructure(data, typeof(Connection));
                        string name = Encoding.UTF8.GetString(connection.Association.Ssid.Bytes, 0, (int)Math.Min(32, connection.Association.Ssid.Length));
                        name = new string(name.Where(c => !Char.IsControl(c)).ToArray());
                        result.Signal = (int)Math.Min(100, connection.Association.Quality);
                        result.WifiName = "Wi-Fi: " + (String.IsNullOrWhiteSpace(name) ? connection.Profile : name) + " · " + result.Signal + "%";
                        break;
                    }
                    finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
                }
                if (!result.WifiPresent && result.EthernetConnected) result.WifiName = "Wi-Fi: адаптер не найден · Ethernet подключён";
            }
            catch { result.WifiName = "Wi-Fi: состояние недоступно"; }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
            return result;
        }

        public static IntPtr ForegroundLayout(out IntPtr window)
        {
            window = Native.GetForegroundWindow();
            return WindowLayout(window);
        }

        public static IntPtr WindowLayout(IntPtr window)
        {
            uint process;
            uint thread = Native.GetWindowThreadProcessId(window, out process);
            return GetKeyboardLayout(thread);
        }

        public static CultureInfo LayoutCulture(IntPtr layout)
        {
            try { return CultureInfo.GetCultureInfo((int)(layout.ToInt64() & 0xffff)); }
            catch { return CultureInfo.InvariantCulture; }
        }

        public static string LanguageCode(IntPtr layout)
        {
            CultureInfo culture = LayoutCulture(layout);
            if (culture.TwoLetterISOLanguageName == "ru") return "РУС";
            if (culture.TwoLetterISOLanguageName == "en") return "ENG";
            return culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }

        public static List<KeyboardChoice> Keyboards()
        {
            int count = GetKeyboardLayoutList(0, null);
            IntPtr[] layouts = new IntPtr[Math.Max(0, count)];
            count = GetKeyboardLayoutList(layouts.Length, layouts);
            return layouts.Take(count).Select(h => new KeyboardChoice { Layout = h, Name = LayoutCulture(h).NativeName }).ToList();
        }

        public static bool SelectKeyboard(IntPtr window, IntPtr layout)
        {
            return window != IntPtr.Zero && Native.IsWindow(window) && PostMessage(window, InputLanguageRequest, IntPtr.Zero, layout);
        }

        public static ImageSource Icon(string kind, string code, StatusSnapshot state, bool light)
        {
            Color foreground = light ? Color.FromRgb(42, 54, 70) : Color.FromRgb(224, 233, 245);
            Color accent = light ? Color.FromRgb(33, 115, 100) : Color.FromRgb(141, 222, 206);
            Color muted = light ? Color.FromRgb(125, 137, 152) : Color.FromRgb(143, 157, 178);
            DrawingGroup group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
            {
                // Фиксированная область сохраняет одинаковый размер статусных значков.
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
                if (kind == "language")
                {
                    FormattedText text = new FormattedText(code ?? "—", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 19,
                        new SolidColorBrush(foreground), 1);
                    dc.DrawText(text, new Point((64 - text.Width) / 2, (64 - text.Height) / 2));
                }
                else if (kind == "bluetooth")
                {
                    Pen pen = new Pen(new SolidColorBrush(state.BluetoothOn ? accent : muted), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    StreamGeometry shape = new StreamGeometry();
                    using (StreamGeometryContext path = shape.Open())
                    {
                        path.BeginFigure(new Point(31, 12), false, false);
                        path.PolyLineTo(new[] { new Point(43, 23), new Point(22, 42) }, true, false);
                        path.BeginFigure(new Point(31, 12), false, false);
                        path.PolyLineTo(new[] { new Point(31, 52), new Point(43, 41), new Point(22, 22) }, true, false);
                    }
                    dc.DrawGeometry(null, pen, shape);
                    if (!state.BluetoothOn) dc.DrawLine(new Pen(new SolidColorBrush(muted), 2), new Point(14, 49), new Point(49, 14));
                }
                else
                {
                    int level = state.Signal < 0 ? 3 : state.Signal >= 66 ? 3 : state.Signal >= 33 ? 2 : 1;
                    for (int ring = 3; ring >= 1; ring--)
                    {
                        double radius = ring * 9;
                        StreamGeometry shape = new StreamGeometry();
                        using (StreamGeometryContext path = shape.Open())
                        {
                            path.BeginFigure(new Point(32 - radius * .77, 47 - radius * .64), false, false);
                            path.ArcTo(new Point(32 + radius * .77, 47 - radius * .64), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                        }
                        Brush brush = new SolidColorBrush(state.WifiConnected && ring <= level ? accent : muted);
                        dc.PushOpacity(state.WifiConnected && ring > level ? .3 : 1);
                        dc.DrawGeometry(null, new Pen(brush, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, shape);
                        dc.Pop();
                    }
                    dc.DrawEllipse(new SolidColorBrush(state.WifiConnected ? accent : muted), null, new Point(32, 47), 2.5, 2.5);
                    if (!state.WifiConnected) dc.DrawLine(new Pen(new SolidColorBrush(muted), 2), new Point(14, 51), new Point(50, 15));
                }
            }
            DrawingImage image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        public static bool ValidateLayouts()
        {
            return Marshal.SizeOf(typeof(WlanInterface)) == 532 && Marshal.SizeOf(typeof(Association)) == 68 && Marshal.SizeOf(typeof(Connection)) == 604;
        }
    }
}
