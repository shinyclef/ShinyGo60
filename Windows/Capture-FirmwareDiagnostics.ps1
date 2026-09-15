[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^COM[0-9]+$')]
    [string] $Port,

    [ValidateRange(1, 86400)]
    [int] $Seconds = 600,

    [string] $LogPath = (Join-Path $PSScriptRoot "..\Output\FirmwareDiagnostics\$Port-$(Get-Date -Format 'yyyyMMdd-HHmmss').log")
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Ports
$resolvedLogPath = [System.IO.Path]::GetFullPath($LogPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedLogPath) | Out-Null
$serial = [System.IO.Ports.SerialPort]::new($Port, 115200)
$serial.DtrEnable = $true
$serial.ReadTimeout = 1000
$serial.NewLine = "`n"
$writer = [System.IO.StreamWriter]::new($resolvedLogPath, $true)
$writer.AutoFlush = $true
$capturedLines = 0

try {
    $serial.Open()
    Write-Host "Capturing ShinyGo60 connection diagnostics from $Port for $Seconds seconds."
    Write-Host "Log: $resolvedLogPath"
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
        try {
            $line = $serial.ReadLine().TrimEnd("`r")
        }
        catch [System.TimeoutException] {
            continue
        }

        # Accept only our connection modules, never arbitrary console or key data.
        if ($line -notmatch '<(?:inf|wrn|err)> shinygo60_(?:connection|ble):') {
            continue
        }

        $entry = "$([DateTimeOffset]::UtcNow.ToString('O')) $line"
        $writer.WriteLine($entry)
        Write-Host $entry
        $capturedLines++
    }
}
finally {
    $serial.Dispose()
    $writer.Dispose()
}

if ($capturedLines -eq 0) {
    Write-Warning 'No diagnostic lines arrived. Check the console COM port and diagnostic firmware; the boot-state line repeats every 60 seconds.'
}
