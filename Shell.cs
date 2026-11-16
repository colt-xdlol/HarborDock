using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace HarborDock
{
    internal static class Shell
    {
        private static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static string FindBrowser()
        {
            try
            {
                using (RegistryKey choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                {
                    string id = choice == null ? "" : choice.GetValue("ProgId", "").ToString();
                    using (RegistryKey command = Registry.ClassesRoot.OpenSubKey(id + @"\shell\open\command"))
                    {
                        string value = command == null ? "" : command.GetValue(null, "").ToString();
                        string path = value.StartsWith("\"") ? value.Split('"')[1] : value.Split(' ')[0];
                        if (File.Exists(path)) return path;
                    }
                }
            }
            catch { }
            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        public static string DownloadsPath()
        {
            Guid id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            IntPtr value = IntPtr.Zero;
            try
            {
                if (Native.SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out value) == 0)
                    return Marshal.PtrToStringUni(value);
            }
            finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static Pin MakePin(string path)
        {
            Pin pin = new Pin(Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar)), path);
            pin.MatchPath = path;
            if (String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                object shell = null, shortcut = null;
                try
                {
                    Type type = Type.GetTypeFromProgID("WScript.Shell");
                    shell = Activator.CreateInstance(type);
                    shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    pin.MatchPath = (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                }
                catch { }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
                }
            }
            return pin;
        }

        public static ImageSource Icon(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return GlyphIcon("\uE8A5", Color.FromRgb(74, 130, 198));
            ImageSource cached;
            if (icons.TryGetValue(path, out cached)) return cached;
            ImageSource result = null;
            Native.IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid id = typeof(Native.IShellItemImageFactory).GUID;
                Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out factory);
                Native.SIZE size = new Native.SIZE { Width = Config.IconPixels, Height = Config.IconPixels };
                if (factory.GetImage(size, 5, out bitmap) == 0 && bitmap != IntPtr.Zero)
                {
                    result = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                }
            }
            catch { }
            finally
            {
                if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            if (result == null)
            {
                Native.SHFILEINFO info;
                if (Native.SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_ICON) != IntPtr.Zero)
                {
                    try
                    {
                        result = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        result.Freeze();
                    }
                    finally { if (info.Icon != IntPtr.Zero) Native.DestroyIcon(info.Icon); }
                }
            }
            if (result == null) result = GlyphIcon("\uE8A5", Color.FromRgb(74, 130, 198));
            // Кэш ограничен: закрытые приложения не накапливают изображения бесконечно.
            if (icons.Count > 256) icons.Clear();
            icons[path] = result;
            return result;
        }

        public static ImageSource GlyphIcon(string glyph, Color color)
        {
            DrawingGroup group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
            {
                LinearGradientBrush fill = new LinearGradientBrush(color, Color.FromRgb((byte)(color.R * .65), (byte)(color.G * .65), (byte)(color.B * .65)), 90);
                dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), 1), new Rect(3, 3, 58, 58), 14, 14);
                FormattedText text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe MDL2 Assets"), 29, Brushes.White, 1);
                dc.DrawText(text, new Point((64 - text.Width) / 2, (64 - text.Height) / 2));
            }
            DrawingImage image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        public static List<WindowInfo> ScanWindows()
        {
            List<WindowInfo> result = new List<WindowInfo>();
            Dictionary<uint, string> paths = new Dictionary<uint, string>();
            uint ownId = (uint)Process.GetCurrentProcess().Id;
            Native.EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                if (!Native.IsWindowVisible(window) || Native.GetWindow(window, Native.GW_OWNER) != IntPtr.Zero) return true;
                if ((Native.GetWindowLong(window, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                uint id;
                Native.GetWindowThreadProcessId(window, out id);
                if (id == ownId || id == 0) return true;
                string cls = Native.ClassName(window);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Progman" || cls == "WorkerW") return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(window, Native.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                StringBuilder title = new StringBuilder(512);
                Native.GetWindowText(window, title, title.Capacity);
                if (title.Length == 0) return true;
                string path;
                if (!paths.TryGetValue(id, out path)) { path = Native.ProcessPath(id); paths[id] = path; }
                result.Add(new WindowInfo { Handle = window, Title = title.ToString(), Path = path, ProcessId = id });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static bool IsFullscreen(System.Drawing.Rectangle screen, IntPtr own)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == own || foreground == IntPtr.Zero || Native.IsIconic(foreground)) return false;
            string name = Native.ClassName(foreground);
            if (name == "Progman" || name == "WorkerW") return false;
            Native.RECT rect;
            if (!Native.GetWindowRect(foreground, out rect)) return false;
            return Math.Abs(rect.Left - screen.Left) <= Config.FullscreenTolerance && Math.Abs(rect.Top - screen.Top) <= Config.FullscreenTolerance
                && Math.Abs(rect.Right - screen.Right) <= Config.FullscreenTolerance && Math.Abs(rect.Bottom - screen.Bottom) <= Config.FullscreenTolerance;
        }

        public static bool ShouldCompact(System.Drawing.Rectangle screen, System.Drawing.Rectangle workArea, IntPtr own)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == own || foreground == IntPtr.Zero || Native.IsIconic(foreground)) return false;
            string name = Native.ClassName(foreground);
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            Native.RECT rect;
            if (!Native.GetWindowRect(foreground, out rect)) return false;
            var window = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return CoversDisplay(screen, workArea, window, Native.IsZoomed(foreground));
        }

        public static bool CoversDisplay(System.Drawing.Rectangle screen, System.Drawing.Rectangle workArea, System.Drawing.Rectangle window, bool maximized)
        {
            var intersection = System.Drawing.Rectangle.Intersect(screen, window);
            if (intersection.Width <= 0 || intersection.Height <= 0) return false;
            bool centered = window.Contains(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            if (maximized && centered && window.Width >= workArea.Width * .9 && window.Height >= workArea.Height * .85) return true;
            int tolerance = Config.FullscreenTolerance;
            return window.Left <= workArea.Left + tolerance && window.Top <= workArea.Top + tolerance
                && window.Right >= workArea.Right - tolerance && window.Bottom >= workArea.Bottom - tolerance;
        }

        public static void Open(Pin pin)
        {
            ProcessStartInfo info = new ProcessStartInfo(pin.Target, pin.Arguments ?? "") { UseShellExecute = true };
            if (File.Exists(pin.Target)) info.WorkingDirectory = Path.GetDirectoryName(pin.Target);
            Process.Start(info);
        }

        public static void Activate(WindowInfo window)
        {
            if (!Native.IsWindow(window.Handle)) return;
            if (Native.IsIconic(window.Handle)) Native.ShowWindowAsync(window.Handle, Native.SW_RESTORE);
            Native.SetForegroundWindow(window.Handle);
        }

        public static bool StartupEnabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    string value = key == null ? "" : Convert.ToString(key.GetValue(Config.StartupValue, ""));
                    return String.Equals(value, StartupCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (value) key.SetValue(Config.StartupValue, StartupCommand, RegistryValueKind.String);
                    else if (StartupEnabled) key.DeleteValue(Config.StartupValue, false);
                }
            }
        }
        private static string StartupCommand { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --quiet"; } }
    }
}
