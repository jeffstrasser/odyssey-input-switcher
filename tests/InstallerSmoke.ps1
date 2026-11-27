param(
    [Parameter(Mandatory = $true)]
    [string]$Installer,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Architecture
)
$ErrorActionPreference = 'Stop'
foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32, [Microsoft.Win32.RegistryView]::Registry64)) {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try {
        $existing = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{29D7D90B-4F22-4F2E-AF74-476F234D7E42}_is1')
        if ($existing) {
            $existing.Dispose()
            throw 'An Odyssey installer registration already exists. Run this smoke test on a disposable test account.'
        }
    } finally { $registry.Dispose() }
}
if (!$Architecture) {
    $Architecture = 'win-x64'
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') {
        $Architecture = 'win-arm64'
    }
}
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$payload = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\dist\$Architecture\OdysseyInputSwitcher.exe")).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('odyssey-setup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$app = Join-Path $directory 'app'
function Get-StartupCommand {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    if (!$key) { return $null }
    try { return $key.GetValue('OdysseyInputSwitcher', $null) }
    finally { $key.Dispose() }
}
$startupBefore = Get-StartupCommand
$settings = Join-Path $env:LOCALAPPDATA 'OdysseyInputSwitcher'
$settingsBefore = @{}
foreach ($name in @('inputs.json', 'preferences.json')) {
    $path = Join-Path $settings $name
    if (Test-Path -LiteralPath $path) { $settingsBefore[$name] = (Get-FileHash -LiteralPath $path).Hash }
}
$installed = $false
try {
    $group = 'Odyssey test ' + [IO.Path]::GetFileName($directory)
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /TASKS=`"`" /GROUP=`"$group`" /DIR=`"$app`" /LOG=`"$directory\install.log`""
    $process = Start-Process -FilePath $installerPath -ArgumentList $arguments -PassThru
    if (!$process.WaitForExit(120000)) { Stop-Process -Id $process.Id; throw 'Installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode). See $directory\install.log" }
    $installed = $true
    $exe = Join-Path $app 'OdysseyInputSwitcher.exe'
    if ((Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath $payload).Hash) {
        throw 'The installer selected the wrong native payload.'
    }
    if (!(Test-Path -LiteralPath (Join-Path $app 'README.md'))) { throw 'Installed README is missing.' }
    $process = Start-Process -FilePath $installerPath -ArgumentList $arguments -PassThru
    if (!$process.WaitForExit(120000)) { Stop-Process -Id $process.Id; throw 'Reinstall timed out.' }
    if ($process.ExitCode -ne 0) { throw "Reinstall failed: $($process.ExitCode)." }
    if ((Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath $payload).Hash) {
        throw 'Reinstall changed the native payload unexpectedly.'
    }
    $uninstaller = Join-Path $app 'unins000.exe'
    $process = Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$directory\uninstall.log`"" -PassThru
    if (!$process.WaitForExit(120000)) { Stop-Process -Id $process.Id; throw 'Uninstall timed out.' }
    if ($process.ExitCode -ne 0) { throw "Uninstall failed: $($process.ExitCode)." }
    $deadline = (Get-Date).AddSeconds(30)
    while ((Test-Path -LiteralPath $uninstaller) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $uninstaller) { throw 'Uninstaller did not finish self-removal.' }
    $installed = $false
    if (Test-Path -LiteralPath $exe) { throw 'Uninstall left the app executable behind.' }
    $startupAfter = Get-StartupCommand
    if ($startupBefore -ne $startupAfter) { throw 'Installer changed unrelated startup registration.' }
    foreach ($name in $settingsBefore.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $settings $name)).Hash -ne $settingsBefore[$name]) {
            throw "Installer changed saved settings: $name"
        }
    }
    Write-Output "PASS: universal installer selects $Architecture, reinstalls, uninstalls, and preserves startup/settings."
} finally {
    if (!$installed) { Remove-Item -LiteralPath $directory -Recurse -Force }
    else { Write-Warning "Installation remains at $app; uninstall it before removing $directory." }
}
