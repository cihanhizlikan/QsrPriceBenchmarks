; QSR Price Benchmarks — the setup program (Inno Setup 7).
;
; Packages the *self-contained* WPF publish output, so the target PC needs no .NET runtime. The whole publish
; folder goes in, including the bundled Chromium ("ms-playwright") or the system-Edge marker
; ("browser-channel.txt") the publish profile put there.
;
; Built by Ui\Properties\PublishProfiles\Installer.targets, which passes:
;   /DPublishDir=<publish folder, no trailing backslash>   /DAppVersion=<Version from Directory.Build.props>
;   /DArch=x64|arm64|x86                                   /O<output folder>
;
; AppId MUST stay constant across versions: it is how a new setup finds and replaces the installed one.

#ifndef PublishDir
  #error PublishDir is not defined - build through a publish profile (Installer.targets passes it)
#endif
#ifndef AppVersion
  #error AppVersion is not defined - build through a publish profile (Installer.targets passes it)
#endif
#ifndef Arch
  #define Arch "x64"
#endif

#define AppName "QSR Price Benchmarks"
#define AppExe "qsr-ui.exe"
#define AppId "{031AD326-2B35-444A-8825-679E7FC5BDFD}"
; The UpgradeCode every MSI release of 3.x carried. Setup removes such an install before its own (see [Code]).
#define MsiUpgradeCode "{7F3A2C18-9B4E-4D6A-8E21-3C5D9A1B2E40}"

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Yelje
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=License.rtf
PrivilegesRequired=admin
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputBaseFilename=QsrPriceBenchmarks-Setup
SetupIconFile=..\Ui\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
; No skipifsourcedoesntexist: a publish folder that is missing or empty must fail the compile, never produce an
; installer with nothing in it.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Code]
const
  ERROR_SUCCESS = 0;
  ERROR_UNKNOWN_PRODUCT = 1605;
  ERROR_SUCCESS_REBOOT_REQUIRED = 3010;

function MsiEnumRelatedProducts(UpgradeCode: string; Reserved: DWORD; Index: DWORD; ProductCode: string): UINT;
  external 'MsiEnumRelatedProductsW@msi.dll stdcall';

{ The product codes of every installed MSI release (3.x, WiX), found by the UpgradeCode they all shared. }
function InstalledMsiReleases(): TArrayOfString;
var
  Index: Integer;
  Code: string;
begin
  SetArrayLength(Result, 0);
  Index := 0;
  while True do
  begin
    Code := StringOfChar(' ', 39);
    if MsiEnumRelatedProducts('{#MsiUpgradeCode}', 0, Index, Code) <> ERROR_SUCCESS then
      Break;
    SetArrayLength(Result, GetArrayLength(Result) + 1);
    Result[GetArrayLength(Result) - 1] := Copy(Code, 1, 38);
    Index := Index + 1;
  end;
end;

{ The version a previous Inno Setup install of this app recorded, or '' when there is none. }
function InstalledVersion(): string;
begin
  if not RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1',
    'DisplayVersion', Result) then
    Result := '';
end;

{ Refuses a downgrade, as the MSI did: a newer installed version is left alone. }
function InitializeSetup(): Boolean;
var
  Installed: string;
  InstalledPacked, ThisPacked: Int64;
begin
  Result := True;
  Installed := InstalledVersion();
  if (Installed <> '') and StrToVersion(Installed, InstalledPacked) and StrToVersion('{#AppVersion}', ThisPacked)
    and (ComparePackedVersion(InstalledPacked, ThisPacked) > 0) then
  begin
    SuppressibleMsgBox('A newer version of {#AppName} (' + Installed + ') is already installed.',
      mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ Removes an MSI release before the files are copied, so the two installers never both claim the folder.
  The user's database lives in %LOCALAPPDATA%, which the MSI never owned, so it is untouched. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Releases: TArrayOfString;
  I, ExitCode: Integer;
begin
  Result := '';
  Releases := InstalledMsiReleases();
  for I := 0 to GetArrayLength(Releases) - 1 do
  begin
    WizardForm.PreparingLabel.Caption := 'Removing the previous version (' + Releases[I] + ')...';
    if not Exec(ExpandConstant('{sys}\msiexec.exe'), '/x ' + Releases[I] + ' /qn /norestart', '', SW_HIDE,
      ewWaitUntilTerminated, ExitCode) then
    begin
      Result := 'The previous version could not be removed: ' + SysErrorMessage(ExitCode);
      Exit;
    end;
    if ExitCode = ERROR_SUCCESS_REBOOT_REQUIRED then
      NeedsRestart := True
    else if (ExitCode <> ERROR_SUCCESS) and (ExitCode <> ERROR_UNKNOWN_PRODUCT) then
    begin
      Result := 'The previous version could not be removed (Windows Installer error ' + IntToStr(ExitCode) +
        '). Uninstall QSR Price Benchmarks from Settings > Apps, then run this setup again.';
      Exit;
    end;
  end;
end;
