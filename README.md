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

Open `dist\win-x64\OdysseyInputSwitcher.exe` on an Intel/AMD x64 PC, or `dist\win-arm64\OdysseyInputSwitcher.exe` on a Windows ARM64 PC. These are self-contained builds; an installed .NET runtime is not required. Keep the app executable after extracting any distribution archive. Single-file .NET deployment extracts native runtime files into the user's temporary directory. The app is unsigned, so Windows may show its normal unrecognized-publisher warning.

1. Connect the PC to the monitor using HDMI or DisplayPort. Keep the monitor awake and the PC's connection active. If the monitor has a DDC/CI setting, enable it; its presence on this exact model is not assumed.
2. On startup, the app discovers physical monitors automatically. If there is **only one**, it selects that monitor. If there are **multiple**, it selects a uniquely matching saved default, or waits for you to choose from the monitor list. Click **Save as default** after selecting your preferred monitor to remember it.
3. Click **DisplayPort**, **HDMI**, or **Micro HDMI**. The confirmation names the selected monitor and command; **Cancel** is the default. Select **Do not show me this again** to save the preference and skip future confirmations. Have the physical Source control available to recover before approving. The app sends one VCP `0x60` write, without a retry.
4. Open **Advanced...** for diagnostics and input settings. The main window intentionally shows only monitor selection, input buttons, and a short status.

Discovery refreshes automatically after input-switch attempts, display-topology changes, and Windows resume. Rapid changes are debounced; discovery waits for any outstanding monitor operation before replacing its handles. **Refresh** is also available manually. Refreshing and selecting never send input commands.

The main window can be resized, and the app uses per-monitor DPI scaling when moved between displays. The Advanced window opens centered over the main window and can be resized independently.

The input confirmation grows to fit its full warning and buttons at larger font sizes. The main window scrolls when its content cannot fit, and input buttons size to their text.

## System tray

The app's monitor icon appears in the Windows notification area while running (possibly under the hidden-icons arrow). Right-click it to choose **DisplayPort**, **HDMI**, or **Micro HDMI**, refresh monitors, open the app or Advanced, or **Exit**. Tray input actions use the same selected monitor, saved input values, and confirmation as the main window; they are disabled when no valid monitor is selected or an operation is in progress.

**Run in tray**, minimizing, or closing the main window hides it without stopping the app. Double-click the tray icon or choose **Show Odyssey** to reopen it. Choose **Exit** from the tray menu to stop the app and release its monitor handles. Exiting waits for an outstanding monitor call or confirmation. This does not install a service or enable launch at Windows sign-in.

## Updates

On launch, Odyssey checks the [latest stable GitHub release](https://github.com/jeffstrasser/odyssey-input-switcher/releases/latest) and asks before downloading a newer version. **No** is the default. You can retry using **Check for updates...** in the tray menu. Drafts and prereleases are not offered; network errors and GitHub rate limits are reported without preventing monitor use.

If you approve, the app downloads the ZIP for its running process architecture (x64 or ARM64), verifies the SHA-256 digest supplied by GitHub, stages only the application executable, and closes before replacing it. A temporary copy of the old app performs the replacement and restarts Odyssey. A backup is retained until restart succeeds, and restored if starting the new process fails. Temporary installer files are removed by the restarted app. Failed installs report their error; a retained `.update-backup` must be preserved or moved before retrying. Starting a process is not a guarantee that the new app will remain healthy.

In-place upgrades require the **published single-file release** in a writable directory. Protected installation folders, other running copies, or antivirus locks can prevent replacement; the app does not request elevation. Source builds cannot self-install. Saved input and monitor preferences remain unchanged. Packages are unsigned: the digest verifies the downloaded archive against GitHub metadata, not independent publisher identity. Old versions without this feature require one manual upgrade first.

Release tags must be `vMAJOR.MINOR.PATCH` (or the same without `v`), with matching `OdysseyInputSwitcher-win-x64.zip` / `OdysseyInputSwitcher-win-arm64.zip` assets and GitHub SHA-256 digests. Publish with `.\build.ps1 -Version MAJOR.MINOR.PATCH` so the executable version matches the tag. The source version defaults to 1.2.0. Do not reuse version numbers.

## Advanced and saved settings

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

From this folder, run `.\build.ps1` in PowerShell (subject to your organization's script execution policy). It generates the multi-resolution monitor icon from source, runs hardware-free tests, publishes both architectures into separate `dist` folders, and packages each app and README in a ZIP. Use `.\build.ps1 -Architecture win-x64` or `win-arm64` to publish just one.

If script execution is disabled, run these equivalent commands directly, replacing `dotnet` with `.\.tools\dotnet\dotnet.exe` if using the folder-local SDK:

```powershell
dotnet run --project tools\IconGenerator.csproj -c Release -- assets\monitor.ico
dotnet run --project tests\SwitcherTests.csproj -c Release
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-x64
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-arm64
```

Cross-publishing ARM64 on an x64 PC is supported; it does not prove ARM64 execution. The hardware-free tests cover value parsing, saved-value overrides and preset restoration, sole-monitor selection, missing/duplicate/reordered default identities, monitor and confirmation preference serialization, single-write/no-readback behavior, write failures without retries, native interop structure sizes, and exactly-once release of valid zero-valued physical-monitor handles. Physical switching and native ARM64 execution need the corresponding hardware.

Update tests cover version comparison, architecture-specific release selection, invalid metadata, digest mismatch, cancellation, safe ZIP extraction, executable replacement, locks, and restart-failure rollback. After running the Release tests and publishing x64, `.\tests\UpdateSmoke.ps1 -PublishedExecutable .\dist\win-x64\OdysseyInputSwitcher.exe` exercises the real published installer with a harmless test executable, including restart and staging cleanup. Run that smoke test on x64 Windows; it never starts the monitor UI or invokes monitor APIs.

The working mapping is confirmed by the owner's testing of the supplied app, not by sending commands during development. Local x64 UI validation does not prove native ARM64 execution or physical switching with this revised build.

## Sources

- [Samsung canonical exact-model product page](https://www.samsung.com/us/business/monitors/gaming/49-odyssey-oled-g9-g93sd-dual-qhd-240hz-0-03ms-g-sync-compatible-curved-gaming-monitor-with-glare-free-sku-ls49dg934snxgo/)
- [Samsung exact-model support and manual downloads](https://www.samsung.com/latin_en/support/model/LS49DG934SNXGO/) (English manual listed as `BN81-26440B-02_WUG_G95NC G93SC G93SD G91SD_NA LATIN_ENG_250509.0.pdf`)
- [Microsoft: low-level monitor configuration](https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-low-level-monitor-configuration-functions)
- [Microsoft: physical monitor enumeration and handle ownership](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor)
- [Microsoft: Windows monitor-interface identities](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaydevicesw)
- [Microsoft: SetVCPFeature, including MCCS compatibility warning](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)
