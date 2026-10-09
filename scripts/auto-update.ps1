<#
.SYNOPSIS
    Updates the Revit MCP add-in to the latest GitHub release after Revit exits.

.DESCRIPTION
    Started hidden by the add-in when Revit shuts down. Waits until no Revit
    process is left (Revit keeps the add-in DLLs locked while it runs), compares
    the latest release tag with revit_mcp_plugin\version.txt, downloads the ZIP
    for this Revit year, checks it against the published .sha256 file and copies
    it over the installation. version.txt is written last, so an interrupted
    update is retried on the next exit. Source builds have no version.txt and are
    never touched.

.PARAMETER Year
    Revit version year, e.g. 2025.

.PARAMETER PluginDir
    The installed revit_mcp_plugin folder.
#>
param(
    [Parameter(Mandatory = $true)][string]$Year,
    [Parameter(Mandatory = $true)][string]$PluginDir
)

$ErrorActionPreference = 'Stop'
$REPO = 'Kurill/revit-mcp-server'

$logDir = Join-Path $PluginDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir ("update-{0:yyyy-MM-dd}.log" -f (Get-Date))
function Log([string]$msg) {
    Add-Content -Path $logFile -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $msg)
}

$mutex = New-Object System.Threading.Mutex($false, "RevitMcpAutoUpdate_$Year")
if (-not $mutex.WaitOne(0)) { exit 0 }

$work = Join-Path $env:TEMP "revit-mcp-update-$Year"
try {
    $versionFile = Join-Path $PluginDir 'version.txt'
    if (-not (Test-Path $versionFile)) { exit 0 }
    $installed = (Get-Content $versionFile -Raw).Trim()

    # Any running Revit may have this year's add-in loaded.
    $deadline = (Get-Date).AddMinutes(10)
    while (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { Log "Revit still running after 10 min; will retry on next exit"; exit 0 }
        Start-Sleep -Seconds 5
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'revit-mcp-auto-update' }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$REPO/releases/latest" -Headers $headers -TimeoutSec 30
    $tag = $release.tag_name
    if ($tag -eq $installed) { exit 0 }

    $zipName = "mcp-servers-for-revit-$tag-Revit$Year.zip"
    $zipAsset = $release.assets | Where-Object { $_.name -eq $zipName }
    $shaAsset = $release.assets | Where-Object { $_.name -eq "$zipName.sha256" }
    if (-not $zipAsset -or -not $shaAsset) { Log "Release $tag has no $zipName with a .sha256; skipping"; exit 0 }

    Log "Updating $installed -> $tag"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zipPath = Join-Path $work $zipName
    $shaPath = "$zipPath.sha256"
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -Headers $headers -UseBasicParsing -TimeoutSec 600
    Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaPath -Headers $headers -UseBasicParsing -TimeoutSec 60

    $expected = ((Get-Content $shaPath -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { Log "SHA-256 mismatch for $zipName; not installing"; exit 1 }

    $extract = Join-Path $work 'extract'
    Expand-Archive -Path $zipPath -DestinationPath $extract -Force
    $newPlugin = Join-Path $extract 'revit_mcp_plugin'
    $newAddin = Join-Path $extract 'mcp-servers-for-revit.addin'
    if (-not (Test-Path (Join-Path $newPlugin 'RevitMCPPlugin.dll')) -or -not (Test-Path $newAddin)) {
        Log "$zipName does not have the expected layout; not installing"; exit 1
    }

    # The bundled node.exe stays locked while an MCP client runs the server, so the
    # runtime is copied separately and a failure there keeps the old runtime.
    $runtimeRel = 'Commands\RevitMCPCommandSet\server\runtime'
    & robocopy $newPlugin $PluginDir /MIR /R:3 /W:5 /NP /NJH /NJS /XD logs (Join-Path $newPlugin $runtimeRel) (Join-Path $PluginDir $runtimeRel) /XF version.txt mcp-port.txt | Out-Null
    if ($LASTEXITCODE -ge 8) { Log "Copy failed (robocopy $LASTEXITCODE); will retry on next exit"; exit 1 }
    & robocopy (Join-Path $newPlugin $runtimeRel) (Join-Path $PluginDir $runtimeRel) /MIR /R:2 /W:5 /NP /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { Log "Node runtime is in use; kept the previous one" }

    Copy-Item $newAddin (Split-Path $PluginDir -Parent) -Force
    Set-Content -Path $versionFile -Value $tag -NoNewline
    Log "Updated to $tag"
}
catch {
    Log "Update failed: $($_.Exception.Message)"
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    $mutex.ReleaseMutex()
}
