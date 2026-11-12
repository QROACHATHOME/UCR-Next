; Inno Setup 6 script for UCR-Next. Built by build\Build-Release.ps1, which passes:
;   /DAppVersion=0.13.0  /DAppDir=<framework-dependent publish folder>
;   /DRuntime=<windowsdesktop-runtime-8-win-x64.exe>  /DRoot=<repository root>
;
; The app folder stays small (about 25 files): the .NET 8 Desktop Runtime is installed
; system-wide by Microsoft's own installer, and only when it is missing.
; Profiles and settings live in %APPDATA%\UCR-Next and are never touched by Setup or Uninstall.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (use build\Build-Release.ps1)
#endif

#define AppName "UCR-Next"
#define AppExe  "UCR-Next.exe"
#define CliExe  "ucrnext.exe"
#define RuntimeExe "windowsdesktop-runtime-8-win-x64.exe"

[Setup]
; Never change AppId: upgrades and uninstall find the existing install by it.
AppId={{6B2E4C1A-7D3F-4E8B-9A51-2C0F8D6E3B47}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=UCR-Next contributors
AppPublisherURL=https://github.com/QROACHATHOME/UCR-Next
AppSupportURL=https://github.com/QROACHATHOME/UCR-Next/issues
AppUpdatesURL=https://github.com/QROACHATHOME/UCR-Next/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputBaseFilename={#AppName}-{#AppVersion}-setup
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile={#Root}\LICENSE

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: autostart; Description: "&Start UCR-Next in the background when I log on (can be changed later in Settings)"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#Runtime}"; DestDir: "{tmp}"; DestName: "{#RuntimeExe}"; Flags: deleteafterinstall; Check: NeedsRuntime; AfterInstall: InstallRuntime

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#CliExe}"; Parameters: "autostart logon"; Tasks: autostart; Flags: runhidden waituntilterminated runasoriginaluser; StatusMsg: "Setting up start with Windows..."
Filename: "{app}\{#AppExe}"; Description: "Start UCR-Next now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Removes the logon Run entry or the elevated logon task, whichever is set.
Filename: "{app}\{#CliExe}"; Parameters: "autostart off"; Flags: runhidden waituntilterminated; RunOnceId: "AutostartOff"

[Code]
var
  RuntimeMissing: Boolean;

{ True if a .NET 8 Desktop Runtime (Microsoft.WindowsDesktop.App 8.x, x64) is installed. }
function HasDesktopRuntime8: Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), Rec) then
  try
    repeat
      if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := True;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function InitializeSetup: Boolean;
begin
  RuntimeMissing := not HasDesktopRuntime8;
  Result := True;
end;

function NeedsRuntime: Boolean;
begin
  Result := RuntimeMissing;
end;

procedure InstallRuntime;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft .NET 8 Desktop Runtime (this can take a minute)...';
  if not Exec(ExpandConstant('{tmp}\{#RuntimeExe}'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  { 0 = OK, 3010 = OK but a restart is needed, 1641 = restart started }
  if (Code <> 0) and (Code <> 3010) and (Code <> 1641) then
    MsgBox('The .NET 8 Desktop Runtime could not be installed (code ' + IntToStr(Code) + ').' + #13#10 +
      'UCR-Next needs it to start. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 ' +
      '(".NET Desktop Runtime", x64), then start UCR-Next.', mbError, MB_OK);
end;

{ UCR-Next holds this mutex while it runs (see App.xaml.cs). }
function AppRunning: Boolean;
begin
  Result := CheckForMutexes('Local\UcrNext-' + GetUserNameString);
end;

function WaitForExit: Boolean;
var
  I: Integer;
begin
  for I := 1 to 40 do
  begin
    if not AppRunning then Break;
    Sleep(250);
  end;
  Result := not AppRunning;
end;

{ Asks a running UCR-Next to exit (it refuses if it has unsaved profile edits). True if it is gone.
  Its control pipe only accepts the same user at the same elevation, so Setup (elevated) first asks
  as the logged-on user, then elevated (UCR-Next started by the "as administrator" logon task).
  The uninstaller can only ask elevated. }
function QuitApp(AsOriginalUser: Boolean): Boolean;
var
  Code: Integer;
  Cli: String;
begin
  Result := not AppRunning;
  if Result then Exit;
  Cli := ExpandConstant('{app}\{#CliExe}');
  if not FileExists(Cli) then Exit;
  if AsOriginalUser then
  begin
    ExecAsOriginalUser(Cli, 'quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Result := WaitForExit;
    if Result then Exit;
  end;
  Exec(Cli, 'quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := WaitForExit;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { Upgrading: the running copy is in the install folder, so it must exit first.
    A portable copy running elsewhere does not lock these files and can keep running. }
  if FileExists(ExpandConstant('{app}\{#AppExe}')) and not QuitApp(True) then
    Result := 'UCR-Next is still running, probably because it has unsaved profile edits. ' +
      'Save or discard them, exit UCR-Next (tray icon > Exit), then click Back and Next to try again.';
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while Result and not QuitApp(False) do
    if UninstallSilent then Result := False
    else Result := MsgBox('Please exit UCR-Next first (tray icon > Exit, or "ucrnext quit"), then click Retry.' + #13#10 +
      'If you have unsaved profile edits, save or discard them when it asks.', mbError, MB_RETRYCANCEL) = IDRETRY;
end;
