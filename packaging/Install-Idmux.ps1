param(
    [string]$Configuration = 'Release',
    [int]$Port = 7878
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$cliOutput = Join-Path $repoRoot "src\Idmux.Cli\bin\$Configuration\net10.0-windows"
$daemonOutput = Join-Path $repoRoot "src\Idmux.Daemon\bin\$Configuration\net10.0-windows"
$sourceCli = Join-Path $cliOutput 'idmux.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\idmux'
$configDirectory = Join-Path $env:LOCALAPPDATA 'idmux'
$configFile = Join-Path $configDirectory 'config.json'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runKeyName = 'idmux-daemon'

if (-not (Test-Path $sourceCli)) {
    throw "Built CLI not found at $sourceCli. Run `dotnet build idmux.slnx -c $Configuration` first."
}

if (-not (Test-Path (Join-Path $daemonOutput 'idmux-daemon.exe'))) {
    throw "Built daemon not found under $daemonOutput. Run `dotnet build idmux.slnx -c $Configuration` first."
}

if ($Port -lt 1 -or $Port -gt 65535) {
    throw 'Port must be between 1 and 65535.'
}

function Stop-ExistingDaemon {
    try {
        & $sourceCli daemon stop --json | Out-Null
    }
    catch {
    }

    Start-Sleep -Seconds 1

    $runningDaemons = Get-Process idmux-daemon -ErrorAction SilentlyContinue
    foreach ($process in $runningDaemons) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }
}

function Update-UserPath {
    param(
        [string]$DirectoryToAdd
    )

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $parts = @()
    if (-not [string]::IsNullOrWhiteSpace($userPath)) {
        $parts = $userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }

    $alreadyPresent = $parts | Where-Object { $_.TrimEnd('\') -ieq $DirectoryToAdd.TrimEnd('\') }
    if (-not $alreadyPresent) {
        $parts += $DirectoryToAdd
        $newPath = ($parts -join ';').Trim(';')
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        if ($env:Path -notlike "*$DirectoryToAdd*") {
            $env:Path = "$DirectoryToAdd;$env:Path"
        }

        return $true
    }

    if ($env:Path -notlike "*$DirectoryToAdd*") {
        $env:Path = "$DirectoryToAdd;$env:Path"
    }

    return $false
}

function Write-ConfigFile {
    param(
        [string]$Path,
        [int]$WebPort
    )

    if (Test-Path $Path) {
        $config = Get-Content $Path -Raw | ConvertFrom-Json -Depth 20
    }
    else {
        $config = [pscustomobject]@{}
    }

    if (-not $config.PSObject.Properties['daemon']) {
        $config | Add-Member -NotePropertyName daemon -NotePropertyValue ([pscustomobject]@{})
    }

    if (-not $config.daemon.PSObject.Properties['webPort']) {
        $config.daemon | Add-Member -NotePropertyName webPort -NotePropertyValue $WebPort
    }
    else {
        $config.daemon.webPort = $WebPort
    }

    $json = $config | ConvertTo-Json -Depth 20
    Set-Content -Path $Path -Value ($json + [Environment]::NewLine)
}

Stop-ExistingDaemon

if (Test-Path $installDirectory) {
    Remove-Item -Recurse -Force $installDirectory
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

Copy-Item -Path (Join-Path $cliOutput '*') -Destination $installDirectory -Recurse -Force
Copy-Item -Path (Join-Path $daemonOutput '*') -Destination $installDirectory -Recurse -Force

Write-ConfigFile -Path $configFile -WebPort $Port
$pathUpdated = Update-UserPath -DirectoryToAdd $installDirectory

$startupCommand = '"' + (Join-Path $installDirectory 'idmux-daemon.exe') + '"'
New-Item -Path $runKeyPath -Force | Out-Null
New-ItemProperty -Path $runKeyPath -Name $runKeyName -Value $startupCommand -PropertyType String -Force | Out-Null

Start-Process -FilePath (Join-Path $installDirectory 'idmux-daemon.exe')

[pscustomobject]@{
    InstallDirectory = $installDirectory
    ConfigFile = $configFile
    StartupRunKey = $runKeyName
    StartupCommand = $startupCommand
    UserPathUpdated = $pathUpdated
    WebPort = $Port
} | ConvertTo-Json -Compress
