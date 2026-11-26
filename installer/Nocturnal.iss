#define MyAppName "Nocturnal"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Hans x Gravi"
#define MyAppURL "https://github.com/umamhaniff/nocturnal"
#define MyAppExeName "Nocturnal.exe"

[Setup]
AppId={{8C7E2F4A-5B1D-4E93-9A2F-7D6C3B1E4A90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
OutputDir=..\dist
OutputBaseFilename=Nocturnal-Setup-{#MyAppVersion}
SetupIconFile=..\src\Nocturnal.App\Assets\nocturnal.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Nocturnal_SingleInstance_Mutex
CloseApplications=force
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Launch {#MyAppName} at Windows startup"; GroupDescription: "Additional options:"
Name: "addtopath"; Description: "Add nocturnal-cli to user PATH"; GroupDescription: "Additional options:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: ShouldRelaunchAfterSilentUpdate

[Code]
const
  EVENT_MODIFY_STATE = $0002;

function OpenEventW(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup(): Boolean;
var
  hEvent: THandle;
  WaitMs: Integer;
  ResultCode: Integer;
begin
  if CheckForMutexes('Nocturnal_SingleInstance_Mutex') then
  begin
    hEvent := OpenEventW(EVENT_MODIFY_STATE, False, 'Nocturnal_GracefulExit_Event');
    if hEvent <> 0 then
    begin
      SetEvent(hEvent);
      CloseHandle(hEvent);
    end;

    WaitMs := 0;
    while CheckForMutexes('Nocturnal_SingleInstance_Mutex') and (WaitMs < 2500) do
    begin
      Sleep(100);
      WaitMs := WaitMs + 100;
    end;

    if CheckForMutexes('Nocturnal_SingleInstance_Mutex') then
    begin
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Nocturnal.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      WaitMs := 0;
      while CheckForMutexes('Nocturnal_SingleInstance_Mutex') and (WaitMs < 1500) do
      begin
        Sleep(100);
        WaitMs := WaitMs + 100;
      end;
    end;
  end;
  Result := True;
end;

function ShouldRelaunchAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

procedure AddToUserPath(Dir: string);
var
  CurrentPath: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath) then
  begin
    CurrentPath := '';
  end;

  if Pos(';' + Lowercase(Dir) + ';', ';' + Lowercase(CurrentPath) + ';') > 0 then
  begin
    Exit;
  end;

  if CurrentPath <> '' then
  begin
    if Copy(CurrentPath, Length(CurrentPath), 1) <> ';' then
    begin
      CurrentPath := CurrentPath + ';';
    end;
  end;

  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', CurrentPath + Dir);
end;

procedure RemoveFromUserPath(Dir: string);
var
  CurrentPath: string;
  UpdatedPath: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath) then
  begin
    Exit;
  end;

  UpdatedPath := CurrentPath;
  P := Pos(';' + Lowercase(Dir), Lowercase(UpdatedPath));
  if P > 0 then
  begin
    Delete(UpdatedPath, P, Length(Dir) + 1);
  end
  else if Pos(Lowercase(Dir) + ';', Lowercase(UpdatedPath)) = 1 then
  begin
    Delete(UpdatedPath, 1, Length(Dir) + 1);
  end
  else if Lowercase(UpdatedPath) = Lowercase(Dir) then
  begin
    UpdatedPath := '';
  end;

  if UpdatedPath <> CurrentPath then
  begin
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', UpdatedPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
  begin
    AddToUserPath(ExpandConstant('{app}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
    RemoveFromUserPath(ExpandConstant('{app}'));
  end;
end;
