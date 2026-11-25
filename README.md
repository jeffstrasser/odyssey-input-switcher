# Odyssey Input Switcher

A Windows 10/11 desktop app for sending a user-configured input command to an explicitly selected physical monitor over HDMI or DisplayPort. Written in C# / Windows Forms with native Windows DDC/CI APIs; published for **x64 and ARM64**. It runs without administrator privileges. The application executable, window, and taskbar use an original video-monitor icon.

## Important: exact-model support is not verified

Samsung identifies **LS49DG934SNXGO** as the **49-inch Odyssey OLED G9 G93SD**. Its official support page provides a shared G95NC/G93SC/G93SD/G91SD manual. No verified DDC/CI VCP `0x60` write mapping for this exact model was found in this investigation. The product/support pages do not establish that mapping or guarantee that input switching through that API is supported; listing the manual is not proof of a working DDC/CI mapping.

The app therefore starts with **all input values blank**. It does not import values from another Odyssey model, infer write values from reported current inputs, or trust capabilities as proof. Some Samsung models use nonstandard write values or report misleading capabilities. This app implements the Windows command path, but successful switching on this exact monitor must be physically tested by its owner.

## Run

Open `dist\win-x64\OdysseyInputSwitcher.exe` on an Intel/AMD x64 PC, or `dist\win-arm64\OdysseyInputSwitcher.exe` on a Windows ARM64 PC. These are self-contained builds; an installed .NET runtime is not required. Keep the app executable after extracting any distribution archive. Single-file .NET deployment extracts native runtime files into the user's temporary directory. The app is unsigned, so Windows may show its normal unrecognized-publisher warning.

1. Connect the PC directly to the monitor using HDMI or DisplayPort. Keep the monitor awake and the PC's connection active. If the monitor has a DDC/CI setting, enable it; its presence on this exact model is not assumed.
2. Click **Refresh monitors**, then explicitly choose the intended entry. Entries show the Windows display device, desktop position, physical-monitor index, and Windows-supplied physical description. This description is not a verified Samsung SKU or serial number. Never choose another display simply because it is first in the list.
3. Click **Read diagnostics (no writes)** to read current VCP `0x60` and the raw capabilities string. You can select/copy the diagnostic log. Each read's error is shown independently. A current value or a `60(...)` capabilities list does not prove a working write mapping.
4. Enter only input write values you have established for your hardware, using decimal or `0x` hexadecimal, in the DisplayPort, HDMI, and Micro HDMI fields. Those names are labels, not discovered capabilities. Accepted values are 0 through 65535; that numeric range does not mean all values are supported. For context only, conventional MCCS values commonly include decimal 15 (`0x0F`) for DisplayPort 1, 17 (`0x11`) for HDMI 1, and 18 (`0x12`) for HDMI 2. **These are not validated defaults for LS49DG934SNXGO or a verified Micro HDMI mapping. Do not systematically try values.**
5. Click the desired **Switch** button. The confirmation names the exact selected entry and value; **No** is the default. Have the monitor's physical Source control available to recover before approving. The app sends exactly one VCP `0x60` write with no automatic retry.

**Save input values** persists only your mappings in `%LOCALAPPDATA%\OdysseyInputSwitcher\inputs.json`. Mappings are user-wide, not tied to a monitor identity; check them against the selected display every time. Monitor selection is never saved or automatic. Blank values disable their switch buttons, and saving does not send monitor commands.

After any switch attempt or Windows display-topology notification, selection is invalidated. Refresh and explicitly select again before further operations. The app does not automatically reconnect, write on startup, scan input values, change brightness/power, or switch all displays.

## Limitations and recovery

`SetVCPFeature` returning true means **Windows accepted the command**, not that the physical monitor changed input. The app deliberately does not claim switch verification or perform a readback after switching. A failed command can also mean that the monitor switched and dropped the control connection before Windows received a response. Check the physical monitor; do not blindly retry.

The originating PC may no longer be able to control the monitor once another input becomes active. This is not a network or USB remote: switching back may require the physical Source control, or an app on the other connected PC if that active path supports DDC/CI. Docks, adapters, KVMs, drivers, inactive cables, sleep states, HDR/firmware behavior, and incomplete monitor MCCS support can affect communication. A USB cable by itself is not this app's control path.

Windows monitor configuration calls can take seconds and do not expose a reliable cancellation API. They run off the UI thread; other monitor actions are disabled while a call is outstanding. Closing waits for the call to finish so physical monitor handles are not destroyed underneath it. No input commands are sent as part of local build validation.

## Build from source

Install the **.NET 10 SDK** for your Windows development PC from [Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0). Visual Studio and C++ build tools are not required. Internet access is needed for the runtime packs on the first self-contained publish.

From this folder, run `.\build.ps1` in PowerShell (subject to your organization's script execution policy). It generates the multi-resolution monitor icon from source, runs hardware-free tests, and publishes both architectures into separate `dist` folders. Use `.\build.ps1 -Architecture win-x64` or `win-arm64` to publish just one.

If script execution is disabled, run these equivalent commands directly, replacing `dotnet` with `.\.tools\dotnet\dotnet.exe` if using the folder-local SDK:

```powershell
dotnet run --project tools\IconGenerator.csproj -c Release -- assets\monitor.ico
dotnet run --project tests\SwitcherTests.csproj -c Release
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-x64
dotnet publish OdysseyInputSwitcher.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o dist\win-arm64
```

Cross-publishing ARM64 on an x64 PC is supported; it does not prove ARM64 execution. The hardware-free tests cover value parsing, explicit selection, single-write/no-readback behavior, propagation of write failures without retries, native interop structure sizes, and exactly-once release of valid zero-valued physical-monitor handles. Physical switching and native ARM64 execution need the corresponding hardware.

Initial local validation: both self-contained architectures compiled successfully; the published x64 GUI launched and enumerated a physical monitor. Explicit-selection/value gating and display-change invalidation were exercised without VCP reads or writes, and the hardware-free tests passed. Native ARM64 execution and actual input switching on LS49DG934SNXGO remain untested.

## Sources

- [Samsung canonical exact-model product page](https://www.samsung.com/us/business/monitors/gaming/49-odyssey-oled-g9-g93sd-dual-qhd-240hz-0-03ms-g-sync-compatible-curved-gaming-monitor-with-glare-free-sku-ls49dg934snxgo/)
- [Samsung exact-model support and manual downloads](https://www.samsung.com/latin_en/support/model/LS49DG934SNXGO/) (English manual listed as `BN81-26440B-02_WUG_G95NC G93SC G93SD G91SD_NA LATIN_ENG_250509.0.pdf`)
- [Microsoft: low-level monitor configuration](https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-low-level-monitor-configuration-functions)
- [Microsoft: physical monitor enumeration and handle ownership](https://learn.microsoft.com/en-us/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor)
- [Microsoft: SetVCPFeature, including MCCS compatibility warning](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)
