<#
.SYNOPSIS
    Publishes the worker, builds the MSI and produces the deployment ZIP in one command.

.DESCRIPTION
    Output lands in dist\:
        SslExpireNotify-v<version>.msi
        SslExpireNotify-v<version>-deploy.zip   (MSI + database scripts + README-DEPLOY.md)

    The version is read from <Version> in SslExpireNotify.Worker.csproj and passed to the installer,
    so it is only maintained in one place.

.PARAMETER Version
    Overrides the version from the csproj (e.g. for a hotfix build).

.PARAMETER Configuration
    Build configuration, Release by default.

.PARAMETER SkipTests
    Skips the unit tests. They run by default so a broken build never reaches an MSI.

.EXAMPLE
    .\deploy\build-package.ps1

.EXAMPLE
    .\deploy\build-package.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot     = Split-Path -Parent $PSScriptRoot
$workerProj   = Join-Path $repoRoot 'src\SslExpireNotify.Worker\SslExpireNotify.Worker.csproj'
$testProj     = Join-Path $repoRoot 'tests\SslExpireNotify.Tests\SslExpireNotify.Tests.csproj'
$installerDir = Join-Path $repoRoot 'installer\SslExpireNotify.Installer'
$databaseDir  = Join-Path $repoRoot 'Database'
$distDir      = Join-Path $repoRoot 'dist'
$runtime      = 'win-x64'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Invoke-Checked([string]$description, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$description failed with exit code $LASTEXITCODE."
    }
}

# ---------------------------------------------------------------- version ----
if (-not $Version) {
    [xml]$csproj = Get-Content -Path $workerProj
    $Version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $Version) {
        throw "No <Version> element found in $workerProj. Add one or pass -Version."
    }
}

Write-Host "Building SslExpireNotify v$Version ($Configuration, $runtime)" -ForegroundColor Green

# -------------------------------------------------- WiX v7 licence check ----
# WiX v7 refuses to build until the Open Source Maintenance Fee EULA has been accepted once per
# machine. This is a licence decision, so it is never done automatically by this script.
$wixEula = Join-Path $env:USERPROFILE '.wix\wix7-osmf-eula.txt'
if (-not (Test-Path $wixEula)) {
    Write-Warning @"
WiX Toolset v7 has not been licensed on this machine yet, so the MSI build will fail.

  One-time step, run by someone who can accept the agreement:
      dotnet build "$installerDir" -t:AcceptEula -p:EulaId=<your-OSMF-EULA-id>

  Details and how to obtain an EULA id: https://wixtoolset.org/osmf/
  (Alternatively pin the installer to WiX 6.0.2, which has no such requirement.)
"@
}

# ---------------------------------------------------------------- tests ------
if (-not $SkipTests) {
    Write-Step 'Running unit tests'
    Invoke-Checked 'dotnet test' { dotnet test $testProj -c $Configuration --nologo }
}

# --------------------------------------------------------------- publish -----
Write-Step "Publishing the worker (self-contained $runtime)"
$publishDir = Join-Path $repoRoot "src\SslExpireNotify.Worker\bin\$Configuration\net10.0\$runtime\publish"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

Invoke-Checked 'dotnet publish' {
    dotnet publish $workerProj -c $Configuration -r $runtime --self-contained true -p:Version=$Version --nologo
}

if (-not (Test-Path (Join-Path $publishDir 'SslExpireNotify.Worker.exe'))) {
    throw "Publish did not produce SslExpireNotify.Worker.exe in $publishDir."
}

$templateCount = (Get-ChildItem -Path (Join-Path $publishDir 'Templates') -Filter '*.html' -ErrorAction SilentlyContinue).Count
if ($templateCount -lt 6) {
    throw "Expected 6 HTML templates in the publish output, found $templateCount."
}

# ------------------------------------------------------------------- MSI -----
Write-Step 'Building the MSI'
Invoke-Checked 'dotnet build (installer)' {
    dotnet build $installerDir -c $Configuration -p:ProductVersion=$Version -p:PublishDir=$publishDir --nologo
}

$msiSource = Join-Path $installerDir "bin\$Configuration\SslExpireNotify-v$Version.msi"
if (-not (Test-Path $msiSource)) {
    throw "The installer build did not produce $msiSource."
}

# ------------------------------------------------------------------ dist -----
Write-Step 'Assembling dist'
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

$msiTarget = Join-Path $distDir "SslExpireNotify-v$Version.msi"
Copy-Item -Path $msiSource -Destination $msiTarget -Force

# The database scripts stay out of the MSI: they run on the SQL Server, not on the service host.
$staging = Join-Path $distDir "_staging-v$Version"
if (Test-Path $staging) {
    Remove-Item -Path $staging -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $staging 'database') | Out-Null

Copy-Item -Path $msiTarget -Destination $staging -Force
Copy-Item -Path (Join-Path $databaseDir 'add-ssl-certificate-primary-key.sql') -Destination (Join-Path $staging 'database') -Force
Copy-Item -Path (Join-Path $databaseDir 'schema.sql') -Destination (Join-Path $staging 'database') -Force
Copy-Item -Path (Join-Path $databaseDir 'seed.sql')   -Destination (Join-Path $staging 'database') -Force
Copy-Item -Path (Join-Path $repoRoot 'deploy\README-DEPLOY.md') -Destination $staging -Force

$zipTarget = Join-Path $distDir "SslExpireNotify-v$Version-deploy.zip"
if (Test-Path $zipTarget) {
    Remove-Item -Path $zipTarget -Force
}
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipTarget
Remove-Item -Path $staging -Recurse -Force

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  MSI : $msiTarget"
Write-Host "  ZIP : $zipTarget"
Write-Host ''
Write-Host 'Reminder: the MSI installs the service but does not start it. Edit appsettings.json first.' -ForegroundColor Yellow
