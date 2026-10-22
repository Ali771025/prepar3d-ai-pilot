[CmdletBinding()]
param([string]$Source=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\pdk'),[string]$Destination=(Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Prepar3D v4 Add-ons\P3D.AIPilot.PdkBridge'),[string]$BackupRoot=(Join-Path $env:LOCALAPPDATA 'P3DAIPilot\backups\pdk'))
$ErrorActionPreference='Stop'
if(Get-Process Prepar3D -ErrorAction SilentlyContinue){throw 'Close Prepar3D before installing the bridge.'}
foreach($file in @('P3D.AIPilot.PdkBridge.dll','PdkBridge.ini','add-on.xml')){if(-not(Test-Path (Join-Path $Source $file))){throw "Missing artifact: $file. Complete Build-PdkDelivery first."}}
$bytes=[IO.File]::ReadAllBytes((Join-Path $Source 'P3D.AIPilot.PdkBridge.dll'))
$pe=[BitConverter]::ToInt32($bytes,60)
if([BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664){throw 'Bridge DLL must be AMD64.'}
if(Test-Path $Destination){
 New-Item $BackupRoot -ItemType Directory -Force | Out-Null
 Copy-Item $Destination (Join-Path $BackupRoot ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))) -Recurse
}
New-Item $Destination -ItemType Directory -Force | Out-Null
foreach($file in @('P3D.AIPilot.PdkBridge.dll','add-on.xml')){Copy-Item (Join-Path $Source $file) (Join-Path $Destination $file) -Force}
# Existing installation settings are preserved; new installations default to controls OFF.
if(-not(Test-Path (Join-Path $Destination 'PdkBridge.ini'))){Copy-Item (Join-Path $Source 'PdkBridge.ini') $Destination}
Write-Host "Installed: $Destination. Launch P3D and enable the add-on; verify logs\pdk\bridge.jsonl first."
