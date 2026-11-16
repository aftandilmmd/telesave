; TeleSave for Windows — per-user installer (no admin rights needed).
; Built by CI: ISCC /DAppVersion=0.3.0 /DArch=x64 /DSourceDir=..\publish\win-x64 TeleSave.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

[Setup]
AppId={{6A0F6A57-6B7B-4C6F-9C2E-5E1E2B7D9A41}
AppName=TeleSave
AppVersion={#AppVersion}
AppPublisher=TeleSave
AppPublisherURL=https://github.com/aftandilmmd/telesave
AppSupportURL=https://github.com/aftandilmmd/telesave/issues
DefaultDirName={localappdata}\Programs\TeleSave
DefaultGroupName=TeleSave
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=TeleSave-{#AppVersion}-windows-{#Arch}-setup
SetupIconFile=..\src\TeleSave.Windows\TeleSave.ico
UninstallDisplayIcon={app}\TeleSave.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\TeleSave"; Filename: "{app}\TeleSave.exe"; AppUserModelID: "TeleSave"

[Run]
Filename: "{app}\TeleSave.exe"; Description: "{cm:LaunchProgram,TeleSave}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the Explorer menu entries, the Send-to shortcut and launch-at-login.
Filename: "{app}\TeleSave.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "TeleSaveCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
