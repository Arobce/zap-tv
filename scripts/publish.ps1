<#
.SYNOPSIS
    Builds ZapTV for release: a portable folder and a Velopack installer, per architecture.

.DESCRIPTION
    Publishes self-contained with ReadyToRun, verifies that the native binary in the output
    matches the architecture it was published for, zips the portable folder, and hands the
    result to `vpk` to produce an installer and a delta-capable release.

    The architecture check is not a formality. Decision 0003 records the failure it exists
    to prevent: the x64 libmpv-2.dll in an ARM64 package fails at load time with an error
    that reads as a missing dependency rather than an architecture mismatch, so it would
    otherwise ship and be diagnosed as something else entirely.

    Not signed. OV and EV code signing certificates both require hardware or HSM key
    storage, so signing needs a token this script cannot conjure. Pass -SigningParams to
    forward arguments to vpk once one exists; without it, expect SmartScreen warnings.

.PARAMETER Version
    The version to stamp. Velopack orders releases by it, so it must increase.

.PARAMETER Rid
    win-x64, win-arm64, or both.

.PARAMETER PortableOnly
    Skip vpk and produce only the folder and its zip. Useful without the tool installed.

.PARAMETER SigningParams
    Passed through to vpk verbatim, for example:
      -SigningParams '--signTemplate "signtool sign /fd sha256 {{file}}"'

.EXAMPLE
    ./scripts/publish.ps1 -Version 0.1.0
    ./scripts/publish.ps1 -Version 0.2.0 -Rid both
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidateSet('win-x64', 'win-arm64', 'both')]
    [string] $Rid = 'win-x64',

    [switch] $PortableOnly,

    [string] $SigningParams = ''
)

$ErrorActionPreference = 'Stop'

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Artifacts = Join-Path $Root 'artifacts'
$PackId = 'ZapTV'
$VpkVersion = '1.2.0'

# Kept in step with the Velopack PackageVersion in Directory.Packages.props. The two halves
# write and read the same package format, and a mismatch produces a release that installs
# and then cannot update itself - which is discovered one release later, by users.
$PlatformOf = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }
$MachineOf = @{ 'win-x64' = 'x64'; 'win-arm64' = 'ARM64' }

function Get-PeMachine {
    <#
        Reads the PE header's machine field. The file extension and the folder it sits in
        say nothing about what is actually inside, and the whole point of this check is
        that the wrong binary looks right everywhere except at load time.
    #>
    param([string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        $machine = $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }

    switch ($machine) {
        0x8664 { 'x64' }
        0xAA64 { 'ARM64' }
        0x014C { 'x86' }
        default { 'unknown 0x{0:X4}' -f $machine }
    }
}

function Publish-Rid {
    param([string] $TargetRid)

    $platform = $PlatformOf[$TargetRid]
    $libmpv = Join-Path $Root ".local\native\mpv\$TargetRid\libmpv-2.dll"

    if (-not (Test-Path $libmpv)) {
        throw "No libmpv-2.dll for $TargetRid. Fetch it first: " +
              "pwsh ./scripts/fetch-libmpv.ps1 -Rid $TargetRid"
    }

    $publishDir = Join-Path $Artifacts "publish\$TargetRid"

    if (Test-Path $publishDir) {
        # Cleared rather than published over. A stale file from a previous run is not
        # overwritten by a build that no longer produces it, and it would then be shipped.
        Remove-Item $publishDir -Recurse -Force
    }

    Write-Host ""
    Write-Host "== $TargetRid : publishing $Version ==" -ForegroundColor Cyan

    # WindowsAppSDKSelfContained so the installed app does not also require the Windows App
    # SDK runtime. An HTPC user unzipping a portable folder has no installer to pull it in.
    & dotnet publish (Join-Path $Root 'src\Iptv.App\Iptv.App.csproj') `
        --configuration Release `
        --runtime $TargetRid `
        --self-contained true `
        -p:Platform=$platform `
        -p:PublishReadyToRun=true `
        -p:WindowsAppSDKSelfContained=true `
        -p:Version=$Version `
        --output $publishDir | Out-Host

    # Out-Host, not the default. A PowerShell function returns everything written to the
    # pipeline, so without it the build log becomes part of this function's return value
    # and the caller receives a few hundred lines of MSBuild where it expected a path.
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $TargetRid"
    }

    Assert-Payload -TargetRid $TargetRid -PublishDir $publishDir

    $size = [math]::Round(
        ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)

    Write-Host "$TargetRid : published, $size MB"

    return $publishDir
}

