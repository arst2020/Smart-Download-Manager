; Inno Setup script for Smart Download Manager.
; Build:  iscc /DAppVersion=0.4.0 /DSourceDir=..\publish installer\SmartDownloadManager.iss
; Installs per user by default (no admin needed), like the browser registration, which lives under HKCU.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#define AppName "Smart Download Manager"
#define AppExe "SmartDownloadManager.exe"

[Setup]
AppId={{6C1F6A2E-3B7B-4C5E-9E58-2F1D4C8B7A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Smart Download Manager contributors
AppPublisherURL=https://github.com/arst2020/Smart-Download-Manager
AppSupportURL=https://github.com/arst2020/Smart-Download-Manager/issues
AppUpdatesURL=https://github.com/arst2020/Smart-Download-Manager/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=SmartDownloadManager-{#AppVersion}-Setup
SetupIconFile=..\src\SDM.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy before files are replaced (and start it again afterwards).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.StartWithWindows=Start with Windows (in the notification area)
de.StartWithWindows=Mit Windows starten (im Infobereich)
en.LaunchApp=Start Smart Download Manager
de.LaunchApp=Smart Download Manager starten

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable.txt,*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SmartDownloadManager"; \
  ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; After a silent update started from the app itself, open the app again.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Remove the browser extension's native-host registration (HKCU), then the files go.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-browser"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowser"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\SDM\NativeHost"
; Settings and the download list in %AppData%\SDM are kept on purpose (reinstall keeps them).
