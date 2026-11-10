<#
.SYNOPSIS
    Builds the same release artifacts locally that the GitHub Actions release workflow builds.

.DESCRIPTION
    Publishes the self-contained single file, packs the portable zip and (when Inno Setup is
    installed) compiles the installer. Everything lands in dist\.

    The in-app updater compares the running version with the newest GitHub release, so the
    version you pass here must match the tag you publish.

.PARAMETER Version
    Version to stamp into the build, without a leading v (for example 1.2.3).

.PARAMETER SkipInstaller
    Build only the portable zip, even if Inno Setup is installed.

.EXAMPLE
    .\scripts\build-release.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $numeric = ($Version -split '[-+]')[0]
    $parts = @($numeric -split '\.')
    while ($parts.Count -lt 3) { $parts += '0' }
    $assembly = (($parts[0..2]) -join '.') + '.0'

    Write-Host "Building $Version (assembly $assembly)" -ForegroundColor Cyan

    # --- publish -------------------------------------------------------------
    Remove-Item publish, dist -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish src\DataverseToSql\DataverseToSql.csproj `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -p:AssemblyVersion=$assembly `
        -p:FileVersion=$assembly `
        -p:InformationalVersion=$Version `
        -o publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Path dist -Force | Out-Null

    # --- portable zip --------------------------------------------------------
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "DataverseToSql-portable-$Version"
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path "$staging\scripts" -Force | Out-Null
    Copy-Item publish\DataverseToSql.exe $staging
    Copy-Item README.md, LICENSE, settings.sample.json $staging -ErrorAction SilentlyContinue
    Copy-Item scripts\*.sql "$staging\scripts" -ErrorAction SilentlyContinue
    @"
Dataverse to SQL Sync $Version - portable
=========================================

Nothing to install. Put DataverseToSql.exe in a folder you can write to,
for example C:\Tools\DataverseToSql, and double-click it.

Keep it in a writable folder: the Updates button in the app replaces this
executable in place, which it cannot do under C:\Program Files.

Headless run:  DataverseToSql.exe --run [path\to\settings.json]
Configurations: %LOCALAPPDATA%\DataverseToSql
"@ | Set-Content -Path "$staging\README-portable.txt" -Encoding utf8

    $zip = "dist\DataverseToSql-$Version-portable-win-x64.zip"
    Compress-Archive -Path "$staging\*" -DestinationPath $zip -CompressionLevel Optimal -Force
    Remove-Item $staging -Recurse -Force
    Write-Host "  $zip  ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)" -ForegroundColor Green

    # --- installer -----------------------------------------------------------
    if (-not $SkipInstaller) {
        $iscc = Get-ChildItem `
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", `
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
        if (-not $iscc) {
            Write-Warning "Inno Setup 6 not found, so no installer was built. Install it with: winget install JRSoftware.InnoSetup"
        }
        else {
            Remove-Item installer\Output -Recurse -Force -ErrorAction SilentlyContinue
            & $iscc "/DAppVersion=$numeric" "/DSourceExe=..\publish\DataverseToSql.exe" "installer\DataverseToSql.iss"
            if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
            $built = Get-ChildItem installer\Output\*.exe | Select-Object -First 1
            $setup = "dist\DataverseToSql-$Version-Setup.exe"
            Copy-Item $built.FullName $setup -Force
            Write-Host "  $setup  ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)" -ForegroundColor Green
        }
    }

    # --- checksums -----------------------------------------------------------
    Get-ChildItem dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
    } | Tee-Object -Variable sums | Out-Null
    $sums | Set-Content dist\SHA256SUMS.txt -Encoding ascii

    Write-Host ""
    Write-Host "Done. Artifacts in dist\:" -ForegroundColor Cyan
    Get-ChildItem dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
    Write-Host "Publish them with:" -ForegroundColor Cyan
    Write-Host "  git tag v$Version && git push origin v$Version     # CI builds and releases, or"
    Write-Host "  gh release create v$Version dist\* --title `"Dataverse to SQL Sync $Version`" --generate-notes"
}
finally {
    Pop-Location
}