function Assert-Payload {
    <#
        Everything that must be true of the folder before it is allowed to become a
        release. Each of these has a failure mode that is silent until a user hits it.
    #>
    param([string] $TargetRid, [string] $PublishDir)

    $exe = Join-Path $PublishDir 'Iptv.App.exe'
    if (-not (Test-Path $exe)) {
        throw "$TargetRid : no Iptv.App.exe in the publish output"
    }

    $dll = Join-Path $PublishDir 'libmpv-2.dll'
    if (-not (Test-Path $dll)) {
        throw "$TargetRid : libmpv-2.dll was not copied into the publish output. " +
              "Without it the app starts and plays nothing."
    }

    # The application's own PRI, which the MSIX tooling would normally add to the publish
    # set and this project does not use. Missing, the app exits during startup with a
    # stowed WinRT exception before it can log anything, so it is checked here rather than
    # discovered by whoever installs the release.
    $pri = Join-Path $PublishDir 'Iptv.App.pri'
    if (-not (Test-Path $pri)) {
        throw "$TargetRid : Iptv.App.pri is missing from the publish output. " +
              "The app will exit at startup with 0xC000027B. See the PublishApplicationPri " +
              "target in src/Iptv.App/Iptv.App.csproj."
    }

    $expected = $MachineOf[$TargetRid]

    foreach ($file in @($exe, $dll)) {
        $actual = Get-PeMachine $file
        if ($actual -ne $expected) {
            throw "$TargetRid : $(Split-Path $file -Leaf) is $actual, expected $expected. " +
                  "See docs/decisions/0003-arm64-feasibility.md - this fails at load time " +
                  "with an error that reads as a missing dependency."
        }
    }

    Write-Host "$TargetRid : payload verified ($expected exe, $expected libmpv)"
}

function New-Portable {
    <#
        Only for -PortableOnly. vpk builds a portable zip of its own, and that one is
        better: it carries the updater stub, so a portable install can still update itself.
        Shipping both would mean two zips with nearly the same name and a difference nobody
        can see from the outside.
    #>
    param([string] $TargetRid, [string] $PublishDir)

    $zip = Join-Path $Artifacts "ZapTV-$Version-$TargetRid-portable.zip"

    if (Test-Path $zip) { Remove-Item $zip -Force }

    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $zip -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "$TargetRid : portable zip, $size MB -> $zip"
}

function Copy-Artifacts {
    <#
        vpk names its output by architecture but not by version, so a second release
        overwrites the first in place. Copied out under the version, which is what a
        release page needs and what tells two downloads apart.
    #>
    param([string] $TargetRid, [string] $ReleasesDir)

    $renames = @{
        'ZapTV-win-Setup.exe'    = "ZapTV-$Version-$TargetRid-Setup.exe"
        'ZapTV-win-Portable.zip' = "ZapTV-$Version-$TargetRid-portable.zip"
    }

    foreach ($source in $renames.Keys) {
        $from = Join-Path $ReleasesDir $source
        if (Test-Path $from) {
            $to = Join-Path $Artifacts $renames[$source]
            Copy-Item $from $to -Force

            $size = [math]::Round((Get-Item $to).Length / 1MB, 1)
            Write-Host "$TargetRid : $($renames[$source]) ($size MB)"
        }
    }
}

function New-Installer {
    param([string] $TargetRid, [string] $PublishDir)

    if (-not (Get-Command 'vpk' -ErrorAction SilentlyContinue)) {
        throw "vpk not found. Install it: dotnet tool install -g vpk --version $VpkVersion"
    }

    $releases = Join-Path $Artifacts "releases\$TargetRid"
    New-Item -ItemType Directory -Path $releases -Force | Out-Null

    $arguments = @(
        'pack'
        '--packId', $PackId
        '--packVersion', $Version
        '--packDir', $PublishDir
        '--mainExe', 'Iptv.App.exe'
        '--packTitle', 'ZapTV'
        '--runtime', $TargetRid
        '--outputDir', $releases
    )

    if ($SigningParams) {
        # Split rather than passed as one string: PowerShell would otherwise hand vpk a
        # single argument containing spaces, which it reads as one malformed option.
        $arguments += ($SigningParams -split ' (?=(?:[^"]*"[^"]*")*[^"]*$)')
    }
    else {
        Write-Warning "$TargetRid : building unsigned. SmartScreen will warn on first run."
    }

    & vpk @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack failed for $TargetRid"
    }

    Write-Host "$TargetRid : installer and release feed -> $releases"

    Copy-Artifacts -TargetRid $TargetRid -ReleasesDir $releases
}

$targets = if ($Rid -eq 'both') { @('win-x64', 'win-arm64') } else { @($Rid) }

New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null

foreach ($target in $targets) {
    $publishDir = Publish-Rid -TargetRid $target

    if ($PortableOnly) {
        New-Portable -TargetRid $target -PublishDir $publishDir
    }
    else {
        New-Installer -TargetRid $target -PublishDir $publishDir
    }
}

Write-Host ""
Write-Host "Done. Artifacts in $Artifacts" -ForegroundColor Green
