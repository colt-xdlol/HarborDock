using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace HarborDock
{
    internal static class Config
    {
        public const string Product = "Harbor Dock";
        public const string Version = "1.2.0";
        public const string DirectoryName = "HarborDock";
        public const string StartupValue = "HarborDock";
        public const double DefaultSize = 48;
        public const double DefaultZoom = 1.85;
        public const double DefaultOpacity = .78;
        public const double MinSize = 28;
        public const double MaxSize = 72;
        public const double MinZoom = 1;
        public const double MaxZoom = 2.2;
        public const double Gap = 12;
        public const double Padding = 19;
        public const double Edge = 12;
        public const double WindowHeight = 224;
        public const double PanelExtraHeight = 25;
        public const double IndicatorOffset = 9;
        public const double InfluenceSlots = 2.35;
        public const double SpringSpeed = 18;
        public const double HideSpeed = 12;
        public const double HideDelay = .85;
        public const double LabelDelay = .25;
        public const double HiddenLip = 3;
        public const double FrameIntervalMs = 16;
        public const double ScanIntervalSeconds = 1.2;
        public const int GuardIntervalMs = 400;
        public const int GuardWaitMs = 4000;
        public const int IconPixels = 128;
        public const int MaxPins = 40;
        public const int MaxRunning = 24;
        public const int FullscreenTolerance = 2;
        public const int HotkeyDock = 4201;
        public const int HotkeySettings = 4202;
        public const int LogLimit = 65536;
        public const double StatusIntervalSeconds = 2;
        public const double RadioRefreshSeconds = 30;
        public const int StatusTimeoutMs = 2000;
        public const double CompactHeight = 28;
        public const double CompactIconSize = 20;
        public const double CompactEdge = 4;
        public const double CompactStretch = 1.14;
        public const double CompactReleaseDelay = .65;
        public const double CompactPointerMargin = 5;
        public const double CompactCheckSeconds = .15;
        public const double ElasticFrequency = 16;
        public const double ElasticDamping = .66;
        public const double ElasticPositionEpsilon = .0008;
        public const double ElasticVelocityEpsilon = .008;
        public const double ShadowMargin = 12;
        public static string DataDirectory;
        public static string SettingsPath { get { return Path.Combine(DataDirectory, "settings.json"); } }
        public static double Clamp(double value, double min, double max)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return min;
            return Math.Max(min, Math.Min(max, value));
        }
    }

    [DataContract]
    public sealed class Pin
    {
        [DataMember] public string Name = "";
        [DataMember] public string Target = "";
        [DataMember] public string Arguments = "";
        [DataMember] public string IconPath = "";
        [DataMember] public string MatchPath = "";
        public Pin() { }
        public Pin(string name, string target) { Name = name; Target = target; }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public double IconSize = Config.DefaultSize;
        [DataMember] public double Magnification = Config.DefaultZoom;
        [DataMember] public double Opacity = Config.DefaultOpacity;
        [DataMember] public bool LightTheme;
        [DataMember] public bool AutoHide;
        [DataMember] public bool HideTaskbar;
        [DataMember] public bool ShowRunning = true;
        [DataMember] public bool ShowStatus = true;
        [DataMember] public bool AutoCompact = true;
        [DataMember] public string Monitor = "";
        [DataMember] public List<Pin> Pins = new List<Pin>();

        [OnDeserializing]
        private void BeforeDeserialize(StreamingContext context) { ShowStatus = true; AutoCompact = true; }

        public void Validate()
        {
            IconSize = Config.Clamp(IconSize, Config.MinSize, Config.MaxSize);
            Magnification = Config.Clamp(Magnification, Config.MinZoom, Config.MaxZoom);
            Opacity = Config.Clamp(Opacity, .35, .95);
            if (Pins == null) Pins = new List<Pin>();
            Pins = Pins.Where(p => p != null && !String.IsNullOrWhiteSpace(p.Target))
                .GroupBy(p => p.Target + "\n" + p.Arguments, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).Take(Config.MaxPins).ToList();
            foreach (Pin pin in Pins)
            {
                if (String.IsNullOrWhiteSpace(pin.Name)) pin.Name = Path.GetFileNameWithoutExtension(pin.Target);
                if (pin.Name.Length > 120) pin.Name = pin.Name.Substring(0, 120);
            }
        }

        public static Settings Load()
        {
            if (!File.Exists(Config.SettingsPath)) return Defaults();
            try
            {
                using (FileStream stream = File.OpenRead(Config.SettingsPath))
                {
                    Settings value = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    value.Validate();
                    return value;
                }
            }
            catch (Exception ex)
            {
                Log.Write("settings load", ex);
                try { File.Copy(Config.SettingsPath, Config.SettingsPath + ".invalid", true); } catch { }
                return Defaults();
            }
        }

        public void Save()
        {
            Validate();
            Directory.CreateDirectory(Config.DataDirectory);
            string temp = Config.SettingsPath + ".tmp";
            using (FileStream stream = File.Create(temp))
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
            if (File.Exists(Config.SettingsPath)) File.Replace(temp, Config.SettingsPath, null);
            else File.Move(temp, Config.SettingsPath);
        }

        public static Settings Defaults()
        {
            Settings settings = new Settings();
            settings.Pins.Add(new Pin("Проводник", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
            string browser = Shell.FindBrowser();
            if (!String.IsNullOrEmpty(browser)) settings.Pins.Add(new Pin("Браузер", browser));
            settings.Pins.Add(new Pin("Блокнот", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")));
            string downloads = Shell.DownloadsPath();
            if (Directory.Exists(downloads)) settings.Pins.Add(new Pin("Загрузки", downloads));
            return settings;
        }
    }

    internal sealed class DockItem
    {
        public string Key;
        public string Name;
        public Pin Pin;
        public string Path;
        public string Kind;
        public ImageSource Icon;
        public List<WindowInfo> Windows = new List<WindowInfo>();
        public bool Pinned;
        public bool DividerBefore;
        public double Scale = 1;
        public double Bounce;
        public Rect Bounds;
        public double Center;
    }

    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string Path;
        public uint ProcessId;
    }

    internal static class Layout
    {
        public static double Lerp(double from, double to, double amount) { return from + (to - from) * amount; }

        public static Rect Arrange(IList<DockItem> items, double size, double viewportWidth, double viewportHeight, double compact, double hideOffset)
        {
            double progress = Config.Clamp(compact, -.08, 1.08);
            double regularWidth = Width(items, size);
            double width = Math.Min(viewportWidth - Config.ShadowMargin * 2, regularWidth * Lerp(1, Config.CompactStretch, progress));
            double horizontalScale = width / Math.Max(1, regularWidth);
            double panelHeight = Lerp(size + Config.PanelExtraHeight, Config.CompactHeight, progress);
            double panelBottom = viewportHeight - Lerp(Config.Edge, Config.CompactEdge, progress) + hideOffset;
            Rect panel = new Rect((viewportWidth - width) / 2, panelBottom - panelHeight, width, panelHeight);
            double x = Config.Padding;
            double baseline = panelBottom - Lerp(16, 5, progress);
            double iconFactor = Lerp(1, Math.Min(size, Config.CompactIconSize) / Math.Max(1, size), progress);
            foreach (DockItem item in items)
            {
                if (item.DividerBefore) x += Config.Gap;
                double regularSize = size * item.Scale;
                double iconSize = regularSize * iconFactor;
                double center = panel.Left + (x + regularSize / 2) * horizontalScale;
                double bounce = item.Bounce > 0 ? Math.Abs(Math.Sin((.65 - item.Bounce) * Math.PI * 3 / .65)) * item.Bounce / .65 * 14 : 0;
                bounce *= Config.Clamp(1 - progress, 0, 1);
                item.Center = center;
                item.Bounds = new Rect(center - iconSize / 2, baseline - iconSize - bounce, iconSize, iconSize);
                x += regularSize + Config.Gap;
            }
            return panel;
        }

        public static double Wave(double distance, double radius)
        {
            if (radius <= 0 || Math.Abs(distance) >= radius) return 0;
            return (1 + Math.Cos(Math.PI * distance / radius)) * .5;
        }

        public static double Ease(double current, double target, double delta, double speed)
        {
            return current + (target - current) * (1 - Math.Exp(-speed * delta));
        }

        public static double FitSize(int count, int separators, double desired, double zoom, double available)
        {
            double reserve = Config.Padding * 2 + separators * Config.Gap + desired * (zoom - 1) * 3;
            return Math.Max(12, Math.Min(desired, (available - reserve) / Math.Max(1, count) - Config.Gap));
        }

        public static double Width(IList<DockItem> items, double size)
        {
            double width = Config.Padding * 2;
            foreach (DockItem item in items)
                width += size * item.Scale + Config.Gap + (item.DividerBefore ? Config.Gap : 0);
            return width - (items.Count > 0 ? Config.Gap : 0);
        }
    }

    internal sealed class ElasticMotion
    {
        public double Position;
        public double Velocity;

        public bool Step(double target, double delta)
        {
            if (delta <= 0) return false;
            double before = Position;
            // Точное решение затухающей пружины сохраняет скорость при смене направления.
            double omega = Config.ElasticFrequency;
            double damping = omega * Config.ElasticDamping;
            double frequency = omega * Math.Sqrt(1 - Config.ElasticDamping * Config.ElasticDamping);
            double offset = Position - target;
            double cosine = Math.Cos(frequency * delta);
            double sine = Math.Sin(frequency * delta);
            double decay = Math.Exp(-damping * delta);
            Position = target + decay * (offset * cosine + (Velocity + damping * offset) / frequency * sine);
            Velocity = decay * (Velocity * cosine - (damping * Velocity + omega * omega * offset) / frequency * sine);
            if (Math.Abs(Position - target) < Config.ElasticPositionEpsilon && Math.Abs(Velocity) < Config.ElasticVelocityEpsilon)
            {
                Position = target;
                Velocity = 0;
            }
            return Math.Abs(before - Position) > .00001 || Velocity != 0;
        }
    }

    internal static class Log
    {
        public static void Write(string operation, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Config.DataDirectory);
                string path = System.IO.Path.Combine(Config.DataDirectory, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > Config.LogLimit) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + operation + " " + exception + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
