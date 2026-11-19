[CmdletBinding()]
param(
    [Parameter(Position=0)]
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Clean,

    [switch]$Publish,

    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "       xBackup Build Script            " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configuration : $Configuration" -ForegroundColor Yellow
Write-Host "Clean First   : $($Clean.IsPresent)" -ForegroundColor Yellow
Write-Host "Publish       : $($Publish.IsPresent)" -ForegroundColor Yellow
if ($Publish) {
    Write-Host "Single File   : $($SingleFile.IsPresent)" -ForegroundColor Yellow
}
Write-Host ""

Set-Location $ScriptDir

if ($Clean) {
    Write-Host "[Step] Cleaning build artifacts..." -ForegroundColor Yellow
    if (Test-Path "$ScriptDir\bin") { Remove-Item "$ScriptDir\bin" -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path "$ScriptDir\obj") { Remove-Item "$ScriptDir\obj" -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path "$ScriptDir\publish") { Remove-Item "$ScriptDir\publish" -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "       Clean completed." -ForegroundColor Green
}

Write-Host "[Step] Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore "$ScriptDir\xBackup.csproj"
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet restore failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "[Step] Building xBackup ($Configuration)..." -ForegroundColor Yellow
dotnet build "$ScriptDir\xBackup.csproj" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
Write-Host "       Build succeeded!" -ForegroundColor Green

if ($Configuration -eq "Release") {
    $deployDir = (Join-Path $env:LOCALAPPDATA "xPrograms\xBackup")
    $releaseBinDir = Get-ChildItem -Path "$ScriptDir\bin\Release" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if ($releaseBinDir -and (Test-Path (Join-Path $releaseBinDir "xBackup.exe"))) {
        Write-Host "[Step] Deploying Release binaries to $deployDir..." -ForegroundColor Yellow
        Get-Process -Name "xBackup" -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 300
        New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
        Copy-Item -Path "$releaseBinDir\*" -Destination $deployDir -Recurse -Force
        Write-Host "       Deployed to $deployDir" -ForegroundColor Green
    }
}

if ($Publish) {
    $PublishDir = "$ScriptDir\publish\$Configuration"
    Write-Host "[Step] Publishing xBackup to $PublishDir..." -ForegroundColor Yellow

    $PublishArgs = @(
        "publish",
        "$ScriptDir\xBackup.csproj",
        "-c", $Configuration,
        "-o", $PublishDir
    )

    if ($SingleFile) {
        $PublishArgs += "-r"
        $PublishArgs += "win-x64"
        $PublishArgs += "--self-contained"
        $PublishArgs += "true"
        $PublishArgs += "-p:PublishSingleFile=true"
        $PublishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    }

    dotnet @PublishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "       Publish completed! Output directory: $PublishDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "          SUCCESSFUL BUILD!             " -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
