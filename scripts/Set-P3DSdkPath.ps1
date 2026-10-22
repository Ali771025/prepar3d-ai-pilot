param(
    [Parameter(Mandatory = $true)]
    [string]$SdkRoot
)

$dll = Join-Path $SdkRoot "lib\SimConnect\managed\LockheedMartin.Prepar3D.SimConnect.dll"
if (-not (Test-Path $dll)) {
    throw "SimConnect managed DLL was not found at: $dll"
}

[Environment]::SetEnvironmentVariable("P3D_SDK_ROOT", $SdkRoot, "User")
$env:P3D_SDK_ROOT = $SdkRoot
Write-Host "P3D_SDK_ROOT configured for this process and the current user."
