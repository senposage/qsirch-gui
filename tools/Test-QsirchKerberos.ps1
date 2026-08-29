Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# This is intentionally a fixed, read-only test for the DIMLAW Qsirch host.
$NasHost = "DRK-NAS9B372E"
$Port = 443
$Query = "wind"

# Windows PowerShell 5.1 can otherwise offer obsolete TLS versions first. QTS
# commonly requires TLS 1.2 on its HTTPS listener.
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$invokeWebRequestParameters = (Get-Command Invoke-WebRequest).Parameters
$skipCertificateWithCommand = $invokeWebRequestParameters.ContainsKey("SkipCertificateCheck")

if (-not $skipCertificateWithCommand) {
    # Windows PowerShell 5.1 has no -SkipCertificateCheck switch. This applies only
    # to this process and lets an administrator test a trusted NAS with a self-signed certificate.
    if ($null -eq ("QsirchKerberosTest.CertificatePolicy" -as [type])) {
        Add-Type @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace QsirchKerberosTest
{
    public static class CertificatePolicy
    {
        public static bool Accept(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            return true;
        }
    }
}
"@
    }

    $certificateCallback = [System.Delegate]::CreateDelegate(
        [System.Net.Security.RemoteCertificateValidationCallback],
        [QsirchKerberosTest.CertificatePolicy].GetMethod("Accept"))
    [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $certificateCallback
}

$escapedQuery = [Uri]::EscapeDataString($Query)
$endpoint = "https://$NasHost`:$Port/qsirch/latest/api/search?q=$escapedQuery&limit=1&offset=0&advanced_mode=0&store_history=0"

function Invoke-QsirchProbe {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$UseDefaultCredentials
    )

    $requestArguments = @{
        Uri = $endpoint
        Method = "Get"
        TimeoutSec = 20
        ErrorAction = "Stop"
    }

    if ($invokeWebRequestParameters.ContainsKey("UseBasicParsing")) {
        $requestArguments.UseBasicParsing = $true
    }

    if ($skipCertificateWithCommand) {
        $requestArguments.SkipCertificateCheck = $true
    }

    if ($UseDefaultCredentials) {
        $requestArguments.UseDefaultCredentials = $true
    }

    try {
        $response = Invoke-WebRequest @requestArguments
    }
    catch [System.Net.WebException] {
        if ($null -eq $_.Exception.Response) {
            $detail = $_.Exception.Message
            if ($null -ne $_.Exception.InnerException) {
                $detail = "$detail $($_.Exception.InnerException.Message)"
            }
            throw "Could not complete the HTTPS handshake with $NasHost`:$Port. $detail"
        }

        $response = $_.Exception.Response
    }

    $headers = $response.Headers
    [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        WwwAuthenticate = [string]$headers["WWW-Authenticate"]
        Server = [string]$headers["Server"]
    }
}

Write-Host "Qsirch Windows-integrated authentication probe" -ForegroundColor Cyan
Write-Host "Endpoint: $endpoint"
Write-Host "Windows identity: $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Write-Host ""

Write-Host "1. Checking the unauthenticated API challenge..."
$anonymous = Invoke-QsirchProbe -UseDefaultCredentials $false
Write-Host "   Status: $($anonymous.StatusCode)"
Write-Host "   WWW-Authenticate: $($anonymous.WwwAuthenticate)"

Write-Host "2. Retrying with the current Windows domain identity..."
$integrated = Invoke-QsirchProbe -UseDefaultCredentials $true
Write-Host "   Status: $($integrated.StatusCode)"
Write-Host "   WWW-Authenticate: $($integrated.WwwAuthenticate)"
Write-Host ""

$advertisesIntegratedAuth = $anonymous.WwwAuthenticate -match "(?i)\b(negotiate|kerberos|ntlm)\b"

if ($integrated.StatusCode -ge 200 -and $integrated.StatusCode -lt 300) {
    Write-Host "Success: Qsirch accepted the current Windows identity without a password." -ForegroundColor Green
    exit 0
}

if (-not $advertisesIntegratedAuth) {
    Write-Host "Result: Qsirch did not advertise Negotiate, Kerberos, or NTLM on this endpoint." -ForegroundColor Yellow
    Write-Host "A domain ticket cannot be used unless the NAS explicitly offers an integrated-authentication challenge."
    exit 2
}

Write-Host "Result: Qsirch advertised integrated authentication, but the current Windows identity was not accepted." -ForegroundColor Yellow
Write-Host "Check the NAS domain join, HTTP service principal name, user permissions, and whether Qsirch itself supports the negotiated identity."
exit 1
