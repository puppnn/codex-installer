Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $repository 'src\CodexUpdater.App\Vendor'
$pin = Get-Content -LiteralPath (Join-Path $vendor 'WebView2Bootstrapper.json') -Raw | ConvertFrom-Json
$destination = Join-Path $vendor 'MicrosoftEdgeWebview2Setup.exe'
$uri = [Uri]$pin.url
if ($uri.Scheme -ne 'https' -or -not $uri.Host.EndsWith('.delivery.mp.microsoft.com') -or $pin.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Invalid WebView2 bootstrapper pin.'
}

function Test-Bootstrapper([string]$Path) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $pin.sha256) {
        throw 'WebView2 bootstrapper SHA-256 mismatch. Do not bypass verification.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
        throw 'WebView2 bootstrapper does not have a valid Microsoft signature.'
    }
}

if (Test-Path -LiteralPath $destination) {
    Test-Bootstrapper $destination
} else {
    $temporary = Join-Path $vendor ([Guid]::NewGuid().ToString('N') + '.download')
    try {
        Invoke-WebRequest -Uri $uri -OutFile $temporary -MaximumRedirection 5 -TimeoutSec 90
        Test-Bootstrapper $temporary
        Move-Item -LiteralPath $temporary -Destination $destination
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Write-Host "Verified Microsoft WebView2 bootstrapper $($pin.version)."
