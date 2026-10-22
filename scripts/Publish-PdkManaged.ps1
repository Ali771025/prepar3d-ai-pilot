$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
foreach($entry in @(@('src\gateway\P3D.AIPilot.Gateway\P3D.AIPilot.Gateway.csproj','gateway'),@('tools\P3D.Bridge.TestClient\P3D.Bridge.TestClient.csproj','tools\TestClient'),@('tools\FlightReplay\FlightReplay.csproj','tools\FlightReplay'))){
 & dotnet publish "$root\$($entry[0])" -c Release -r win-x64 --self-contained true -o "$root\artifacts\$($entry[1])"
 if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
}
