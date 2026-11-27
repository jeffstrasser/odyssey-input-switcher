# Odyssey Input Switcher

A Windows 10/11 desktop and system-tray app for switching a selected physical monitor's input over HDMI or DisplayPort. Written in C# / Windows Forms with native Windows DDC/CI APIs; published for **x64 and ARM64**. It runs without administrator privileges. The application executable, window, taskbar, and tray use an original video-monitor icon.

## Working Odyssey input mapping

Samsung identifies **LS49DG934SNXGO** as the **49-inch Odyssey OLED G9 G93SD**. The owner supplied another working app and confirmed that all three of its input buttons switch their monitor successfully. Static inspection of that executable (without running it) established these VCP `0x60` writes:

| Input | Working write value |
| --- | --- |
| DisplayPort | `0x0F` (15) |
| HDMI, full-size | `0x05` (5) |
| Micro HDMI | `0x06` (6) |

These values are now **preloaded**. The first version left values blank and mentioned conventional HDMI values `0x11`/`0x12`, which do not match the working app's HDMI commands. Both apps use the same Windows `SetVCPFeature` API and VCP `0x60`; the important difference is the values, not the communication protocol.

This mapping is owner-confirmed for their setup, not a Samsung-published guarantee for every G93SD, firmware revision, or another monitor. The official product/support pages do not provide the mapping. Do not infer write values from current-input reads or capabilities, which can be misleading on Samsung monitors.

## Run

