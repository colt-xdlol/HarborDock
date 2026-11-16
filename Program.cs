using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Harbor Dock")]
[assembly: AssemblyDescription("Desktop application dock")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace HarborDock
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--guard")
            {
                int parent;
                if (Int32.TryParse(args[1], out parent)) TaskbarLease.Watch(parent, args[2]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--guard-fixture")
            {
                using (TaskbarLease lease = new TaskbarLease())
                {
                    lease.Enable();
                    using (EventWaitHandle ready = EventWaitHandle.OpenExisting("Local\\HarborDock.Fixture." + args[1])) ready.Set();
                    Thread.Sleep(Timeout.Infinite);
                }
                return 0;
            }
            Config.DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Config.DirectoryName);
            int profile = Array.IndexOf(args, "--profile");
            if (profile >= 0 && profile + 1 < args.Length) Config.DataDirectory = Path.GetFullPath(args[profile + 1]);
            if (args.Length == 2 && args[0] == "--self-test") return SelfTest.Run(args[1]);
            if (args.Length == 2 && args[0] == "--guard-test") return SelfTest.Guard(args[1]);
            if (args.Length == 2 && args[0] == "--render-preview") return SelfTest.Render(args[1]);
            string suffix;
            using (SHA256 hash = SHA256.Create()) suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Config.DataDirectory.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\HarborDock.Instance." + suffix, out created))
            {
                if (!created)
                {
                    try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\HarborDock.Show." + suffix)) signal.Set(); } catch { }
                    return 0;
                }
                try
                {
                    using (EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\HarborDock.Show." + suffix))
                        new DockApplication(signal, args.Contains("--quiet")).Run();
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Write("fatal", ex);
                    MessageBox.Show("Не удалось запустить Harbor Dock.\n\n" + ex.Message, Config.Product, MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class DockApplication : Application
    {
        private readonly EventWaitHandle showSignal;
        private readonly bool quiet;
        private DockWindow dock;
        private PreferencesWindow preferences;
        private Forms.NotifyIcon tray;
        private DispatcherTimer signalTimer;

        public DockApplication(EventWaitHandle signal, bool quiet)
        {
            showSignal = signal;
            this.quiet = quiet;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                Log.Write("UI", e.Exception);
                if (dock != null) dock.Cleanup();
            };
            SessionEnding += delegate { if (dock != null) dock.Cleanup(); };
            Exit += delegate {
                if (signalTimer != null) signalTimer.Stop();
                if (dock != null) dock.Cleanup();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(MenuResources));
            bool first = !File.Exists(Config.SettingsPath);
            Settings settings = Settings.Load();
            dock = new DockWindow(settings);
            MainWindow = dock;
            dock.PreferencesRequested = ShowPreferences;
            dock.ExitRequested = ExitApp;
            dock.Error = ShowError;
            dock.Show();
            CreateTray();
            signalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            signalTimer.Tick += delegate { if (showSignal.WaitOne(0)) ShowPreferences(); };
            signalTimer.Start();
            if (first && !quiet) Dispatcher.BeginInvoke(new Action(ShowPreferences), DispatcherPriority.ApplicationIdle);
        }

        private void CreateTray()
        {
            tray = new Forms.NotifyIcon();
            tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            tray.Text = Config.Product;
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("Показать док", null, delegate { dock.Reveal(); });
            menu.Items.Add("Настройки…", null, delegate { ShowPreferences(); });
            menu.Items.Add("Добавить приложение…", null, delegate { dock.ChooseFiles(); });
            menu.Items.Add("Вернуть панель Windows", null, delegate { dock.RestoreTaskbar(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выйти", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowPreferences(); };
            tray.Visible = true;
        }

        private void ShowPreferences()
        {
            dock.Reveal();
            if (preferences == null)
            {
                preferences = new PreferencesWindow(dock);
                preferences.Closed += delegate { preferences = null; };
                preferences.Show();
            }
            else
            {
                if (preferences.WindowState == WindowState.Minimized) preferences.WindowState = WindowState.Normal;
                preferences.Activate();
            }
        }

        private void ExitApp()
        {
            if (preferences != null) preferences.Close();
            dock.Cleanup();
            dock.Close();
            Shutdown();
        }

        private void ShowError(string message)
        {
            if (tray != null)
            {
                tray.BalloonTipTitle = Config.Product;
                tray.BalloonTipText = message;
                tray.BalloonTipIcon = Forms.ToolTipIcon.Warning;
                tray.ShowBalloonTip(5000);
            }
        }

        private const string MenuResources = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ContextMenu'><Setter Property='Background' Value='#F1262F3E'/><Setter Property='Foreground' Value='#EDF3FA'/><Setter Property='BorderBrush' Value='#526176'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='5'/><Setter Property='FontFamily' Value='Segoe UI'/><Setter Property='FontSize' Value='12'/></Style>
  <Style TargetType='MenuItem'><Setter Property='Padding' Value='12,7'/><Setter Property='Foreground' Value='#EDF3FA'/><Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='MenuItem'><Border x:Name='B' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}'>
      <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
        <ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/><TextBlock Grid.Column='1' Text='{TemplateBinding InputGestureText}' Margin='24,0,0,0' Foreground='#9DAEC4'/>
      </Grid></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='B' Property='Background' Value='#445870'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter></Style>
  <Style TargetType='Separator'><Setter Property='Margin' Value='5,4'/><Setter Property='Background' Value='#526176'/></Style>
</ResourceDictionary>";
    }

    internal static class SelfTest
    {
        public static int Guard(string output)
        {
            List<string> lines = new List<string>();
            int failures = 0;
            Action<string, bool> check = (name, pass) => { lines.Add((pass ? "PASS " : "FAIL ") + name); if (!pass) failures++; };
            var initial = Native.Taskbars().ToDictionary(h => h, h => Native.IsWindowVisible(h));
            check("visible taskbar baseline", initial.Count > 0 && initial.Values.Any(v => v));
            using (TaskbarLease lease = new TaskbarLease())
            {
                lease.Enable();
                Thread.Sleep(Config.GuardIntervalMs * 3);
                check("guard started", lease.Active);
                check("taskbar hidden", initial.Keys.All(h => !Native.IsWindowVisible(h)));
            }
            check("taskbar restored on dispose", initial.All(p => Native.IsWindowVisible(p.Key) == p.Value));
            string token = Guid.NewGuid().ToString("N");
            using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\HarborDock.Fixture." + token))
            using (Process fixture = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard-fixture " + token) { UseShellExecute = false, CreateNoWindow = true }))
            {
                bool started = ready.WaitOne(Config.GuardWaitMs);
                check("crash fixture started", started);
                Thread.Sleep(Config.GuardIntervalMs * 3);
                check("fixture taskbar hidden", initial.Keys.All(h => !Native.IsWindowVisible(h)));
                fixture.Kill();
                fixture.WaitForExit(Config.GuardWaitMs);
                Thread.Sleep(Config.GuardIntervalMs * 3);
                check("taskbar restored after parent crash", initial.All(p => Native.IsWindowVisible(p.Key) == p.Value));
            }
            lines.Add("failures: " + failures);
            File.WriteAllLines(output, lines.ToArray(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        public static int Render(string directory)
        {
            Directory.CreateDirectory(directory);
            Settings settings = Settings.Defaults();
            using (MemoryStream unused = new MemoryStream())
            {
                DockWindow dock = new DockWindow(settings, true);
                dock.PreparePreview();
                SaveVisual(dock.Surface, (int)dock.Width, (int)dock.Height, Path.Combine(directory, "dock-rest.png"));
                double x = dock.Width / 2;
                foreach (DockItem item in dock.Items)
                    item.Scale = 1 + (settings.Magnification - 1) * Layout.Wave(item.Center - x, (dock.IconSize + Config.Gap) * Config.InfluenceSlots);
                dock.Surface.InvalidateVisual();
                dock.Surface.UpdateLayout();
                SaveVisual(dock.Surface, (int)dock.Width, (int)dock.Height, Path.Combine(directory, "dock-hover.png"));
                foreach (DockItem item in dock.Items) item.Scale = 1;
                dock.Surface.CompactProgress = 1;
                dock.Surface.CompactVelocity = 0;
                dock.Surface.InvalidateVisual();
                dock.Surface.UpdateLayout();
                SaveVisual(dock.Surface, (int)dock.Width, (int)dock.Height, Path.Combine(directory, "dock-compact.png"));
                DrawingVisual strip = new DrawingVisual();
                ElasticMotion motion = new ElasticMotion();
                using (DrawingContext drawing = strip.RenderOpen())
                {
                    for (int frame = 0; frame < 8; frame++)
                    {
                        for (int step = 0; step < 6; step++) motion.Step(1, 1.0 / 120);
                        dock.Surface.CompactProgress = motion.Position;
                        dock.Surface.CompactVelocity = motion.Velocity;
                        dock.Surface.InvalidateVisual();
                        dock.Surface.UpdateLayout();
                        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dock.Width, (int)dock.Height, 96, 96, PixelFormats.Pbgra32);
                        image.Render(dock.Surface);
                        drawing.PushClip(new RectangleGeometry(new Rect(0, frame * 96, dock.Width, 96)));
                        drawing.DrawImage(image, new Rect(0, frame * 96 - 128, dock.Width, dock.Height));
                        drawing.Pop();
                    }
                }
                SaveVisual(strip, (int)dock.Width, 8 * 96, Path.Combine(directory, "elastic-frames.png"));
                PreferencesWindow preferences = new PreferencesWindow(dock);
                FrameworkElement root = (FrameworkElement)preferences.Content;
                root.Measure(new Size(610, 790));
                root.Arrange(new Rect(0, 0, 610, 790));
                root.UpdateLayout();
                SaveVisual(root, 610, 790, Path.Combine(directory, "preferences.png"));
                dock.Cleanup();
            }
            return 0;
        }

        private static void SaveVisual(Visual visual, int width, int height, string path)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }

        public static int Run(string output)
        {
            List<string> lines = new List<string>();
            int failures = 0;
            Action<string, bool> check = (name, pass) => { lines.Add((pass ? "PASS " : "FAIL ") + name); if (!pass) failures++; };
            try
            {
                check("wave peak", Math.Abs(Layout.Wave(0, 100) - 1) < .00001);
                check("wave boundary", Layout.Wave(100, 100) == 0 && Layout.Wave(101, 100) == 0);
                check("wave symmetry", Math.Abs(Layout.Wave(33, 100) - Layout.Wave(-33, 100)) < .00001);
                check("wave monotonic", Layout.Wave(10, 100) > Layout.Wave(50, 100));
                double sixty = 1, thirty = 1;
                for (int i = 0; i < 60; i++) sixty = Layout.Ease(sixty, 2, 1.0 / 60, Config.SpringSpeed);
                for (int i = 0; i < 30; i++) thirty = Layout.Ease(thirty, 2, 1.0 / 30, Config.SpringSpeed);
                check("frame-rate independent animation", Math.Abs(sixty - thirty) < .000001 && sixty < 2 && sixty > 1.999);
                ElasticMotion spring60 = new ElasticMotion(), spring30 = new ElasticMotion();
                for (int i = 0; i < 18; i++) spring60.Step(1, 1.0 / 60);
                for (int i = 0; i < 9; i++) spring30.Step(1, 1.0 / 30);
                check("elastic frame-rate independence", Math.Abs(spring60.Position - spring30.Position) < .000001 && Math.Abs(spring60.Velocity - spring30.Velocity) < .000001);
                ElasticMotion settling = new ElasticMotion();
                double peak = 0;
                for (int i = 0; i < 240; i++) { settling.Step(1, 1.0 / 120); peak = Math.Max(peak, settling.Position); }
                check("slime overshoot and settle", peak > 1.02 && peak < 1.08 && settling.Position == 1 && settling.Velocity == 0);
                bool stable = true;
                for (int i = 0; i < 1200; i++)
                {
                    settling.Step((i / 17) % 2 == 0 ? 0 : 1, 1.0 / 120);
                    stable &= !Double.IsNaN(settling.Position) && settling.Position > -.2 && settling.Position < 1.2;
                }
                check("rapid elastic reversal stable", stable);
                var screen = new System.Drawing.Rectangle(0, 0, 1920, 724);
                var workArea = new System.Drawing.Rectangle(0, 0, 1920, 684);
                check("fullscreen detection", Shell.CoversDisplay(screen, workArea, screen, false));
                check("maximized detection with invisible resize borders", Shell.CoversDisplay(screen, workArea, new System.Drawing.Rectangle(-8, -8, 1936, 700), true));
                check("work-area sized borderless window", Shell.CoversDisplay(screen, workArea, workArea, false));
                check("ordinary window stays expanded", !Shell.CoversDisplay(screen, workArea, new System.Drawing.Rectangle(200, 80, 1000, 500), false));
                check("snapped half-screen stays expanded", !Shell.CoversDisplay(screen, workArea, new System.Drawing.Rectangle(0, 0, 960, 684), false));
                check("maximized on another monitor ignored", !Shell.CoversDisplay(screen, workArea, new System.Drawing.Rectangle(-1920, 0, 1920, 724), true));
                check("negative-origin display supported", Shell.CoversDisplay(new System.Drawing.Rectangle(-1920, 0, 1920, 1080), new System.Drawing.Rectangle(-1920, 0, 1920, 1040), new System.Drawing.Rectangle(-1928, -8, 1936, 1056), true));
                Settings corrupt = new Settings { IconSize = Double.NaN, Magnification = 100, Opacity = -4, Pins = null };
                corrupt.Validate();
                check("invalid settings normalized", corrupt.IconSize == Config.MinSize && corrupt.Magnification == Config.MaxZoom && corrupt.Opacity == .35 && corrupt.Pins.Count == 0);
                Settings roundtrip = Settings.Defaults();
                using (MemoryStream stream = new MemoryStream())
                {
                    var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(Settings));
                    serializer.WriteObject(stream, roundtrip); stream.Position = 0;
                    Settings decoded = (Settings)serializer.ReadObject(stream);
                    check("settings JSON roundtrip", decoded.Pins.Count == roundtrip.Pins.Count && decoded.Pins[0].Name == roundtrip.Pins[0].Name);
                }
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Pins\":[],\"ShowStatus\":true}")))
                {
                    var legacy = (Settings)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    check("existing settings enable compact mode", legacy.AutoCompact && legacy.ShowStatus);
                }
                ImageSource icon = Shell.Icon(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                check("native icon available", icon != null && icon.Width > 0 && icon.IsFrozen);
                check("running windows accessible", Shell.ScanWindows().Count > 0);
                check("taskbar discovered", Native.Taskbars().Count > 0);
                check("WLAN ABI layout", SystemStatus.ValidateLayouts());
                IntPtr inputWindow;
                IntPtr inputLayout = SystemStatus.ForegroundLayout(out inputWindow);
                check("foreground keyboard detected", inputLayout != IntPtr.Zero && SystemStatus.LanguageCode(inputLayout).Length > 0);
                check("installed keyboards detected", SystemStatus.Keyboards().Count > 0);
                using (Forms.Form fixture = new Forms.Form())
                {
                    IntPtr handle = fixture.Handle;
                    IntPtr original = SystemStatus.WindowLayout(handle);
                    var choices = SystemStatus.Keyboards();
                    var alternate = choices.FirstOrDefault(k => k.Layout != original) ?? choices.First();
                    bool posted = SystemStatus.SelectKeyboard(handle, alternate.Layout);
                    Forms.Application.DoEvents();
                    check("keyboard switch on local fixture", posted && SystemStatus.WindowLayout(handle) == alternate.Layout);
                    SystemStatus.SelectKeyboard(handle, original);
                    Forms.Application.DoEvents();
                }
                StatusSnapshot status = TaskStatus();
                check("system status read", !String.IsNullOrEmpty(status.WifiName) && !String.IsNullOrEmpty(status.BluetoothName));
                check("status icons render", SystemStatus.Icon("wifi", "", status, false).Width == 64 && SystemStatus.Icon("language", "РУС", status, true).IsFrozen);
                lines.Add("wifi connected " + status.WifiConnected + "; signal " + status.Signal);
                lines.Add(status.BluetoothName);
                List<DockItem> items = Enumerable.Range(0, 10).Select(i => new DockItem { Scale = 1 }).ToList();
                double width = Layout.Width(items, 48);
                check("dock geometry", Math.Abs(width - (Config.Padding * 2 + 10 * 48 + 9 * Config.Gap)) < .001);
                items[5].Scale = 2;
                check("hover preserves spacing", Math.Abs(Layout.Width(items, 48) - width - 48) < .001);
                foreach (DockItem item in items) item.Scale = 1;
                Rect normal = Layout.Arrange(items, 39, 1400, 224, 0, 0);
                Rect compact = Layout.Arrange(items, 39, 1400, 224, 1, 0);
                check("compact longer and thinner", compact.Width > normal.Width && compact.Height == Config.CompactHeight && compact.Height < normal.Height / 2);
                check("compact icons fit without distortion", items.All(i => i.Bounds.Width == i.Bounds.Height && compact.Contains(i.Bounds)));
                check("compact icon spacing remains ordered", items.Zip(items.Skip(1), (a, b) => a.Bounds.Right < b.Bounds.Left).All(v => v));
                Rect limited = Layout.Arrange(items, 39, 700, 224, 1.08, 0);
                check("elastic overshoot stays within display", limited.Left >= Config.ShadowMargin && limited.Right <= 700 - Config.ShadowMargin && limited.Height > 0);
                var currentScreen = Forms.Screen.PrimaryScreen;
                lines.Add("foreground compact classification " + Shell.ShouldCompact(currentScreen.Bounds, currentScreen.WorkingArea, IntPtr.Zero));
                lines.Add("OS " + Environment.OSVersion.VersionString);
                lines.Add("CLR " + Environment.Version);
                lines.Add("render tier " + (RenderCapability.Tier >> 16));
            }
            catch (Exception ex) { failures++; lines.Add("FAIL unexpected " + ex); }
            lines.Add("failures: " + failures);
            File.WriteAllLines(Path.GetFullPath(output), lines.ToArray(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        private static StatusSnapshot TaskStatus()
        {
            return System.Threading.Tasks.Task.Run(() => SystemStatus.ReadAsync()).GetAwaiter().GetResult();
        }
    }
}
