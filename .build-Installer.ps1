<#
.SYNOPSIS
    Builds the standalone Inno Setup 6 installer (xBackup-Setup.exe) for xBackup.

.DESCRIPTION
    Packages %LOCALAPPDATA%\xPrograms\xBackup into a non-MSI
    Windows installer in the project folder (xBackup-Setup.exe), complete with registry
    entries and unins000.exe uninstaller.

.PARAMETER RebuildApp
    Runs .\.build.ps1 first to compile and deploy the latest Release binaries to xPrograms before packaging.

.PARAMETER Install
    Silently runs the generated installer after compilation to apply registry entries and update unins000.exe.

.PARAMETER OutputDir
    Directory where xBackup-Setup.exe will be placed (defaults to the project folder).
#>
[CmdletBinding()]
param(
    [switch]$RebuildApp,
    [switch]$Install,
    [string]$OutputDir = "",
    [string]$IsccPath  = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot      = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = $repoRoot
}
$folderName    = "xBackup"
$appName       = "xBackup"
$installerBase = "xBackup-Setup"
$exeName       = "xBackup.exe"
$version       = "1.0.0"
$appId         = "E5F2C6A8-7D15-4B6E-8098-5D18C1F0E006"
$description   = "Automated backup and snapshot management utility"
$startupArgs   = ""
$defStartup    = $false
$sourceDir     = Join-Path $env:LOCALAPPDATA "xPrograms\$folderName"
$repoIcon      = Join-Path $repoRoot "icon.ico"

if (-not (Test-Path $IsccPath)) {
    throw "Inno Setup 6 compiler (ISCC.exe) not found at: $IsccPath"
}

if ($RebuildApp -or -not (Test-Path (Join-Path $sourceDir $exeName))) {
    $buildScript = Join-Path $repoRoot ".build.ps1"
    if (Test-Path $buildScript) {
        Write-Host "==> Running .build.ps1 for $appName..." -ForegroundColor Cyan
        & $buildScript -Configuration Release
    }
}

if (-not (Test-Path $sourceDir)) {
    throw "Deployed application folder not found: $sourceDir. Run .\.build.ps1 first."
}

$destIcon = Join-Path $sourceDir "icon.ico"
if ((-not (Test-Path $destIcon)) -and (Test-Path $repoIcon)) {
    Copy-Item -Path $repoIcon -Destination $destIcon -Force
}

$setupIconLine = if (Test-Path $destIcon) { "SetupIconFile=`"$destIcon`"" } else { "" }
$startupCmd = if ([string]::IsNullOrWhiteSpace($startupArgs)) {
    "`"`"{app}\$exeName`"`""
} else {
    "`"`"{app}\$exeName`"`" $startupArgs"
}
$startupTaskFlags = if ($defStartup) { "" } else { "; Flags: unchecked" }

