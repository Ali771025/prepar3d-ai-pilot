[CmdletBinding()]
param([string]$SdkRoot=$env:P3D_SDK_ROOT,[string]$ToolchainRoot=(Join-Path $env:LOCALAPPDATA 'P3DAIPilot\BuildTools\v143'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $SdkRoot){$SdkRoot=(Get-ChildItem "$env:ProgramFiles\Lockheed Martin" -Directory -Filter 'Prepar3D v4 SDK*' | Sort-Object Name -Descending | Select-Object -First 1).FullName}
if(-not(Test-Path "$SdkRoot\inc\PDK\Pdk.h")){throw 'Actual installed P3D v4 SDK required; pass -SdkRoot.'}
$vc=(Get-ChildItem "$ToolchainRoot\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkInclude=(Get-ChildItem "$ToolchainRoot\sdk\cpp\c\Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkLib=Join-Path $ToolchainRoot 'sdk\x64\c'
$cl=Join-Path $vc 'bin\Hostx64\x64\cl.exe'
$sig=Get-AuthenticodeSignature $cl
if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw 'Microsoft compiler signature must verify.'}
$env:PATH="$vc\bin\Hostx64\x64;$env:PATH"
$env:INCLUDE="$vc\include;$vc\atlmfc\include;$sdkInclude\ucrt;$sdkInclude\shared;$sdkInclude\um;$sdkInclude\winrt"
$env:LIB="$vc\lib\x64;$vc\atlmfc\lib\x64;$sdkLib\ucrt\x64;$sdkLib\um\x64;$vc\lib\onecore\x64"
$env:VSLANG='1033'
$out=Join-Path $root 'artifacts\pdk'
$obj=Join-Path $root 'artifacts\obj\pdk\portable'
New-Item $out,$obj -ItemType Directory -Force | Out-Null
& $cl /nologo /LD /MT /O2 /EHsc /std:c++17 /W4 /DWIN32_LEAN_AND_MEAN /DNOMINMAX /DUNICODE /D_UNICODE /DNDEBUG "/I$SdkRoot\inc\PDK" "/I$SdkRoot\inc\PDK\Helpers" "/Fo$obj\Plugin.obj" "$root\src\native\P3D.AIPilot.PdkBridge\Plugin.cpp" /link /MACHINE:X64 "/OUT:$out\P3D.AIPilot.PdkBridge.dll" "/IMPLIB:$obj\P3D.AIPilot.PdkBridge.lib" advapi32.lib ole32.lib user32.lib uuid.lib
if($LASTEXITCODE -ne 0){throw "Native compiler/linker failed ($LASTEXITCODE)."}
Copy-Item "$root\src\native\P3D.AIPilot.PdkBridge\PdkBridge.ini" $out -Force
Copy-Item "$root\deployment\pdk\add-on.xml" $out -Force
& "$vc\bin\Hostx64\x64\dumpbin.exe" /headers /exports /dependents "$out\P3D.AIPilot.PdkBridge.dll"
if($LASTEXITCODE -ne 0){throw 'Native PE inspection failed.'}
