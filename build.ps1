param(
    [ValidateSet('win-x64', 'win-arm64', 'all')]
    [string]$Architecture = 'all'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
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
    & $dotnet publish 'OdysseyInputSwitcher.csproj' -c Release -r $target --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false "-p:OutputPath=bin\Publish\$target\" -o "dist\$target"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $target" }
    Copy-Item 'README.md' "dist\$target\README.md"
    Compress-Archive -LiteralPath "dist\$target\OdysseyInputSwitcher.exe", "dist\$target\README.md" `
        -DestinationPath "dist\OdysseyInputSwitcher-$target.zip" -CompressionLevel Optimal -Force
}