$tempBuildDir = Join-Path $env:TEMP "xPrograms-InstallerBuild\$folderName"
$stageOutDir  = Join-Path $tempBuildDir "out"
New-Item -ItemType Directory -Path $stageOutDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$issLines = @(
    "; Generated Inno Setup 6 Script for $appName",
    "#define AppName        `"$appName`"",
    "#define AppFolderName  `"$folderName`"",
    "#define AppVersion     `"$version`"",
    "#define AppPublisher   `"xPrograms`"",
    "#define AppExeName     `"$exeName`"",
    "#define AppId          `"$appId`"",
    "#define AppDescription `"$description`"",
    "#define SourcePath     `"$sourceDir`"",
    "#define OutputPath     `"$stageOutDir`"",
    "",
    "[Setup]",
    "AppId={{{#AppId}}",
    "AppName={#AppName}",
    "AppVersion={#AppVersion}",
    "AppVerName={#AppName} {#AppVersion}",
    "AppPublisher={#AppPublisher}",
    "AppComments={#AppDescription}",
    "DefaultDirName={localappdata}\xPrograms\{#AppFolderName}",
    "DefaultGroupName=xPrograms",
    "DisableProgramGroupPage=yes",
    "PrivilegesRequired=lowest",
    "PrivilegesRequiredOverridesAllowed=dialog commandline",
    "ArchitecturesAllowed=x64compatible",
    "ArchitecturesInstallIn64BitMode=x64compatible",
    "WizardStyle=modern",
    $setupIconLine,
    "UninstallDisplayIcon={app}\{#AppExeName}",
    "UninstallDisplayName={#AppName}",
    "OutputDir={#OutputPath}",
    "OutputBaseFilename=$installerBase",
    "Compression=lzma2/fast",
    "SolidCompression=yes",
    "CloseApplications=force",
    "RestartApplications=no",
    "VersionInfoVersion={#AppVersion}",
    "VersionInfoDescription={#AppDescription}",
    "",
    "[Languages]",
    "Name: `"english`"; MessagesFile: `"compiler:Default.isl`"",
    "",
    "[Tasks]",
    "Name: `"desktopicon`"; Description: `"{cm:CreateDesktopIcon}`"; GroupDescription: `"{cm:AdditionalIcons}`"; Flags: unchecked",
    "Name: `"startupicon`"; Description: `"Start {#AppName} automatically when Windows starts`"$startupTaskFlags",
    "",
    "[Files]",
    "Source: `"{#SourcePath}\*`"; DestDir: `"{app}`"; Excludes: `"unins*.*`"; Flags: ignoreversion recursesubdirs createallsubdirs",
    "",
    "[Icons]",
    "Name: `"{userprograms}\{#AppName}`"; Filename: `"{app}\{#AppExeName}`"; WorkingDir: `"{app}`"; Comment: `"{#AppDescription}`"",
    "Name: `"{userdesktop}\{#AppName}`"; Filename: `"{app}\{#AppExeName}`"; WorkingDir: `"{app}`"; Comment: `"{#AppDescription}`"; Tasks: desktopicon",
    "",
    "[Registry]",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"DisplayName`"; ValueData: `"{#AppName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"InstallDir`"; ValueData: `"{app}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"ExecutablePath`"; ValueData: `"{app}\{#AppExeName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\xPrograms\{#AppFolderName}`"; ValueType: string; ValueName: `"Version`"; ValueData: `"{#AppVersion}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}`"; ValueType: string; ValueName: `"`"; ValueData: `"{app}\{#AppExeName}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExeName}`"; ValueType: string; ValueName: `"Path`"; ValueData: `"{app}`"; Flags: uninsdeletekey",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\Run`"; ValueType: string; ValueName: `"{#AppFolderName}`"; ValueData: `"$startupCmd`"; Tasks: startupicon; Flags: uninsdeletevalue",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\Run`"; ValueType: none; ValueName: `"{#AppFolderName}`"; Flags: uninsdeletevalue",
    "Root: HKCU; Subkey: `"Software\Microsoft\Windows\CurrentVersion\Run`"; ValueType: none; ValueName: `"{#AppName}`"; Flags: uninsdeletevalue",
    "",
    "[Run]",
    "Filename: `"{app}\{#AppExeName}`"; Description: `"{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}`"; Flags: nowait postinstall skipifsilent shellexec",
    "",
    "[UninstallRun]",
    "Filename: `"schtasks.exe`"; Parameters: `"/end /tn `"`"{#AppName}`"`"`"; Flags: runhidden; RunOnceId: `"EndTask_{#AppFolderName}`"",
    "Filename: `"taskkill.exe`"; Parameters: `"/F /IM {#AppExeName}`"; Flags: runhidden; RunOnceId: `"Kill_{#AppFolderName}`"",
    "",
    "[UninstallDelete]",
    "Type: filesandordirs; Name: `"{app}`"",
    "",
    "[Code]",
    "function PrepareToInstall(var NeedsRestart: Boolean): String;",
    "var",
    "  ResultCode: Integer;",
    "begin",
    "  Result := '';",
    "  Exec('schtasks.exe', '/end /tn `"{#AppName}`"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);",
    "  Exec('taskkill.exe', '/F /IM `"{#AppExeName}`"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);",
    "  Sleep(300);",
    "end;"
)

$issFile = Join-Path $tempBuildDir "$folderName.iss"
Set-Content -Path $issFile -Value ($issLines -join "`r`n") -Encoding UTF8

Write-Host "==> Compiling $installerBase.exe..." -ForegroundColor Cyan
$compiled = $false
$stagedExe = Join-Path $stageOutDir "$installerBase.exe"
for ($attempt = 1; $attempt -le 4; $attempt++) {
    if (Test-Path $stagedExe) { Remove-Item $stagedExe -Force -ErrorAction SilentlyContinue }
    & $IsccPath /Q $issFile
    if ($LASTEXITCODE -eq 0 -and (Test-Path $stagedExe)) {
        $compiled = $true
        break
    }
    Write-Host "    [Retry $attempt] Waiting briefly for file handle release..." -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}
if (-not $compiled) {
    throw "ISCC failed to compile $issFile"
}

$finalExe = Join-Path $OutputDir "$installerBase.exe"
Copy-Item -Path $stagedExe -Destination $finalExe -Force
Write-Host "[OK] Installer created at: $finalExe" -ForegroundColor Green

if ($Install) {
    Write-Host "==> Running silent install for $appName..." -ForegroundColor Yellow
    $procName = [System.IO.Path]::GetFileNameWithoutExtension($exeName)
    $wasRunning = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($folderName -eq "DriveSentinel") {
        & schtasks.exe /end /tn "DriveSentinel" 2>&1 | Out-Null
        Start-Sleep -Milliseconds 500
    } elseif ($wasRunning) {
        $wasRunning | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
    $p = Start-Process -FilePath $finalExe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "Silent install failed with exit code $($p.ExitCode)"
    }
    if ($folderName -eq "DriveSentinel" -and $wasRunning) {
        & schtasks.exe /run /tn "DriveSentinel" 2>&1 | Out-Null
    } elseif ($wasRunning -or $defStartup) {
        $installedExe = Join-Path $sourceDir $exeName
        if ($startupArgs) {
            Start-Process -FilePath $installedExe -ArgumentList $startupArgs
        } else {
            Start-Process -FilePath $installedExe
        }
    }
    Write-Host "[OK] Installed & registered $appName in $sourceDir" -ForegroundColor Green
}