Download **OdysseyInputSwitcher-Setup-VERSION.exe** from [Releases](https://github.com/jeffstrasser/odyssey-input-switcher/releases) for a guided installation. One installer supports both x64 and ARM64 Windows and selects the native app automatically (Windows 10 version 1809 or later / Windows 11). It installs for the current user in `%LOCALAPPDATA%\Programs\Odyssey Input Switcher`, without administrator rights, and adds a Start menu shortcut plus an optional desktop shortcut. Uninstall from Windows **Installed apps**. Close Odyssey using **Exit** in its tray menu before installing, upgrading, or uninstalling; the installer does not forcibly terminate monitor operations.

Setup does not enable Windows startup. Enable it later from the app's Settings menu if desired. Uninstall preserves input/monitor preferences and removes startup registration only when it points at the executable being uninstalled, not a portable copy elsewhere. Re-running Setup upgrades the same installation. In-app upgrades also work in this writable install location, but the Installed apps version reflects the last Setup run. Installers remain unsigned and may trigger Windows publisher warnings.

The portable ZIPs remain available:

Open `dist\win-x64\OdysseyInputSwitcher.exe` on an Intel/AMD x64 PC, or `dist\win-arm64\OdysseyInputSwitcher.exe` on a Windows ARM64 PC. These are self-contained builds; an installed .NET runtime is not required. Keep the app executable after extracting any distribution archive. Single-file .NET deployment extracts native runtime files into the user's temporary directory. The app is unsigned, so Windows may show its normal unrecognized-publisher warning.

1. Connect the PC to the monitor using HDMI or DisplayPort. Keep the monitor awake and the PC's connection active. If the monitor has a DDC/CI setting, enable it; its presence on this exact model is not assumed.
2. On startup, the app discovers physical monitors automatically. If there is **only one**, it selects that monitor. If there are **multiple**, it selects a uniquely matching saved default, or waits for you to choose from the monitor list. Choose **Settings > Save selected monitor as default** after selecting your preferred monitor to remember it.
3. Click **DisplayPort**, **HDMI**, or **Micro HDMI**. The confirmation names the selected monitor and command; **Cancel** is the default. Select **Do not show me this again** to save the preference and skip future confirmations. Have the physical Source control available to recover before approving. The app sends one VCP `0x60` write, without a retry.
4. Open the **Settings** menu directly beneath the native Windows title bar (or press **Alt+S**) for **Input & monitor settings...**, **Diagnostics & logging...**, update checks, and Windows startup. There is no Settings button in the footer. The tray icon retains its equivalent **Settings** submenu.

**Settings > About Odyssey...** in the main or tray menu shows the app name, installed version, creator, process architecture, and source repository.

Discovery refreshes automatically after input-switch attempts, display-topology changes, and Windows resume. Rapid changes are debounced; discovery waits for any outstanding monitor operation before replacing its handles. **Refresh** is also available manually. Refreshing and selecting never send input commands.

The main and settings windows have fixed borders: they cannot be manually resized or maximized. The app still uses per-monitor DPI scaling when moved between displays. The main window keeps a minimum width but sizes its height to the content, with no empty status row when idle; settings open centered over it, with scrolling available for content that exceeds the window.

The input confirmation grows to fit its full warning and buttons at larger font sizes. The main window scrolls when its content cannot fit, and input buttons size to their text.

## System tray

The app's monitor icon appears in the Windows notification area while running (possibly under the hidden-icons arrow). Right-click it to choose **DisplayPort**, **HDMI**, or **Micro HDMI**, refresh monitors, open the app, access the **Settings** submenu, or **Exit**. Tray input actions use the same selected monitor, saved input values, and confirmation as the main window; they are disabled when no valid monitor is selected or an operation is in progress.

**Run in tray**, minimizing, or closing the main window hides it without stopping the app. Double-click the tray icon or choose **Show Odyssey** to reopen it. Choose **Exit** from the tray menu to stop the app and release its monitor handles. Exiting waits for an outstanding operation or confirmation. This does not install a service.

**Settings > Run at Windows startup** is opt-in and applies only to the current Windows user. It registers the published executable's quoted path with `--tray` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; no administrator rights are required. At sign-in the app starts directly in the tray without opening its main window. Automatic update notifications remain in the tray until you choose **Settings > Check for updates...**. Uncheck the option to remove the registration. Keep the executable in a permanent location; after moving it, enable startup again from the new location. Windows Task Manager's Startup apps controls may independently disable launching. Development builds cannot enable this option. You can also launch a published executable with `--tray` manually.

## Updates

On launch, Odyssey checks the [latest stable GitHub release](https://github.com/jeffstrasser/odyssey-input-switcher/releases/latest) and asks before downloading a newer version. **No** is the default. You can retry using **Settings > Check for updates...** in the main or tray menu. Tray-mode launches notify without opening the upgrade prompt. Drafts and prereleases are not offered; network errors and GitHub rate limits are reported without preventing monitor use.

If you approve, the app downloads the ZIP for its running process architecture (x64 or ARM64), verifies the SHA-256 digest supplied by GitHub, and checks the executable's PE structure, architecture, and embedded version against the release. It stages only the application executable and closes before replacing it. A temporary copy of the old app performs the replacement and restarts Odyssey. The backup is retained until the new app's UI and preferences have initialized and it reports readiness. A failed launch, early crash, or missing readiness signal within two minutes restores the old executable; restart it manually after a failed update. Temporary installer files are removed by the initialized app without blocking its UI. Failed installs report their error; a retained `.update-backup` must be preserved or moved before retrying. Readiness is not a guarantee against later runtime failures.

In-place upgrades require the **published single-file release** in a writable directory. Protected installation folders, other running copies, or antivirus locks can prevent replacement; the app does not request elevation. Source builds cannot self-install. Saved input and monitor preferences remain unchanged. Packages are unsigned: the digest verifies the downloaded archive against GitHub metadata, not independent publisher identity. Old versions without this feature require one manual upgrade first.

Automated release tags must be `vMAJOR.MINOR.PATCH` and match the project version, with matching `OdysseyInputSwitcher-win-x64.zip` / `OdysseyInputSwitcher-win-arm64.zip` assets and GitHub SHA-256 digests. Publish with `.\build.ps1 -Version MAJOR.MINOR.PATCH` so the executable version matches the tag. The build defaults to the project version (currently 1.3.0). Do not reuse version numbers. The in-app updater continues to use the native ZIP, not the universal Setup executable.

## Settings and saved preferences

**Settings > Customize input names...** (also in the tray menu) lets you name DisplayPort, HDMI, and Micro HDMI after their connected devices. Enter 1-48 characters per name, without line breaks or control characters, then choose **Save input names**. Surrounding whitespace is trimmed. **Restore default names** fills in the original names; choose **Save input names** to keep them. Buttons, tray actions, and switch confirmations use saved names; longer button/confirmation labels wrap. Names never change input identities or VCP values.

Names are stored separately in `%LOCALAPPDATA%\OdysseyInputSwitcher\input-names.json`, so existing mappings remain compatible and names persist across restarts and upgrades. Missing names retain their original labels. Invalid saved names are reported rather than silently accepted.

The **Diagnostics** tab contains the detailed log, process architecture, selected display, Windows monitor-interface identity, and saved default. **Read monitor diagnostics** reads VCP `0x60` and the raw capabilities string; each read's error is shown independently. A current value or `60(...)` capabilities list does not prove a working write mapping. No diagnostic read happens automatically during refresh.

In **Inputs & default monitor**, use the preloaded DisplayPort `0x0F`, HDMI `0x05`, and Micro HDMI `0x06` values for the owner's tested setup. Blank old settings are filled from that mapping on launch. For different nonblank old values, click **Use working Odyssey values**, then **Save input values**. This only updates settings; it does not switch anything. Use **Ask before switching inputs** to turn the confirmation back on after opting out. Values remain editable in decimal or `0x` hexadecimal, from 0 through 65535. The numeric range does not mean all values are supported. Do not systematically try values or substitute conventional HDMI `0x11`/`0x12` for this working mapping.

Input mappings are stored in `%LOCALAPPDATA%\OdysseyInputSwitcher\inputs.json`. Nonblank custom values are preserved; missing or blank saved values are treated as unconfigured. Clearing a field disables its main-window and tray switch until it is filled or the app restarts. Mappings are user-wide, not tied to a monitor identity.

The default monitor and input-confirmation preference are stored in `%LOCALAPPDATA%\OdysseyInputSwitcher\preferences.json`. Defaults match a unique Windows monitor-interface identity, **not the first list position**, display number, or generic description. If the saved default disappears while multiple monitors remain, none is selected automatically. **Clear default monitor** removes the monitor preference. Changing ports/drivers may change Windows' interface identity and require saving the default again. Mirrored displays or drivers that do not expose an unambiguous physical-monitor identity cannot be saved as defaults; select those manually. A sole connected monitor is still selected automatically, even if a different default was saved.

The app does not write on startup, automatically switch after discovery, brute-force input values, change brightness/power, or control all displays.

## Limitations and recovery

`SetVCPFeature` returning true means **Windows accepted the command**, not that the physical monitor changed input. The app deliberately does not claim switch verification or perform a readback after switching. A failed command can also mean that the monitor switched and dropped the control connection before Windows received a response. Check the physical monitor; do not blindly retry.

The originating PC may no longer be able to control the monitor once another input becomes active. This is not a network or USB remote: switching back may require the physical Source control, or an app on the other connected PC if that active path supports DDC/CI. Docks, adapters, KVMs, drivers, inactive cables, sleep states, HDR/firmware behavior, and incomplete monitor MCCS support can affect communication. A USB cable by itself is not this app's control path.

Windows monitor configuration calls can take seconds and do not expose a reliable cancellation API. They run off the UI thread; other monitor actions are disabled while a call is outstanding. Exit and automatic refresh wait for the call to finish so physical monitor handles are not destroyed underneath it. No input commands are sent as part of local build validation.

## Build from source

Install the **.NET 10 SDK** for your Windows development PC from [Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio and C++ build tools are not required. Internet access is needed for the runtime packs on the first self-contained publish.

Install [Inno Setup 6.7.3](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3) to build the installer (`winget install --id JRSoftware.InnoSetup --exact --version 6.7.3`). Then run `.\build.ps1` in PowerShell (subject to your organization's script execution policy). It generates the icon, runs hardware-free tests, publishes and verifies both native ZIPs, compiles one universal Setup executable, and writes `dist\SHA256SUMS-VERSION.txt`. Inno Setup is only a build dependency; users need neither it nor an installed .NET runtime.

Use `-InnoCompiler 'C:\path\ISCC.exe'` for a nonstandard compiler location. Use `-SkipInstaller` for portable-only builds, or `-Architecture win-x64 -SkipInstaller` / `-Architecture win-arm64 -SkipInstaller` for one native ZIP. Setup requires both architectures, so a partial build without `-SkipInstaller` fails rather than using stale payloads. The installer source is `installer\OdysseyInputSwitcher.iss`.

### Release automation

`.github\workflows\release.yml` runs on pull requests, manual dispatch, and `v*` tags. It installs the pinned compiler with a verified SHA-256 digest, builds all packages, and smoke-tests install/reinstall/uninstall on native x64 and ARM64 GitHub-hosted runners. Only a version tag matching the project version publishes a GitHub release, and only after both installer checks pass. Release assets are the universal Setup executable, both portable ZIPs, and the checksum file. Update the project version, merge the change, then push the corresponding new tag. No release is published by PR or manual-dispatch runs.

For local installer validation, run `.\tests\InstallerSmoke.ps1 -Installer dist\OdysseyInputSwitcher-Setup-VERSION.exe` after a full build. This installs and uninstalls in a temporary directory and checks native payload bytes and saved-settings/startup preservation. Run on a disposable Windows test account with no existing Odyssey installer registration; it makes temporary Installed apps and Start menu entries. It does not launch the app or enable Windows startup.

If script execution is disabled, run these equivalent commands directly, replacing `dotnet` with `.\.tools\dotnet\dotnet.exe` if using the folder-local SDK:

```powershell
dotnet run --project tools\IconGenerator.csproj -c Release -- assets\monitor.ico
dotnet run --project tests\SwitcherTests.csproj -c Release
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-x64
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-arm64
```

Those direct commands publish executables only. Use the full build script to produce release ZIPs, checksums, and Setup.

Cross-publishing ARM64 on an x64 PC is supported; it does not prove ARM64 execution. The hardware-free tests cover value parsing, saved-value overrides and preset restoration, sole-monitor selection, missing/duplicate/reordered default identities, monitor and confirmation preference serialization, single-write/no-readback behavior, write failures without retries, native interop structure sizes, and exactly-once release of valid zero-valued physical-monitor handles. Physical switching and native ARM64 execution need the corresponding hardware.

Input-name tests cover validation, persistence/restart, restoring defaults, literal ampersands, stable VCP mappings, and long-label/editor/confirmation layout at 10, 16, and 24pt fonts. Repeat the suite with `dotnet run --project tests\SwitcherTests.csproj -c Release -- --dpi-unaware` to also exercise 100% logical scaling independently of the desktop's DPI setting.

Update tests cover version comparison, architecture-specific release selection, invalid metadata, digest mismatch, cancellation, safe ZIP extraction, executable structure/version/architecture validation, locks, readiness signals, and rollback after failed launches or early crashes. Startup tests cover quoted executable paths, invalid paths, development-build rejection before registry writes, hidden initial visibility with a tray icon, and reopening the main window. They do not enable Windows startup or simulate sign-in. After running the Release tests and publishing x64, `.\tests\UpdateSmoke.ps1 -PublishedExecutable .\dist\win-x64\OdysseyInputSwitcher.exe` exercises the real published installer with a harmless test executable, including initialization readiness, restart, and staging cleanup. Run that smoke test on x64 Windows; it never starts the monitor UI or invokes monitor APIs.

The working mapping is confirmed by the owner's testing of the supplied app, not by sending commands during development. Local x64 UI validation does not prove native ARM64 execution or physical switching with this revised build.

## Sources

- [Samsung canonical exact-model product page](https://www.samsung.com/us/business/monitors/gaming/49-odyssey-oled-g9-g93sd-dual-qhd-240hz-0-03ms-g-sync-compatible-curved-gaming-monitor-with-glare-free-sku-ls49dg934snxgo/)
- [Samsung exact-model support and manual downloads](https://www.samsung.com/latin_en/support/model/LS49DG934SNXGO/) (English manual listed as `BN81-26440B-02_WUG_G95NC G93SC G93SD G91SD_NA LATIN_ENG_250509.0.pdf`)
- [Microsoft: low-level monitor configuration](https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-low-level-monitor-configuration-functions)
- [Microsoft: physical monitor enumeration and handle ownership](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor)
- [Microsoft: Windows monitor-interface identities](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw)
- [Microsoft: SetVCPFeature, including MCCS compatibility warning](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)
