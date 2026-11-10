; Inno Setup script for Dataverse to SQL Sync.
;
; Build it with:
;   iscc /DAppVersion=1.2.3 /DSourceExe="..\publish\DataverseToSql.exe" installer\DataverseToSql.iss
;
; It produces installer\Output\DataverseToSql-<version>-Setup.exe. The app is a self-contained
; single file, so the installer deploys one executable plus the ".installed" marker that tells the
; in-app updater to update by running the new setup program instead of swapping the file.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceExe
  #define SourceExe "..\publish\DataverseToSql.exe"
#endif

#define AppName "Dataverse to SQL Sync"
#define AppShortName "DataverseToSql"
#define AppPublisher "DataverseToSql"
#define AppUrl "https://github.com/larsul/dynamics365-data-exporter"
#define AppExe "DataverseToSql.exe"

[Setup]
; A stable AppId keeps upgrades in place instead of installing a second copy. Do not change it.
AppId={{8E5D4C2B-6A31-4F7E-9C14-2D0B7A3E95F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename={#AppShortName}-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-machine by default, but a user without admin rights may install under their profile instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; Lets the in-app updater close and restart the app with /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS.
CloseApplications=yes
RestartApplications=no
MinVersion=10.0
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion
Source: "..\scripts\*.sql"; DestDir: "{app}\scripts"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist
Source: "..\settings.sample.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Marker: tells the in-app updater that this copy was installed, not unpacked from the portable zip.
Source: "installed-marker.txt"; DestDir: "{app}"; DestName: ".installed"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Left behind by a failed portable update; harmless, but do not leave it in Program Files.
Type: files; Name: "{app}\{#AppExe}.old"
Type: dirifempty; Name: "{app}"
