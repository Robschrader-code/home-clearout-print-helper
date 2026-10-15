param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/publish'), [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/HomeClearout.PrintHelper/HomeClearout.PrintHelper.csproj'
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publication failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../QUICKSTART.txt'),(Join-Path $PSScriptRoot '../THIRD-PARTY-NOTICES.txt'),(Join-Path $PSScriptRoot '../licenses/DOTNET-LICENSE.txt'),(Join-Path $PSScriptRoot '../licenses/DOTNET-THIRD-PARTY-NOTICES.txt') -Destination $OutputDirectory
$exe = Join-Path $OutputDirectory 'HomeClearout.PrintHelper.exe'
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  HomeClearout.PrintHelper.exe" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
$zip = Join-Path (Split-Path $OutputDirectory -Parent) 'HomeClearout-PrintHelper-v0.1.0-win-x64.zip'
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $zip -Force
Write-Output $zip
