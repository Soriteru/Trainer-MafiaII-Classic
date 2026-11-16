$ErrorActionPreference = 'Stop'
& 'C:\Program Files\dotnet\dotnet.exe' build "$PSScriptRoot\MafiaII-SoloTrainer.csproj" -c Release --ignore-failed-sources -o "$PSScriptRoot\app" -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée' }
