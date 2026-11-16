using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace HarborDock
{
    internal sealed class DockWindow : Window
    {
        public readonly Settings Settings;
        public readonly List<DockItem> Items = new List<DockItem>();
        public readonly List<DockItem> Overflow = new List<DockItem>();
        public readonly DockSurface Surface;
        public Action PreferencesRequested;
        public Action ExitRequested;
        public Action<string> Error;
        private readonly TaskbarLease taskbar = new TaskbarLease();
        private readonly DispatcherTimer scanTimer;
        private readonly DispatcherTimer frameTimer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<string, DockItem> retained = new Dictionary<string, DockItem>(StringComparer.OrdinalIgnoreCase);
        private List<WindowInfo> windows = new List<WindowInfo>();
        private double lastTime;
        private double lastHover;
        private double forceUntil;
        private double visibility = 1;
        private bool scanning;
        private bool closing;
        private bool disposed;
        private bool readingStatus;
        private StatusSnapshot status = new StatusSnapshot();
        private double lastStatusScan = -10;
        private double lastKeyboardScan;
        private double lastWindowCheck = -1;
        private bool coveringWindow;
        private readonly ElasticMotion compression = new ElasticMotion();
        internal IntPtr InputWindow;
        internal IntPtr InputLayout;
        private HwndSource source;
        internal IntPtr Handle;
        internal Forms.Screen Monitor;
        internal double DpiX = 1;
        internal double DpiY = 1;
        internal double IconSize;
        internal bool MenuOpen;

        public DockWindow(Settings settings, bool preview = false)
        {
            Settings = settings;
            Title = Config.Product;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = null;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Height = Config.WindowHeight;
            Surface = new DockSurface(this);
            Content = Surface;
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { ApplySettings(); BeginScan(); BeginStatusScan(); };
            Closing += delegate { closing = true; Cleanup(); };
            scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Config.ScanIntervalSeconds) };
            scanTimer.Tick += delegate { BeginScan(); CheckMonitor(); };
            frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Config.FrameIntervalMs) };
            frameTimer.Tick += delegate { Frame(); };
            if (!preview) { scanTimer.Start(); frameTimer.Start(); }
        }

        public void PreparePreview()
        {
            windows = Shell.ScanWindows();
            status = Task.Run(() => SystemStatus.ReadAsync()).GetAwaiter().GetResult();
            InputLayout = SystemStatus.ForegroundLayout(out InputWindow);
            RebuildItems();
            Width = Math.Min(1400, Monitor.Bounds.Width / DpiX - 24);
            Surface.Measure(new Size(Width, Height));
            Surface.Arrange(new Rect(0, 0, Width, Height));
            Surface.UpdateLayout();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            Handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            source = HwndSource.FromHwnd(Handle);
            source.AddHook(WndProc);
            Native.RegisterHotKey(Handle, Config.HotkeyDock, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.D));
            Native.RegisterHotKey(Handle, Config.HotkeySettings, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.S));
            UpdatePosition();
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
            if (message == Native.WM_HOTKEY)
            {
                if (wparam.ToInt32() == Config.HotkeySettings) RequestPreferences();
                if (wparam.ToInt32() == Config.HotkeyDock) Reveal();
                handled = true;
            }
            if (message == Native.WM_DPICHANGED || message == Native.WM_DISPLAYCHANGE)
                Dispatcher.BeginInvoke(new Action(UpdatePosition));
            if (message == 0x219) SystemStatus.InvalidateRadios();
            return IntPtr.Zero;
        }

        public void Reveal() { forceUntil = clock.Elapsed.TotalSeconds + 3; lastHover = clock.Elapsed.TotalSeconds; Topmost = true; }
        public void RequestPreferences() { Reveal(); if (PreferencesRequested != null) PreferencesRequested(); }
        public void RequestExit() { if (ExitRequested != null) ExitRequested(); }

        public void ApplySettings()
        {
            try
            {
                Settings.Validate();
                if (Settings.HideTaskbar) taskbar.Enable(); else taskbar.Dispose();
                Settings.Save();
            }
            catch (Exception ex)
            {
                Settings.HideTaskbar = false;
                taskbar.Dispose();
                Report("Настройки не применены: " + ex.Message, ex);
            }
            RebuildItems();
            RefreshStatusIcons();
            UpdatePosition();
            Reveal();
            Surface.InvalidateVisual();
        }

        public void RestoreTaskbar()
        {
            Settings.HideTaskbar = false;
            Settings.AutoHide = false;
            ApplySettings();
        }

        private void CheckMonitor()
        {
            Forms.Screen next = SelectMonitor();
            if (Monitor == null || next.Bounds != Monitor.Bounds || next.WorkingArea != Monitor.WorkingArea || next.DeviceName != Monitor.DeviceName)
                UpdatePosition();
        }

        private Forms.Screen SelectMonitor()
        {
            return Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Settings.Monitor) ?? Forms.Screen.PrimaryScreen;
        }

        public void UpdatePosition()
        {
            Monitor = SelectMonitor();
            if (source != null && source.CompositionTarget != null)
            {
                DpiX = source.CompositionTarget.TransformToDevice.M11;
                DpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            double available = Monitor.Bounds.Width / DpiX - Config.Edge * 2;
            IconSize = Layout.FitSize(Items.Count, Items.Count(i => i.DividerBefore), Settings.IconSize, Settings.Magnification, available);
            double idealWidth = Config.Padding * 2 + Items.Count * (IconSize + Config.Gap) + Items.Count(i => i.DividerBefore) * Config.Gap
                + IconSize * (Settings.Magnification - 1) * 4;
            double restingWidth = Config.Padding * 2 + Items.Count * (IconSize + Config.Gap) - Config.Gap + Items.Count(i => i.DividerBefore) * Config.Gap;
            idealWidth = Math.Max(idealWidth, restingWidth * Config.CompactStretch + Config.ShadowMargin * 2);
            Width = Math.Min(available, Math.Max(240, idealWidth));
            Height = Config.WindowHeight;
            double bottom = (Settings.HideTaskbar ? Monitor.Bounds.Bottom : Monitor.WorkingArea.Bottom) / DpiY;
            Left = Monitor.Bounds.Left / DpiX + (Monitor.Bounds.Width / DpiX - Width) / 2;
            Top = bottom - Height;
            Surface.InvalidateVisual();
        }

        private async void BeginScan()
        {
            if (scanning || closing) return;
            scanning = true;
            try
            {
                List<WindowInfo> found = await Task.Run(() => Shell.ScanWindows());
                if (closing) return;
                windows = found;
                RebuildItems();
            }
            catch (Exception ex) { Log.Write("window scan", ex); }
            finally { scanning = false; }
        }

        private async void BeginStatusScan()
        {
            if (readingStatus || closing || !Settings.ShowStatus) return;
            readingStatus = true;
            lastStatusScan = clock.Elapsed.TotalSeconds;
            try
            {
                StatusSnapshot value = await Task.Run(() => SystemStatus.ReadAsync());
                if (closing) return;
                status = value;
                RefreshStatusIcons();
            }
            catch (Exception ex) { Log.Write("system status", ex); }
            finally { readingStatus = false; }
        }

        private void RefreshStatusIcons()
        {
            foreach (DockItem item in Items)
            {
                if (item.Kind != "wifi" && item.Kind != "bluetooth" && item.Kind != "language") continue;
                item.Icon = SystemStatus.Icon(item.Kind, SystemStatus.LanguageCode(InputLayout), status, Settings.LightTheme);
                if (item.Kind == "wifi") item.Name = status.WifiName;
                if (item.Kind == "bluetooth") item.Name = status.BluetoothName;
                if (item.Kind == "language") item.Name = "Язык ввода: " + SystemStatus.LayoutCulture(InputLayout).NativeName;
            }
            Surface.InvalidateVisual();
        }

        private DockItem Retain(string key, string name, string path, string kind, Pin pin)
        {
            DockItem item;
            if (!retained.TryGetValue(key, out item))
            {
                item = new DockItem { Key = key, Scale = 1 };
                retained[key] = item;
            }
            item.Name = name;
            item.Path = path;
            item.Kind = kind;
            item.Pin = pin;
            item.Pinned = pin != null && kind == "app";
            item.DividerBefore = false;
            if (item.Icon == null)
            {
                if (kind == "preferences") item.Icon = Shell.GlyphIcon("\uE713", Color.FromRgb(120, 135, 150));
                else if (kind == "apps") item.Icon = Shell.GlyphIcon("\uE80A", Color.FromRgb(78, 118, 174));
                else if (kind == "overflow") item.Icon = Shell.GlyphIcon("\uE712", Color.FromRgb(84, 109, 139));
                else item.Icon = Shell.Icon(path);
            }
            return item;
        }

        private void RebuildItems()
        {
            List<DockItem> next = new List<DockItem>();
            HashSet<IntPtr> assigned = new HashSet<IntPtr>();
            foreach (Pin pin in Settings.Pins)
            {
                string key = "pin:" + pin.Target + "\n" + pin.Arguments;
                string match = String.IsNullOrWhiteSpace(pin.MatchPath) ? pin.Target : pin.MatchPath;
                DockItem item = Retain(key, pin.Name, String.IsNullOrEmpty(pin.IconPath) ? pin.Target : pin.IconPath, "app", pin);
                item.Windows = windows.Where(w => !String.IsNullOrEmpty(w.Path) && String.Equals(w.Path, match, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (WindowInfo w in item.Windows) assigned.Add(w.Handle);
                next.Add(item);
            }
            if (Settings.ShowRunning)
            {
                var groups = windows.Where(w => !assigned.Contains(w.Handle)).GroupBy(w =>
                    String.IsNullOrEmpty(w.Path) || Path.GetFileName(w.Path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                        ? "window:" + w.Handle.ToString() : w.Path, StringComparer.OrdinalIgnoreCase).Take(Config.MaxRunning);
                bool first = true;
                foreach (var group in groups)
                {
                    WindowInfo window = group.First();
                    DockItem item = Retain("run:" + group.Key, window.Title, window.Path, "app", null);
                    item.Windows = group.ToList();
                    item.DividerBefore = first && next.Count > 0;
                    next.Add(item);
                    first = false;
                }
            }
            DockItem apps = Retain("utility:apps", "Все приложения", "", "apps", null);
            apps.DividerBefore = next.Count > 0;
            next.Add(apps);
            next.Add(Retain("utility:bin", "Корзина", "shell:RecycleBinFolder", "bin", null));
            next.Add(Retain("utility:preferences", "Настройки Harbor Dock", "", "preferences", null));
            if (Settings.ShowStatus)
            {
                DockItem wifi = Retain("status:wifi", status.WifiName, "", "wifi", null);
                wifi.DividerBefore = true;
                next.Add(wifi);
                next.Add(Retain("status:bluetooth", status.BluetoothName, "", "bluetooth", null));
                next.Add(Retain("status:language", "Язык ввода", "", "language", null));
            }
            Overflow.Clear();
            double available = SelectMonitor().Bounds.Width / DpiX - Config.Edge * 2;
            int maxCount = Math.Max(10, (int)((available - Config.Padding * 2 - Config.Gap * 4 - Settings.IconSize * (Settings.Magnification - 1) * 3) / (Config.MinSize + Config.Gap)));
            if (next.Count > maxCount)
            {
                int utilityCount = Settings.ShowStatus ? 6 : 3;
                int firstHidden = Math.Max(1, maxCount - utilityCount - 1);
                int hiddenCount = next.Count - utilityCount - firstHidden;
                Overflow.AddRange(next.GetRange(firstHidden, hiddenCount));
                next.RemoveRange(firstHidden, hiddenCount);
                DockItem overflow = Retain("utility:overflow", "Ещё приложения · " + hiddenCount, "", "overflow", null);
                overflow.DividerBefore = true;
                next.Insert(firstHidden, overflow);
            }
            bool countChanged = Items.Count != next.Count;
            Items.Clear();
            Items.AddRange(next);
            HashSet<string> live = new HashSet<string>(next.Concat(Overflow).Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            foreach (string key in retained.Keys.Where(k => !live.Contains(k)).ToList()) retained.Remove(key);
            if (countChanged) UpdatePosition();
            RefreshStatusIcons();
            Surface.InvalidateVisual();
        }

        private void Frame()
        {
            if (closing || Monitor == null || Items.Count == 0) return;
            double now = clock.Elapsed.TotalSeconds;
            if (Settings.ShowStatus && now - lastStatusScan > Config.StatusIntervalSeconds) BeginStatusScan();
            if (Settings.ShowStatus && now - lastKeyboardScan > .12)
            {
                lastKeyboardScan = now;
                if (!MenuOpen)
                {
                    IntPtr window;
                    IntPtr layout = SystemStatus.ForegroundLayout(out window);
                    if (window != Handle && window != IntPtr.Zero)
                    {
                        InputWindow = window;
                        if (layout != InputLayout) { InputLayout = layout; RefreshStatusIcons(); }
                    }
                }
            }
            double delta = Math.Min(.05, Math.Max(.001, now - lastTime));
            lastTime = now;
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            Point point = new Point(cursor.X / DpiX - Left, cursor.Y / DpiY - Top);
            if (now - lastWindowCheck > Config.CompactCheckSeconds)
            {
                coveringWindow = Shell.ShouldCompact(Monitor.Bounds, Monitor.WorkingArea, Handle);
                lastWindowCheck = now;
            }
            double baseWidth = Config.Padding * 2 + Items.Count * (IconSize + Config.Gap) - Config.Gap + Items.Count(i => i.DividerBefore) * Config.Gap;
            bool horizontal = Math.Abs(point.X - Width / 2) < Math.Max(baseWidth, Surface.Panel.Width) / 2 + Config.CompactPointerMargin;
            bool inside = Surface.PointerNearDock(point);
            bool edge = horizontal && point.Y >= Height - 5 && point.Y < Height + 1;
            if ((inside && visibility > .8) || edge || MenuOpen || Surface.Dragging) lastHover = now;
            bool show = forceUntil > now || MenuOpen || Surface.Dragging || !Settings.AutoHide || now - lastHover < Config.HideDelay;
            bool shouldCompress = Settings.AutoCompact && coveringWindow && forceUntil <= now && !MenuOpen && !Surface.Dragging
                && now - lastHover > Config.CompactReleaseDelay;
            bool shapeChanged = compression.Step(shouldCompress ? 1 : 0, delta);
            Surface.CompactProgress = compression.Position;
            Surface.CompactVelocity = compression.Velocity;
            double previous = visibility;
            visibility = Layout.Ease(visibility, show ? 1 : 0, delta, Config.HideSpeed);
            double visibleHeight = Layout.Lerp(IconSize + Config.PanelExtraHeight + Config.Edge, Config.CompactHeight + Config.CompactEdge, Config.Clamp(compression.Position, 0, 1));
            Surface.HideOffset = (1 - visibility) * (visibleHeight - Config.HiddenLip);
            Surface.Interactive = visibility > .7;
            bool hover = inside && visibility > .85 && !MenuOpen;
            bool changed = Math.Abs(previous - visibility) > .0001 || shapeChanged;
            double x = (Width - baseWidth) / 2 + Config.Padding;
            double virtualMouse = point.X;
            // Обратное отображение из расширенной строки в исходные позиции.
            if (hover && Surface.HasLayout)
            {
                for (int index = 0; index < Items.Count; index++)
                {
                    DockItem item = Items[index];
                    if (item.DividerBefore) x += Config.Gap;
                    double left = index == 0 ? Surface.Panel.Left : (Items[index - 1].Center + item.Center) / 2;
                    double right = index == Items.Count - 1 ? Surface.Panel.Right : (Items[index + 1].Center + item.Center) / 2;
                    if (point.X >= left && point.X <= right)
                    {
                        virtualMouse = x + IconSize / 2 + (point.X - item.Center) / Math.Max(1, right - left) * (IconSize + Config.Gap);
                        break;
                    }
                    x += IconSize + Config.Gap;
                }
            }
            x = (Width - baseWidth) / 2 + Config.Padding;
            foreach (DockItem item in Items)
            {
                if (item.DividerBefore) x += Config.Gap;
                double center = x + IconSize / 2;
                double target = 1 + (Settings.Magnification - 1) * Config.Clamp(1 - compression.Position, 0, 1)
                    * (hover ? Layout.Wave(virtualMouse - center, (IconSize + Config.Gap) * Config.InfluenceSlots) : 0);
                double before = item.Scale;
                item.Scale = Layout.Ease(item.Scale, target, delta, Config.SpringSpeed);
                if (Math.Abs(item.Scale - target) < .001) item.Scale = target;
                changed |= Math.Abs(before - item.Scale) > .0001;
                if (item.Bounce > 0) { item.Bounce = Math.Max(0, item.Bounce - delta); changed = true; }
                x += IconSize + Config.Gap;
            }
            changed |= Surface.SetPointer(point, hover, now);
            if (changed) Surface.InvalidateVisual();
        }

        public void ActivateItem(DockItem item, bool newInstance)
        {
            if (item == null) return;
            try
            {
                if (item.Kind == "preferences") { RequestPreferences(); return; }
                if (item.Kind == "overflow") { Surface.OpenOverflowMenu(); return; }
                if (item.Kind == "wifi")
                {
                    using (var uri = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("ms-availablenetworks"))
                        Shell.Open(new Pin("", uri == null ? "ms-settings:network-wifi" : "ms-availablenetworks:"));
                    return;
                }
                if (item.Kind == "bluetooth") { Shell.Open(new Pin("", "ms-settings:bluetooth")); return; }
                if (item.Kind == "language") { Surface.OpenLanguageMenu(); return; }
                if (item.Kind == "apps") { Shell.Open(new Pin("", "shell:AppsFolder")); return; }
                if (item.Kind == "bin") { Shell.Open(new Pin("", "shell:RecycleBinFolder")); return; }
                if (!newInstance && item.Windows.Count > 0)
                {
                    if (item.Windows.Count > 1) { Surface.OpenMenu(item, true); return; }
                    Shell.Activate(item.Windows[0]);
                }
                else if (item.Pin != null) Shell.Open(item.Pin);
                else if (!String.IsNullOrEmpty(item.Path)) Shell.Open(new Pin(item.Name, item.Path));
                item.Bounce = .65;
            }
            catch (Exception ex) { Report("Не удалось открыть «" + item.Name + "»: " + ex.Message, ex); }
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if ((!File.Exists(path) && !Directory.Exists(path)) || Settings.Pins.Count >= Config.MaxPins) continue;
                if (Settings.Pins.Any(p => String.Equals(p.Target, path, StringComparison.OrdinalIgnoreCase))) continue;
                Settings.Pins.Add(Shell.MakePin(path));
            }
            ApplySettings();
        }

        public void ChooseFiles()
        {
            MenuOpen = true;
            try
            {
                Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog {
                    Title = "Закрепить приложение или ярлык", Multiselect = true,
                    Filter = "Приложения и ярлыки|*.exe;*.lnk;*.url;*.appref-ms|Все файлы|*.*", CheckFileExists = true
                };
                if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
            }
            finally { MenuOpen = false; Reveal(); }
        }

        public void TogglePin(DockItem item)
        {
            if (item.Pinned) Settings.Pins.Remove(item.Pin);
            else if (!String.IsNullOrEmpty(item.Path) && Settings.Pins.Count < Config.MaxPins) Settings.Pins.Add(Shell.MakePin(item.Path));
            ApplySettings();
        }

        public void Reorder(DockItem sourceItem, DockItem target)
        {
            if (sourceItem == target || sourceItem == null || target == null || !sourceItem.Pinned || !target.Pinned) return;
            int destination = Settings.Pins.IndexOf(target.Pin);
            Settings.Pins.Remove(sourceItem.Pin);
            Settings.Pins.Insert(destination, sourceItem.Pin);
            ApplySettings();
        }

        public void Report(string message, Exception ex)
        {
            Log.Write(message, ex);
            if (Error != null) Error(message);
        }

        public void Cleanup()
        {
            if (disposed) return;
            disposed = true;
            frameTimer.Stop();
            scanTimer.Stop();
            Native.UnregisterHotKey(Handle, Config.HotkeyDock);
            Native.UnregisterHotKey(Handle, Config.HotkeySettings);
            taskbar.Dispose();
            if (source != null) source.RemoveHook(WndProc);
        }
    }

    internal sealed class DockSurface : FrameworkElement
    {
        private readonly DockWindow dock;
        private DockItem hovered;
        private DockItem pressed;
        private Point pressPoint;
        private Point pointer;
        private double hoverSince;
        private bool labelVisible;
        public bool Dragging;
        public bool Interactive = true;
        public bool HasLayout;
        public double HideOffset;
        public double CompactProgress;
        public double CompactVelocity;
        public Rect Panel;
        private readonly Typeface labelFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

        public DockSurface(DockWindow dock)
        {
            this.dock = dock;
            AllowDrop = true;
            Focusable = false;
            Cursor = Cursors.Arrow;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            MouseMove += OnMove;
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { if (Interactive) OpenMenu(Hit(e.GetPosition(this)), false); e.Handled = true; };
            MouseDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ChangedButton == MouseButton.Middle && Interactive) { dock.ActivateItem(Hit(e.GetPosition(this)), true); e.Handled = true; }
            };
            DragOver += delegate(object sender, DragEventArgs e) {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                dock.Reveal(); e.Handled = true;
            };
            Drop += delegate(object sender, DragEventArgs e) {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) dock.AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
                e.Handled = true;
            };
            LostMouseCapture += delegate { pressed = null; Dragging = false; InvalidateVisual(); };
        }

        public bool SetPointer(Point point, bool enabled, double now)
        {
            pointer = point;
            DockItem next = enabled && !Dragging ? Hit(point) : null;
            bool changed = next != hovered;
            if (changed) { hovered = next; hoverSince = now; labelVisible = false; }
            if (!labelVisible && hovered != null && now - hoverSince >= Config.LabelDelay) { labelVisible = true; changed = true; }
            Cursor = next != null ? Cursors.Hand : Cursors.Arrow;
            return changed || Dragging;
        }

        public DockItem Hit(Point point)
        {
            if (!PointerNearDock(point)) return null;
            for (int index = 0; index < dock.Items.Count; index++)
            {
                DockItem item = dock.Items[index];
                Rect hit = item.Bounds;
                double left = index == 0 ? Panel.Left : (dock.Items[index - 1].Center + item.Center) / 2;
                double right = index == dock.Items.Count - 1 ? Panel.Right : (dock.Items[index + 1].Center + item.Center) / 2;
                hit = new Rect(left, Math.Min(Panel.Top, hit.Top), right - left, Math.Max(Panel.Bottom, hit.Bottom) - Math.Min(Panel.Top, hit.Top));
                hit.Inflate(0, Config.CompactPointerMargin);
                if (hit.Contains(point)) return item;
            }
            return null;
        }

        public bool PointerNearDock(Point point)
        {
            if (!HasLayout) return false;
            Rect panel = Panel;
            panel.Inflate(Config.CompactPointerMargin, Config.CompactPointerMargin);
            panel.Height = Math.Max(panel.Height, ActualHeight - panel.Top);
            if (panel.Contains(point)) return true;
            foreach (DockItem item in dock.Items)
            {
                Rect icon = item.Bounds;
                icon.Inflate(Config.CompactPointerMargin, Config.CompactPointerMargin);
                if (icon.Contains(point)) return true;
            }
            return false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (dock.Items.Count == 0) return;
            bool light = dock.Settings.LightTheme;
            double size = dock.IconSize;
            double compact = Config.Clamp(CompactProgress, 0, 1);
            Panel = Layout.Arrange(dock.Items, size, ActualWidth, ActualHeight, CompactProgress, HideOffset);
            double panelBottom = Panel.Bottom;
            double radius = Math.Min(22, Panel.Height / 2);
            // Полупрозрачная поверхность и мягкая тень без захвата фона рабочего стола.
            for (int step = 5; step > 0; step--)
            {
                Rect shadow = Panel; shadow.Inflate(step * 1.6, step * 1.2); shadow.Offset(0, step * .6);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(step == 1 ? 13 : 4), 0, 0, 0)), null, shadow, radius + 1, radius + 1);
            }
            byte alpha = (byte)(dock.Settings.Opacity * 255);
            LinearGradientBrush fill = light
                ? new LinearGradientBrush(Color.FromArgb(alpha, 248, 250, 254), Color.FromArgb(alpha, 217, 224, 234), 90)
                : new LinearGradientBrush(Color.FromArgb(alpha, 55, 61, 73), Color.FromArgb(alpha, 27, 31, 40), 90);
            Pen border = new Pen(new SolidColorBrush(light ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(65, 255, 255, 255)), 1);
            dc.DrawGeometry(fill, border, SlimeOutline(Panel, Config.Clamp(CompactVelocity * .6, -3, 3)));
            IntPtr active = Native.GetForegroundWindow();
            DockItem previousItem = null;
            foreach (DockItem item in dock.Items)
            {
                if (item.DividerBefore && previousItem != null)
                {
                    double divider = (previousItem.Center + item.Center) / 2;
                    double inset = Math.Min(15, Panel.Height * .25);
                    dc.DrawLine(new Pen(new SolidColorBrush(light ? Color.FromArgb(65, 42, 50, 70) : Color.FromArgb(65, 240, 245, 255)), 1),
                        new Point(divider, Panel.Top + inset), new Point(divider, Panel.Bottom - inset));
                }
                if (item == hovered && !Dragging)
                {
                    Rect glow = item.Bounds; glow.Inflate(Layout.Lerp(4, 2, compact), Layout.Lerp(4, 2, compact));
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(light ? (byte)32 : (byte)19, 255, 255, 255)), null, glow, 13, 13);
                }
                dc.PushOpacity(Dragging && pressed == item ? .3 : 1);
                if (item.Kind == "language")
                {
                    FormattedText language = new FormattedText(SystemStatus.LanguageCode(dock.InputLayout), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, labelFace, Math.Max(9, item.Bounds.Height * 19 / 64),
                        light ? new SolidColorBrush(Color.FromRgb(42, 54, 70)) : new SolidColorBrush(Color.FromRgb(224, 233, 245)), dock.DpiY);
                    dc.DrawText(language, new Point(item.Center - language.Width / 2, item.Bounds.Top + (item.Bounds.Height - language.Height) / 2));
                }
                else dc.DrawImage(item.Icon, item.Bounds);
                dc.Pop();
                if (item.Windows.Count > 0)
                {
                    bool foreground = item.Windows.Any(w => w.Handle == active);
                    Brush dot = new SolidColorBrush(light ? Color.FromRgb(55, 67, 84) : (foreground ? Color.FromRgb(147, 227, 214) : Color.FromRgb(212, 221, 235)));
                    double dotRadius = Layout.Lerp(foreground ? 2.7 : 2, 1.1, compact);
                    dc.DrawEllipse(dot, null, new Point(item.Center, panelBottom - Layout.Lerp(7, 2.4, compact)), dotRadius, dotRadius);
                }
                previousItem = item;
            }
            HasLayout = true;
            if (labelVisible && hovered != null && !Dragging && Interactive && CompactProgress < .2)
            {
                string name = hovered.Name;
                if (name.Length > 46) name = name.Substring(0, 43) + "…";
                FormattedText text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, labelFace, 12, light ? Brushes.Black : Brushes.White, dock.DpiY);
                double left = Math.Max(8, Math.Min(ActualWidth - text.Width - 24, hovered.Center - (text.Width + 22) / 2));
                Rect pill = new Rect(left, hovered.Bounds.Top - text.Height - 26, text.Width + 22, text.Height + 12);
                dc.DrawRoundedRectangle(new SolidColorBrush(light ? Color.FromArgb(246, 250, 251, 254) : Color.FromArgb(242, 35, 39, 49)),
                    new Pen(new SolidColorBrush(Color.FromArgb(50, 200, 210, 230)), 1), pill, 8, 8);
                dc.DrawText(text, new Point(pill.Left + 11, pill.Top + 5));
            }
            if (Dragging && pressed != null)
            {
                double draggedSize = size * 1.35;
                dc.DrawImage(pressed.Icon, new Rect(pointer.X - draggedSize / 2, pointer.Y - draggedSize / 2, draggedSize, draggedSize));
            }
        }

        private static Geometry SlimeOutline(Rect rect, double bend)
        {
            double radius = Math.Min(22, rect.Height / 2);
            double kappa = .55228475;
            double middle = rect.Left + rect.Width / 2;
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext path = geometry.Open())
            {
                path.BeginFigure(new Point(rect.Left + radius, rect.Top), true, true);
                path.BezierTo(new Point(middle - rect.Width * .2, rect.Top - bend), new Point(middle + rect.Width * .2, rect.Top + bend), new Point(rect.Right - radius, rect.Top), true, false);
                path.BezierTo(new Point(rect.Right - radius * (1 - kappa), rect.Top), new Point(rect.Right, rect.Top + radius * (1 - kappa)), new Point(rect.Right, rect.Top + radius), true, false);
                path.LineTo(new Point(rect.Right, rect.Bottom - radius), true, false);
                path.BezierTo(new Point(rect.Right, rect.Bottom - radius * (1 - kappa)), new Point(rect.Right - radius * (1 - kappa), rect.Bottom), new Point(rect.Right - radius, rect.Bottom), true, false);
                path.BezierTo(new Point(middle + rect.Width * .2, rect.Bottom - bend), new Point(middle - rect.Width * .2, rect.Bottom + bend), new Point(rect.Left + radius, rect.Bottom), true, false);
                path.BezierTo(new Point(rect.Left + radius * (1 - kappa), rect.Bottom), new Point(rect.Left, rect.Bottom - radius * (1 - kappa)), new Point(rect.Left, rect.Bottom - radius), true, false);
                path.LineTo(new Point(rect.Left, rect.Top + radius), true, false);
                path.BezierTo(new Point(rect.Left, rect.Top + radius * (1 - kappa)), new Point(rect.Left + radius * (1 - kappa), rect.Top), new Point(rect.Left + radius, rect.Top), true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (!Interactive) return;
            pressed = Hit(e.GetPosition(this));
            pressPoint = e.GetPosition(this);
            if (pressed != null) CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (pressed != null && pressed.Pinned && e.LeftButton == MouseButtonState.Pressed
                && (e.GetPosition(this) - pressPoint).Length > 7) Dragging = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            DockItem original = pressed;
            DockItem target = Hit(e.GetPosition(this));
            bool wasDragging = Dragging;
            ReleaseMouseCapture();
            pressed = null;
            Dragging = false;
            if (original != null)
            {
                if (wasDragging) dock.Reorder(original, target);
                else if (target == original || (e.GetPosition(this) - pressPoint).Length < 8)
                    dock.ActivateItem(original, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            }
            e.Handled = true;
        }

        private MenuItem Menu(ContextMenu menu, string name, Action action)
        {
            MenuItem item = new MenuItem { Header = name };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
            return item;
        }

        public void OpenMenu(DockItem item, bool windowsOnly)
        {
            if (item != null && item.Kind == "language") { OpenLanguageMenu(); return; }
            ContextMenu menu = new ContextMenu();
            dock.MenuOpen = true;
            menu.Closed += delegate { dock.MenuOpen = false; dock.Reveal(); };
            if (item != null)
            {
                if (item.Windows.Count > 0)
                {
                    foreach (WindowInfo window in item.Windows)
                    {
                        WindowInfo selected = window;
                        string title = selected.Title.Length > 65 ? selected.Title.Substring(0, 62) + "…" : selected.Title;
                        Menu(menu, title.Replace("_", "__"), () => Shell.Activate(selected));
                    }
                    menu.Items.Add(new Separator());
                }
                if (item.Kind == "app")
                {
                    Menu(menu, "Открыть новое окно", () => dock.ActivateItem(item, true));
                    if (item.Pinned || !String.IsNullOrEmpty(item.Path)) Menu(menu, item.Pinned ? "Убрать из закреплённых" : "Закрепить в доке", () => dock.TogglePin(item));
                }
                else Menu(menu, "Открыть", () => dock.ActivateItem(item, false));
                menu.Items.Add(new Separator());
            }
            Menu(menu, "Добавить приложение…", dock.ChooseFiles);
            Menu(menu, "Настройки…", dock.RequestPreferences).InputGestureText = "Ctrl+Alt+S";
            if (dock.Settings.HideTaskbar) Menu(menu, "Вернуть панель Windows", dock.RestoreTaskbar);
            menu.Items.Add(new Separator());
            Menu(menu, "Выйти из Harbor Dock", dock.RequestExit);
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        public void OpenLanguageMenu()
        {
            IntPtr target = dock.InputWindow;
            IntPtr current = dock.InputLayout;
            ContextMenu menu = new ContextMenu();
            dock.MenuOpen = true;
            menu.Closed += delegate { dock.MenuOpen = false; dock.Reveal(); };
            foreach (KeyboardChoice choice in SystemStatus.Keyboards())
            {
                KeyboardChoice selected = choice;
                string label = (choice.Layout == current ? "✓   " : "     ") + choice.Name;
                Menu(menu, label, delegate {
                    if (!SystemStatus.SelectKeyboard(target, selected.Layout))
                        dock.Report("Раскладка не переключена. Выбери активное окно и попробуй снова.", new InvalidOperationException("Keyboard request rejected"));
                    else Native.SetForegroundWindow(target);
                });
            }
            menu.Items.Add(new Separator());
            Menu(menu, "Параметры языка…", () => Shell.Open(new Pin("", "ms-settings:regionlanguage")));
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        public void OpenOverflowMenu()
        {
            ContextMenu menu = new ContextMenu();
            dock.MenuOpen = true;
            menu.Closed += delegate { dock.MenuOpen = false; dock.Reveal(); };
            foreach (DockItem hidden in dock.Overflow)
            {
                DockItem selected = hidden;
                Menu(menu, (selected.Windows.Count > 0 ? "• " : "") + selected.Name.Replace("_", "__"), () => dock.ActivateItem(selected, false));
            }
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }
    }
}
