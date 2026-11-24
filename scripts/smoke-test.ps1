param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\YtbDownloader\bin\Release\net9.0-windows\win-x64\publish\YtbDownloader.exe'),
    [int]$Seconds = 5
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Write-Output "Exe: $Exe"

$process = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds $Seconds

if ($process.HasExited) {
    throw "YtbDownloader exited early (code=$($process.ExitCode))"
}

Stop-Process -Id $process.Id -Force
Write-Output "OK: YtbDownloader ran for $Seconds seconds (pid=$($process.Id))."
