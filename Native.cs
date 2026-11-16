using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HarborDock
{
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_MOUSEACTIVATE = 0x21;
        public const int WM_HOTKEY = 0x312;
        public const int WM_DPICHANGED = 0x2E0;
        public const int WM_DISPLAYCHANGE = 0x7E;
        public const int MA_NOACTIVATE = 3;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_RESTORE = 9;
        public const uint MOD_ALT = 1;
        public const uint MOD_CONTROL = 2;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint GA_ROOTOWNER = 3;
        public const uint GW_OWNER = 4;
        public const int DWMWA_CLOAKED = 14;
        public const uint SHGFI_ICON = 0x100;
        public const uint SHGFI_LARGEICON = 0;
        public const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        public const uint SHGFI_SYSICONINDEX = 0x4000;
        public const int SHIL_JUMBO = 4;
        public const uint ILD_TRANSPARENT = 1;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Width; public int Height; }
        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr bitmap);
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr value);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] public static extern void SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
        [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string ClassName(IntPtr window)
        {
            StringBuilder text = new StringBuilder(256);
            GetClassName(window, text, text.Capacity);
            return text.ToString();
        }
        public static string ProcessPath(uint id)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, id);
            if (process == IntPtr.Zero) return "";
            try
            {
                StringBuilder path = new StringBuilder(32768);
                int capacity = path.Capacity;
                return QueryFullProcessImageName(process, 0, path, ref capacity) ? path.ToString() : "";
            }
            finally { CloseHandle(process); }
        }

        public static List<IntPtr> Taskbars()
        {
            List<IntPtr> result = new List<IntPtr>();
            EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                string name = ClassName(window);
                if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") result.Add(window);
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    internal sealed class TaskbarLease : IDisposable
    {
        private Process guard;
        private EventWaitHandle stop;
        public bool Active { get { return guard != null && !guard.HasExited; } }

        public void Enable()
        {
            if (Active) return;
            Dispose();
            string token = Guid.NewGuid().ToString("N");
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\HarborDock.Stop." + token);
            using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\HarborDock.Ready." + token))
            {
                guard = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                    "--guard " + Process.GetCurrentProcess().Id + " " + token) { UseShellExecute = false, CreateNoWindow = true });
                if (!ready.WaitOne(Config.GuardWaitMs) || guard.HasExited)
                {
                    Dispose();
                    throw new InvalidOperationException("Не удалось запустить восстановление панели Windows.");
                }
            }
        }

        public void Dispose()
        {
            if (stop != null) stop.Set();
            if (guard != null)
            {
                try { guard.WaitForExit(Config.GuardWaitMs); } catch { }
                guard.Dispose();
                guard = null;
            }
            if (stop != null) { stop.Dispose(); stop = null; }
        }

        public static void Watch(int parentId, string token)
        {
            Dictionary<IntPtr, bool> originals = new Dictionary<IntPtr, bool>();
            try
            {
                using (Process parent = Process.GetProcessById(parentId))
                using (EventWaitHandle stopEvent = EventWaitHandle.OpenExisting("Local\\HarborDock.Stop." + token))
                using (EventWaitHandle ready = EventWaitHandle.OpenExisting("Local\\HarborDock.Ready." + token))
                {
                    foreach (IntPtr bar in Native.Taskbars()) originals[bar] = Native.IsWindowVisible(bar);
                    ready.Set();
                    while (!parent.HasExited && !stopEvent.WaitOne(Config.GuardIntervalMs))
                    {
                        foreach (IntPtr bar in Native.Taskbars())
                        {
                            if (!originals.ContainsKey(bar)) originals[bar] = Native.IsWindowVisible(bar);
                            if (originals[bar]) Native.ShowWindow(bar, Native.SW_HIDE);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                foreach (KeyValuePair<IntPtr, bool> entry in originals)
                    if (entry.Value && Native.IsWindow(entry.Key)) Native.ShowWindow(entry.Key, Native.SW_SHOWNOACTIVATE);
            }
        }
    }
}
