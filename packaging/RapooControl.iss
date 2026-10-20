#ifndef PublishDir
  #error PublishDir must be supplied by build-installer.ps1
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B1FD428A-C2B7-4FB0-B4F8-0EA95C197AA3}
AppName=Rapoo Control
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\RapooControl
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=RapooControl-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Rapoo.App\Assets\Rapoo.ico
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Rapoo.App.exe
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
ReadyLabel2a=Setup will install Rapoo Control for your Windows account.%n%nExisting runtimes will be reused. Missing .NET 10, Visual C++ or Windows App Runtime components will be downloaded from Microsoft. Administrator approval may be required for system runtimes.%n%nClick Install to continue.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "requirements.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Prerequisites.ps1"; Flags: dontcopy
Source: "{#PublishDir}\requirements.json"; Flags: dontcopy
Source: "{#PublishDir}\Microsoft.WindowsAppRuntime.Bootstrap.dll"; Flags: dontcopy

[Icons]
Name: "{userprograms}\Rapoo Control"; Filename: "{app}\Rapoo.App.exe"
Name: "{userdesktop}\Rapoo Control"; Filename: "{app}\Rapoo.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Rapoo.App.exe"; Description: "Launch Rapoo Control"; Flags: nowait postinstall skipifsilent; Check: CanLaunch

[Code]
var
  RuntimeRestartNeeded: Boolean;

function CanLaunch: Boolean;
begin
  Result := not RuntimeRestartNeeded;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Params, PrerequisiteLog: String;
begin
  Result := '';
  ExtractTemporaryFile('Install-Prerequisites.ps1');
  ExtractTemporaryFile('requirements.json');
  ExtractTemporaryFile('Microsoft.WindowsAppRuntime.Bootstrap.dll');
  PrerequisiteLog := ExpandConstant('{localappdata}\RapooControl\installer-prerequisites.log');
  ForceDirectories(ExtractFileDir(PrerequisiteLog));
  WizardForm.PreparingLabel.Caption := 'Checking runtimes and installing missing components. Downloads may take several minutes...';
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Install-Prerequisites.ps1') + '" -LogPath "' + PrerequisiteLog + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Could not start the runtime check: ' + SysErrorMessage(ResultCode)
  else if ResultCode = 3010 then
    RuntimeRestartNeeded := True
  else if ResultCode <> 0 then
    Result := 'Required runtimes could not be installed. Check your internet connection and administrator approval, then retry.' + #13#10 +
      'Details: ' + PrerequisiteLog;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeRestartNeeded;
end;
