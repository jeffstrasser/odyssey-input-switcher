param(
    [string]$FilePath,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

function Get-ReleaseSigningCertificate {
    param([string]$Thumbprint)
    if ($Thumbprint) {
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    } else {
        if (!$env:SIGNING_PFX_BASE64 -or $null -eq $env:SIGNING_PFX_PASSWORD) {
            throw 'Self-signed releases require a certificate thumbprint or SIGNING_PFX_BASE64 and SIGNING_PFX_PASSWORD secrets.'
        }
        $bytes = [Convert]::FromBase64String($env:SIGNING_PFX_BASE64)
        try {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $bytes, $env:SIGNING_PFX_PASSWORD,
                [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    }
    try {
        if (!$certificate.HasPrivateKey) { throw 'The signing certificate has no private key.' }
        if ($certificate.Subject -ne $certificate.Issuer) {
            throw 'SelfSigned mode requires a self-signed certificate, not a CA-issued certificate.'
        }
        $now = Get-Date
        if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
            throw 'The signing certificate is not currently valid.'
        }
        $usages = @($certificate.Extensions | Where-Object {
            $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]
        } | ForEach-Object { $_.EnhancedKeyUsages })
        if (!@($usages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count) {
            throw 'The certificate must have the Code Signing enhanced key usage.'
        }
        return $certificate
    } catch {
        $certificate.Dispose()
        throw
    }
}

function Assert-ReleaseSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [switch]$RequireTimestamp
    )
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Missing or unexpected Authenticode signer: $Path"
    }
    if (!('ConnectedDisplay.AuthenticodeVerifier' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ConnectedDisplay {
    public static class AuthenticodeVerifier {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo {
            public uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr File, Subject;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData {
            public uint Size;
            public IntPtr Policy, Sip;
            public uint Ui, Revocation, Choice;
            public IntPtr File;
            public uint Action;
            public IntPtr State, Url;
            public uint Flags, Context;
            public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        public static int Verify(string path) {
            var file = new FileInfo { Size = (uint)Marshal.SizeOf(typeof(FileInfo)), Path = path };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData {
                Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Choice = 1,
                File = pointer, Action = 1, Flags = 0x1010
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            try { return WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally {
                data.Action = 2;
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                Marshal.DestroyStructure(pointer, typeof(FileInfo));
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
'@
    }
    # PowerShell can label an untrusted root UnknownError. Check the exact native result, not localized text.
    $result = [ConnectedDisplay.AuthenticodeVerifier]::Verify((Resolve-Path -LiteralPath $Path).Path)
    $untrustedRoot = -2146762487 # CERT_E_UNTRUSTEDROOT (0x800B0109).
    if ($signature.Status -eq 'HashMismatch' -or ($result -ne 0 -and $result -ne $untrustedRoot)) {
        throw "Authenticode verification failed for ${Path}: native result $result / $($signature.Status)"
    }
    if ($result -eq $untrustedRoot) {
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.RevocationMode = 'NoCheck'
            $chain.ChainPolicy.VerificationFlags = 'AllowUnknownCertificateAuthority'
            if (!$chain.Build($signature.SignerCertificate) -or
                @($chain.ChainStatus | Where-Object { $_.Status -ne 'UntrustedRoot' }).Count) {
                throw "The signature has certificate errors other than an untrusted self-signed root: $Path"
            }
        } finally { $chain.Dispose() }
    }
    if ($RequireTimestamp -and !$signature.TimeStamperCertificate) {
        throw "The signed release has no timestamp: $Path"
    }
}

function Invoke-ReleaseSigning {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Thumbprint,
        [string]$TimestampUrl = 'http://timestamp.digicert.com',
        [switch]$WithoutTimestamp
    )
    $certificate = Get-ReleaseSigningCertificate -Thumbprint $Thumbprint
    try {
        $arguments = @{
            LiteralPath = $Path
            Certificate = $certificate
            HashAlgorithm = 'SHA256'
            ErrorAction = 'Stop'
        }
        if (!$WithoutTimestamp) {
            if ($TimestampUrl -notmatch '^https?://[A-Za-z0-9./:_-]+$') {
                throw 'A valid HTTP(S) timestamp server URL is required.'
            }
            $arguments.TimestampServer = $TimestampUrl
        }
        Set-AuthenticodeSignature @arguments | Out-Null
        Assert-ReleaseSignature -Path $Path -Thumbprint $certificate.Thumbprint -RequireTimestamp:(!$WithoutTimestamp)
    } finally { $certificate.Dispose() }
}

if ($FilePath) {
    Invoke-ReleaseSigning -Path $FilePath -Thumbprint $CertificateThumbprint -TimestampUrl $TimestampServer
}
