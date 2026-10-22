[CmdletBinding()]
param([string]$SdkRoot=$env:P3D_SDK_ROOT)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $SdkRoot){
 $parent=Join-Path $env:ProgramFiles 'Lockheed Martin'
 if(Test-Path $parent){$candidate=Get-ChildItem $parent -Directory -Filter 'Prepar3D v4 SDK*' | Sort-Object Name -Descending | Select-Object -First 1; if($candidate){$SdkRoot=$candidate.FullName}}
}
if(-not $SdkRoot -or -not(Test-Path (Join-Path $SdkRoot 'lib\SimConnect\managed\LockheedMartin.Prepar3D.SimConnect.dll'))){throw 'Pass -SdkRoot with the actual installed Prepar3D v4 SDK.'}
$env:P3D_SDK_ROOT=$SdkRoot
foreach($entry in @(@('src\P3DAIPilot.P3Dv4','compatibility'),@('tools\SimConnect.SmokeTest','tools\SimConnect.SmokeTest'),@('tools\FSUIPC.OffsetTest','tools\FSUIPC.OffsetTest'))){
 & dotnet build (Join-Path $root $entry[0]) -c Release -p:NuGetAudit=false -o (Join-Path $root ('artifacts\'+$entry[1]))
 if($LASTEXITCODE -ne 0){throw "Compatibility build failed: $($entry[0]) exit=$LASTEXITCODE"}
}
