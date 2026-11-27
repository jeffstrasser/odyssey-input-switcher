param(
    [ValidateSet('win-x64', 'win-arm64', 'all')]
    [string]$Architecture = 'all',
    [ValidatePattern('^([1-9]\d*|\d+\.\d+\.\d+)$')]
    [string]$Version,
    [switch]$SkipInstaller,
    [string]$InnoCompiler,
    [ValidateSet('None', 'SelfSigned')]
    [string]$SigningMode = 'None',
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$SigningCertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
. "$PSScriptRoot\tools\SignRelease.ps1" -TimestampServer $TimestampServer
$signingThumbprint = $null
if ($SigningMode -eq 'SelfSigned') {
    $certificate = Get-ReleaseSigningCertificate -Thumbprint $SigningCertificateThumbprint
    try { $signingThumbprint = $certificate.Thumbprint }
    finally { $certificate.Dispose() }
    Write-Warning 'Self-signed Authenticode does not establish public Windows trust or guarantee fewer SmartScreen warnings.'
}
if (!$Version) {
    [xml]$project = Get-Content -LiteralPath 'OdysseyInputSwitcher.csproj'
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^([1-9]\d*|\d+\.\d+\.\d+)$') { throw 'Version must be a positive whole number or a legacy MAJOR.MINOR.PATCH.' }
$binaryVersion = $Version
if ($Version -match '^\d+$') { $binaryVersion = "$Version.0.0" }
if (@(([version]$binaryVersion).Major, ([version]$binaryVersion).Minor, ([version]$binaryVersion).Build) |
    Where-Object { $_ -gt 65535 }) { throw 'Version exceeds the Windows version limit.' }
if (!$SkipInstaller -and $Architecture -ne 'all') {
    throw 'The universal installer requires both architectures. Use -Architecture all or explicitly -SkipInstaller.'
}
if (!$SkipInstaller) {
    if (!$InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
        else {
            foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
                if (!$root) { continue }
                $candidate = Join-Path $root 'Inno Setup 6\ISCC.exe'
                if (Test-Path -LiteralPath $candidate) { $InnoCompiler = $candidate; break }
                $candidate = Join-Path $root 'Programs\Inno Setup 6\ISCC.exe'
                if (Test-Path -LiteralPath $candidate) { $InnoCompiler = $candidate; break }
            }
        }
    }
    if (!$InnoCompiler -or !(Test-Path -LiteralPath $InnoCompiler)) {
        throw 'Inno Setup 6.7.3 is required. Install it, pass -InnoCompiler, or explicitly use -SkipInstaller for portable builds.'
    }
}
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (Test-Path $localSdk) {
    $dotnet = $localSdk
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
& $dotnet run --project 'tools\IconGenerator.csproj' -c Release -- 'assets\monitor.ico'
if ($LASTEXITCODE -ne 0) { throw 'Monitor icon generation failed.' }
& $dotnet run --project 'tests\SwitcherTests.csproj' -c Release
if ($LASTEXITCODE -ne 0) { throw 'Hardware-free tests failed.' }
$targets = @($Architecture)
if ($Architecture -eq 'all') { $targets = @('win-x64', 'win-arm64') }
foreach ($target in $targets) {
    $executable = "dist\$target\OdysseyInputSwitcher.exe"
    if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable }
    & $dotnet publish 'OdysseyInputSwitcher.csproj' -c Release -r $target --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false "-p:Version=$Version" "-p:OutputPath=bin\Publish\$target\" -o "dist\$target"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $target" }
    if ($SigningMode -eq 'SelfSigned') {
        Invoke-ReleaseSigning -Path $executable `
            -Thumbprint $SigningCertificateThumbprint -TimestampUrl $TimestampServer
    }
    Copy-Item 'README.md' "dist\$target\README.md"
    Compress-Archive -LiteralPath "dist\$target\OdysseyInputSwitcher.exe", "dist\$target\README.md" `
        -DestinationPath "dist\OdysseyInputSwitcher-$target.zip" -CompressionLevel Optimal -Force
    $testArchitecture = 'X64'
    if ($target -eq 'win-arm64') { $testArchitecture = 'Arm64' }
    & $dotnet run --project 'tests\SwitcherTests.csproj' -c Release -- `
        --verify-package "dist\OdysseyInputSwitcher-$target.zip" $testArchitecture $Version
    if ($LASTEXITCODE -ne 0) { throw "Package verification failed: $target" }
}
if (!$SkipInstaller) {
    $setup = "dist\OdysseyInputSwitcher-Setup-$Version.exe"
    if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup }
    $compilerArguments = @('/Qp', "/DAppVersion=$Version", "/DAppFileVersion=$binaryVersion.0")
    if ($SigningMode -eq 'SelfSigned') {
        if ($TimestampServer -notmatch '^https?://[A-Za-z0-9./:_-]+$') { throw 'Invalid timestamp server URL.' }
        $script = (Join-Path $PSScriptRoot 'tools\SignRelease.ps1').Replace('$', '$$')
        $shell = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe').Replace('$', '$$')
        $signCommand = '$q' + $shell + '$q -NoProfile -File $q' + $script + '$q -FilePath $f'
        if ($SigningCertificateThumbprint) { $signCommand += " -CertificateThumbprint $SigningCertificateThumbprint" }
        $signCommand += " -TimestampServer $TimestampServer"
        $compilerArguments += '/DEnableSigning', "/SConnectedDisplaySelfSigned=$signCommand"
    }
    & $InnoCompiler @compilerArguments 'installer\OdysseyInputSwitcher.iss'
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $setup)) {
        throw 'Universal installer compilation failed.'
    }
    if ($SigningMode -eq 'SelfSigned') {
        Assert-ReleaseSignature -Path $setup -Thumbprint $signingThumbprint -RequireTimestamp
    }
}
$artifacts = @("dist\OdysseyInputSwitcher-$($targets[0]).zip")
if ($targets.Length -gt 1) { $artifacts += "dist\OdysseyInputSwitcher-$($targets[1]).zip" }
if (!$SkipInstaller) { $artifacts += $setup }
$checksums = foreach ($artifact in $artifacts) {
    $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($artifact)
}
$checksums | Set-Content -LiteralPath "dist\SHA256SUMS-$Version.txt" -Encoding ASCII
