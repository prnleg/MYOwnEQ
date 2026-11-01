; MyOwnEQ installer (Inno Setup 6). Per-user: no admin needed, so updates are silent.
;   iscc /DMyAppVersion=0.2.0 windows\installer\MyOwnEQ.iss
; Expects the published app in windows\publish (see windows\build.ps1).

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{6D796F77-6E65-7100-8F2C-7B1E4A5D9C30}
AppName=MyOwnEQ
AppVersion={#MyAppVersion}
AppVerName=MyOwnEQ {#MyAppVersion}
AppPublisher=prnleg
AppPublisherURL=https://github.com/prnleg/MYOwnEQ
AppSupportURL=https://github.com/prnleg/MYOwnEQ/issues
DefaultDirName={localappdata}\Programs\MyOwnEQ
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\dist
OutputBaseFilename=MyOwnEQ-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
AppMutex=MyOwnEQ.SingleInstance
UninstallDisplayIcon={app}\MyOwnEQ.exe
SetupIconFile=..\MyOwnEQ\Assets\MyOwnEQ.ico
UninstallDisplayName=MyOwnEQ

[Tasks]
Name: "startup"; Description: "Start MyOwnEQ with Windows"
Name: "apo"; Description: "Download and install Equalizer APO (the Windows audio engine MyOwnEQ uses)"; Check: not ApoInstalled

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\MyOwnEQ"; Filename: "{app}\MyOwnEQ.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyOwnEQ"; ValueData: """{app}\MyOwnEQ.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Equalizer APO's own installer (asks for admin, then lets you pick devices).
Filename: "{tmp}\EqualizerAPO-setup.exe"; StatusMsg: "Installing Equalizer APO…"; Flags: shellexec waituntilterminated skipifdoesntexist; Tasks: apo
; Interactive install: offer to launch.
Filename: "{app}\MyOwnEQ.exe"; Description: "Launch MyOwnEQ"; Flags: nowait postinstall skipifsilent
; Silent self-update (/RELAUNCH=1): bring the app back.
Filename: "{app}\MyOwnEQ.exe"; Parameters: "--background"; Flags: nowait; Check: IsRelaunch

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  ApoUrl = 'https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download';

var
  DownloadPage: TDownloadWizardPage;

function ApoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\EqualizerAPO');
end;

function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') <> '0';
end;

{ The app starts the updater and then quits; give it time to release its mutex. }
function InitializeSetup: Boolean;
var
  i: Integer;
begin
  i := 0;
  while CheckForMutexes('MyOwnEQ.SingleInstance') and (i < 50) do
  begin
    Sleep(200);
    i := i + 1;
  end;
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('apo') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(ApoUrl, 'EqualizerAPO-setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        SuppressibleMsgBox('Could not download Equalizer APO (' + GetExceptionMessage + '). MyOwnEQ will show you where to get it.', mbError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
