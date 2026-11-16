# Harbor Dock

Native WPF application for Windows 10 x64 / .NET Framework 4.8. No NuGet dependencies.

Version 1.2 adds elastic automatic compaction over maximized/fullscreen windows. It recognizes both maximization and work-area coverage, including the invisible resize borders and the taskbar-reserved bottom area. The compact panel is 28 DIP high and stretches horizontally by up to 14%; icons keep their aspect ratio. Pointer activation is limited to the rendered dock and icons, with a small margin. Hover, menus, dragging and the reveal hotkey expand it. Auto-hide remains an independent setting.

The spring uses an analytic damped solution so motion is independent of frame rate and preserves velocity when direction changes. Layout and hit testing use the actual animated icon bounds. The background outline bends slightly with spring velocity.

Build with Windows PowerShell: `./build.ps1`. The output defaults to the parent directory.
`make_icon.ps1` regenerates the embedded application icon.

- Model.cs: configuration, atomic JSON storage, animation math.
- Native.cs: Win32 bindings and taskbar recovery process.
- Shell.cs: native icons, shortcuts, visible windows, application launch, optional startup entry.
- Dock.cs: rendering, magnification, mouse handling, menus, window activation, status refresh.
- SystemStatus.cs: Native Wi-Fi, WinRT radio state, foreground keyboard layout and language requests.
- Preferences.cs: settings and interactive preview.
- Program.cs: lifecycle, tray, instance handling, checks and render verification.

`--profile <directory>` uses an isolated settings directory.
`--self-test <report.txt>` performs computation and read checks and switches the keyboard layout only on its own hidden test window.
`--guard-test <report.txt>` briefly hides the taskbar and checks restoration after normal exit and a deliberately terminated child process. Run with other dock instances closed.
`--render-preview <directory>` renders the actual WPF dock and settings visuals to PNG without showing windows or saving preferences.

The status reader does not change radio state, request location permission, or scan for Bluetooth devices. Radio enumeration is cached for 30 seconds and refreshed on device changes; state and WLAN association are checked in a background task. Keyboard layout is read from the foreground window thread. Layout selection posts WM_INPUTLANGCHANGEREQUEST to that window; an application can reject this request.

API references:

- [WPF windows](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/)
- [Native Wi-Fi query](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface)
- [WLAN association structure](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/ns-wlanapi-wlan_association_attributes)
- [WinRT radio enumeration](https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radio.getradiosasync)
- [Keyboard layout change request](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-inputlangchangerequest)
- [Windows Settings URIs](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)
- [Maximized window detection](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iszoomed)
- [Layered window transparency and input](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)
