#define MyAppName "railreader2"
; CI passes the version as /DAPP_VERSION=<tag> (a preprocessor define); an APP_VERSION environment
; variable also works. GetEnv alone ignored the /D define, so installers were stamped with the fallback.
#ifdef APP_VERSION
  #define MyAppVersion APP_VERSION
#else
  #define MyAppVersion GetEnv('APP_VERSION')
#endif
#if MyAppVersion == ""
  #define MyAppVersion "2.0.0"
#endif
#define MyAppPublisher "sjvrensburg"
#define MyAppURL "https://github.com/sjvrensburg/railreader2"
#define MyAppExeName "RailReader2.exe"

[Setup]
AppId={{E8A3F2D1-7B4C-4E5A-9F6D-2C8B1A3E4F5D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Paths are relative to this .iss file's directory (installer/)
OutputDir=output
OutputBaseFilename=railreader2-setup-x64
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pdfassoc"; Description: "Associate with .pdf files"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
; publish/ contains the app + models/ subdirectory with layout models (placed there by CI)
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "RailReader2.PDF"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailReader2.PDF"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailReader2.PDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailReader2.PDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: pdfassoc
; Reading packages are RailReader2's own file type, so they are always associated (no task).
Root: HKA; Subkey: "Software\Classes\.railreading"; ValueType: string; ValueName: ""; ValueData: "RailReader2.Package"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.railreading\OpenWithProgids"; ValueType: string; ValueName: "RailReader2.Package"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\RailReader2.Package"; ValueType: string; ValueName: ""; ValueData: "RailReader2 Reading Package"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\RailReader2.Package\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\RailReader2.Package\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; In-app update (src/RailReader2.Shared/Updates/UpdateInstaller.cs): the app runs this installer silently after it exits,
; with /RELAUNCH=1 when the reader chose "Restart and Install". runasoriginaluser: an all-users install
; is elevated, but the reopened app must not be.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
const
  SYNCHRONIZE = $00100000;

function OpenProcess(dwDesiredAccess: Cardinal; bInheritHandle: LongBool; dwProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';

// /WAITPID=<pid>: the app that launched this update. Wait (up to 30 s) for it to exit so its files are
// free and its last saves are done.
function InitializeSetup(): Boolean;
var
  Pid: Cardinal;
  H: THandle;
begin
  Result := True;
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid <> 0 then
  begin
    H := OpenProcess(SYNCHRONIZE, False, Pid);
    if H <> 0 then
    begin
      WaitForSingleObject(H, 30000);
      CloseHandle(H);
    end;
  end;
end;

function ShouldRelaunch(): Boolean;
begin
  Result := WizardSilent() and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
