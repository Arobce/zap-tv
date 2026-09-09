<#
.SYNOPSIS
    Downloads libmpv-2.dll for one or both shipping architectures.

.DESCRIPTION
    libmpv-2.dll is 120MB and is not committed. This fetches it from shinchiro's
    mpv-winbuild-cmake releases into .local/native/mpv/<rid>/, which is where the build
    and the packaging script look for it.

    The release is PINNED. Decision 0001 established that the render API surface is a
    property of the build - it offers "opengl" and "sw" and no D3D11 - and re-running that
    inventory is the condition for moving to a newer one. Floating on "latest" would move
    the binary underneath that check.

    The asset name carries a git hash that changes with every build, so the tag is pinned
    and the asset is matched by shape rather than spelled out in full.

.PARAMETER Rid
    win-x64, win-arm64, or both. Defaults to the architecture of this machine, because
    that is the one a developer needs to actually run anything.

.PARAMETER Force
    Re-download even when the file is already there.

.EXAMPLE
    ./scripts/fetch-libmpv.ps1
    ./scripts/fetch-libmpv.ps1 -Rid both
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'both', 'host')]
    [string] $Rid = 'host',

    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# Pinned. See the note above before changing it, and re-run the render API inventory in
# docs/decisions/0001-libmpv-render-api.md when you do.
$Tag = '20260903'
$Repo = 'shinchiro/mpv-winbuild-cmake'

$Architectures = @{
    'win-x64'   = 'x86_64'
    'win-arm64' = 'aarch64'
}

function Resolve-HostRid {
    # ARM64 Windows reports its own architecture here, and running x64 code under
    # emulation would still report ARM64 for the OS. The process architecture is what
    # decides which native DLL can be loaded, so that is what is asked.
    switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'Arm64' { 'win-arm64' }
        default { 'win-x64' }
    }
}

function Get-SevenZip {
    foreach ($candidate in '7z', '7za', '7zr') {
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($found) { return $found.Source }
    }

    $installed = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
    if (Test-Path $installed) { return $installed }

    throw "7-Zip is required to unpack the mpv archive and was not found. " +
          "Install it (winget install 7zip.7zip) and run this again."
}

function Get-LibMpv {
    param([string] $TargetRid)

    $arch = $Architectures[$TargetRid]
    $root = Join-Path $PSScriptRoot '..\.local\native\mpv' | Resolve-Path -ErrorAction SilentlyContinue
    if (-not $root) {
        $root = Join-Path $PSScriptRoot '..\.local\native\mpv'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
    }

    $destination = Join-Path $root $TargetRid
    $dll = Join-Path $destination 'libmpv-2.dll'

    if ((Test-Path $dll) -and -not $Force) {
        $size = [math]::Round((Get-Item $dll).Length / 1MB, 1)
        Write-Host "$TargetRid : already present ($size MB). Use -Force to replace."
        return
    }

    Write-Host "$TargetRid : resolving $Repo release $Tag..."

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" `
                                 -Headers @{ 'User-Agent' = 'zaptv-build' }

    # Matched on the full shape rather than a prefix: mpv-dev-x86_64-v3-* is a separate
    # asset and a prefix match on mpv-dev-x86_64- would pick whichever came first.
    $asset = $release.assets | Where-Object { $_.name -like "mpv-dev-$arch-$Tag-git-*.7z" } | Select-Object -First 1

    if (-not $asset) {
        throw "No mpv-dev-$arch asset in release $Tag. Available: " +
              (($release.assets | ForEach-Object { $_.name }) -join ', ')
    }

    $archive = Join-Path ([System.IO.Path]::GetTempPath()) $asset.name

    Write-Host "$TargetRid : downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)..."

    # ProgressPreference silenced: Invoke-WebRequest's progress bar makes a large download
    # roughly an order of magnitude slower, which is a documented quirk rather than a
    # cosmetic preference.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null

    Write-Host "$TargetRid : extracting..."

    $sevenZip = Get-SevenZip

    # Only what is needed. The dev archive also carries headers and an import library,
    # neither of which the build uses: the P/Invoke is declared in C# and there is nothing
    # to link against.
    & $sevenZip e $archive "-o$destination" 'libmpv-2.dll' -y | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip failed with exit code $LASTEXITCODE"
    }

    if (-not (Test-Path $dll)) {
        throw "Extraction produced no libmpv-2.dll in $destination"
    }

    Remove-Item $archive -Force -ErrorAction SilentlyContinue

    $size = [math]::Round((Get-Item $dll).Length / 1MB, 1)
    Write-Host "$TargetRid : done ($size MB) -> $dll"
}

$targets = switch ($Rid) {
    'both' { @('win-x64', 'win-arm64') }
    'host' { @(Resolve-HostRid) }
    default { @($Rid) }
}

foreach ($target in $targets) {
    Get-LibMpv -TargetRid $target
}
