param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\tools\SignRelease.ps1"
$source = (Resolve-Path -LiteralPath $Executable).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('connected-display-signing-test-' + [Guid]::NewGuid().ToString('N'))
$certificate = $null
$oldPfx = $env:SIGNING_PFX_BASE64
$oldPassword = $env:SIGNING_PFX_PASSWORD
$oldSmokeScript = $env:SIGNING_SMOKE_SCRIPT
$oldSmokeExecutable = $env:SIGNING_SMOKE_EXECUTABLE
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Temporary Connected Display Signing Test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 `
        -HashAlgorithm SHA256 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(1)
    $signed = Join-Path $directory 'signed.exe'
    Copy-Item -LiteralPath $source -Destination $signed
    Invoke-ReleaseSigning -Path $signed -Thumbprint $certificate.Thumbprint -WithoutTimestamp
    Assert-ReleaseSignature -Path $signed -Thumbprint $certificate.Thumbprint
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if ($signature.Status -eq 'Valid') {
        throw 'The temporary self-signed certificate must not establish Windows trust.'
    }
    $timestampRejected = $false
    try { Assert-ReleaseSignature -Path $signed -Thumbprint $certificate.Thumbprint -RequireTimestamp }
    catch { $timestampRejected = $true }
    if (!$timestampRejected) { throw 'Release verification accepted a missing timestamp.' }
    $tampered = Join-Path $directory 'tampered.exe'
    $bytes = [IO.File]::ReadAllBytes($signed)
    $bytes[0x50] = $bytes[0x50] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $bytes)
    if ((Get-AuthenticodeSignature -LiteralPath $tampered).Status -ne 'HashMismatch') {
        throw 'The tampered fixture must remain a recognized PE file with an invalid Authenticode digest.'
    }
    $tamperRejected = $false
    try { Assert-ReleaseSignature -Path $tampered -Thumbprint $certificate.Thumbprint }
    catch { $tamperRejected = $true }
    if (!$tamperRejected) { throw 'Release verification accepted a tampered signature.' }
    $password = [Guid]::NewGuid().ToString('N')
    $pfx = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
    try { $env:SIGNING_PFX_BASE64 = [Convert]::ToBase64String($pfx) }
    finally { [Array]::Clear($pfx, 0, $pfx.Length) }
    $env:SIGNING_PFX_PASSWORD = $password
    $fromSecret = Join-Path $directory 'from-secret.exe'
    Copy-Item -LiteralPath $source -Destination $fromSecret
    Invoke-ReleaseSigning -Path $fromSecret -WithoutTimestamp
    Assert-ReleaseSignature -Path $fromSecret -Thumbprint $certificate.Thumbprint
    $childSigned = Join-Path $directory 'child-signed.exe'
    Copy-Item -LiteralPath $source -Destination $childSigned
    $env:SIGNING_SMOKE_SCRIPT = (Resolve-Path -LiteralPath "$PSScriptRoot\..\tools\SignRelease.ps1").Path
    $env:SIGNING_SMOKE_EXECUTABLE = $childSigned
    $command = '$ErrorActionPreference = ''Stop''; . $env:SIGNING_SMOKE_SCRIPT; Invoke-ReleaseSigning -Path $env:SIGNING_SMOKE_EXECUTABLE -WithoutTimestamp'
    & (Get-SigningPowerShell) -NoProfile -NonInteractive -Command $command
    if ($LASTEXITCODE -ne 0) { throw 'Signing in a child PowerShell process failed.' }
    Assert-ReleaseSignature -Path $childSigned -Thumbprint $certificate.Thumbprint
    $env:SIGNING_PFX_BASE64 = $null
    $env:SIGNING_PFX_PASSWORD = $null
    $missingRejected = $false
    try { Invoke-ReleaseSigning -Path $fromSecret -WithoutTimestamp }
    catch { $missingRejected = $true }
    if (!$missingRejected) { throw 'Signing accepted missing credentials.' }
    Write-Output 'PASS: self-signed Authenticode, PFX and child-process signing, tamper rejection, and required timestamp/credential failures. No Windows trust was installed.'
} finally {
    $env:SIGNING_PFX_BASE64 = $oldPfx
    $env:SIGNING_PFX_PASSWORD = $oldPassword
    $env:SIGNING_SMOKE_SCRIPT = $oldSmokeScript
    $env:SIGNING_SMOKE_EXECUTABLE = $oldSmokeExecutable
    if ($certificate) {
        $thumbprint = $certificate.Thumbprint
        $certificate.Dispose()
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -DeleteKey
    }
    Remove-Item -LiteralPath $directory -Recurse -Force
}
