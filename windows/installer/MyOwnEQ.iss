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

[UninstallRun]
; Unhook Equalizer APO and delete %ProgramData%\MyOwnEQ (engine), so the EQ stops.
; AppMutex already made the user close the app. Profiles in %APPDATA% are kept.
Filename: "{app}\MyOwnEQ.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "MyOwnEQCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  { Pinned version + SHA256: a changed or broken download is never run. To bump,
    update both (shasum -a 256 / Get-FileHash) and check the md5 on SourceForge. }
  ApoUrl = 'https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download';
  ApoSha256 = '7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa';
  ApoPage = 'https://sourceforge.net/projects/equalizerapo/';

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
var
  ErrCode: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('apo') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(ApoUrl, 'EqualizerAPO-setup.exe', ApoSha256);
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        { Failed or hash mismatch: never run it, send the user to the official page. }
        DeleteFile(ExpandConstant('{tmp}\EqualizerAPO-setup.exe'));
        SuppressibleMsgBox('Could not download a verified copy of Equalizer APO (' + GetExceptionMessage + ').' + #13#10 +
          'Its official page will open so you can install it yourself.', mbError, MB_OK, IDOK);
        ShellExec('open', ApoPage, '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
