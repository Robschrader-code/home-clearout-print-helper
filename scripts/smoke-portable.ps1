param([string]$Executable = (Join-Path $PSScriptRoot '../artifacts/publish/HomeClearout.PrintHelper.exe'), [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/portable-smoke'))
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$taskDotnetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Join-Path $outputPath 'no-installed-runtime'
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList @('--diagnostics', ('"' + $outputPath + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw 'Portable executable did not start successfully.' }
    $result = Get-Content -LiteralPath (Join-Path $outputPath 'portable-startup.json') -Raw | ConvertFrom-Json
    if (-not $result.is64Bit -or -not $result.runtimeFromBundle) { throw 'Expected Windows x64 with a bundled runtime.' }
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath 'portable-label.png'))) { throw 'Portable renderer produced no label.' }
    Write-Output 'Portable executable starts, loads its bundled x64 runtime, detects Windows printers and renders a label.'
}
finally { $env:DOTNET_ROOT = $taskDotnetRoot }
