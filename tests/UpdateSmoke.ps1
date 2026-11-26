param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedExecutable
)
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishedExecutable).Path
$fixture = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\SwitcherTests.exe'
if (!(Test-Path -LiteralPath $fixture)) { throw 'Run the Release hardware-free tests first.' }
$id = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $env:LOCALAPPDATA "OdysseyInputSwitcher\updates\$id"
$target = Join-Path (Split-Path $fixture) "update-smoke-$id.exe"
$receipt = "$target.restarted"
$helper = $null
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath $published -Destination (Join-Path $stage 'installer.exe')
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $stage 'payload.exe')
    Set-Content -LiteralPath $target -Value 'Previous executable'
    $helper = Start-Process -FilePath (Join-Path $stage 'installer.exe') `
        -ArgumentList "--apply-update 2147483647 `"$target`" `"$stage`"" -PassThru
    if (!$helper.WaitForExit(30000)) { throw 'Published installer did not exit.' }
    $deadline = (Get-Date).AddSeconds(30)
    while (!(Test-Path -LiteralPath $receipt) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path -LiteralPath $receipt)) { throw 'Updated executable did not restart and complete cleanup.' }
    if (Test-Path -LiteralPath $stage) { throw 'Restarted executable did not remove the staging directory.' }
    if (Test-Path -LiteralPath "$target.update-backup") { throw 'Successful installation retained its backup.' }
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $fixture).Hash) {
        throw 'Installed bytes do not match the staged executable.'
    }
    Write-Output 'PASS: published installer replaces, restarts, and cleans up. No monitor APIs invoked.'
} finally {
    if ($null -ne $helper -and !$helper.HasExited) { Stop-Process -Id $helper.Id }
    foreach ($file in @($target, $receipt, "$target.update-backup", "$target.update-new")) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
