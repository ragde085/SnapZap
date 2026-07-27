; SnapZap installer (Inno Setup 6.1+).
;
; Build it with scripts/build-installer.bat, which publishes first and then runs ISCC over
; this file. Do not run ISCC by hand against a stale artifacts/win-x64.
;
; Why an installer at all, when the app needs no runtime and writes nothing to the registry:
; because the thing being shipped is a *folder*. SnapZap.App.exe does not work on its own — it
; serves its stylesheet and its Blazor scripts out of the wwwroot beside it — and when a user
; copies the exe somewhere on its own, it still starts, still opens, and renders an unstyled
; dead page. There is no error to search for. An installer removes the opportunity: the layout
; is never something the user assembles, and the Start Menu entry points at a path that is
; correct by construction.
;
; Per-user by design (PrivilegesRequired=lowest): SnapZap has no service, no driver and no
; shared component, so there is nothing to justify making the person answer a UAC prompt.

#define AppName        "SnapZap"
#define AppPublisher   "SnapZap"
#define AppExeName     "SnapZap.App.exe"
#define AppUrl         "https://github.com/ragde085/SnapZap"

; Read straight out of the executable that is being packaged, so the installer version can
; never disagree with the binary's. Requires that publish has already run.
#define SourceDir      "..\artifacts\win-x64"
#define AppVersion     GetVersionNumbersString(SourceDir + "\" + AppExeName)

; Pinned exactly as scripts/install-deps.sh pins them — same revision, same checksums. When
; that file's pins move, these move with it.
#define ModelUrl       "https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5/onnx/model.onnx"
#define ModelSha       "a4316a4fb750169ac4fcabaabee1fcbd982b0ee8c0cc63fe3e944954bb9a7d9c"
#define ConfigUrl      "https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5/preprocessor_config.json"
#define ConfigSha      "ae9bb157b9629887cc74913a4e7c12c9308f374f0930e8072320e8f2e1583c5e"

[Setup]
; Never change AppId: it is how Windows recognises an existing SnapZap and upgrades it in
; place instead of leaving two entries in Apps & features.
AppId={{8B7A5F1C-3D42-4E9A-9C61-2F0E7B4D8A15}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes

LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=SnapZap-{#AppVersion}-setup
SetupIconFile=..\src\SnapZap.App\wwwroot\favicon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; The payload is a ~132 MB self-contained binary that is already compressed internally.
; LZMA2/max still earns its keep on it; solid mode does not, and costs a slow install.
Compression=lzma2/max
SolidCompression=no
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Types]
Name: "standard"; Description: "SnapZap"
Name: "full";     Description: "SnapZap and the explicit-content model (328 MB download)"
Name: "custom";   Description: "Custom"; Flags: iscustom

[Components]
Name: "app";   Description: "SnapZap"; Types: standard full custom; Flags: fixed
; The one optional capability. Offered here rather than left to a batch script the user would
; have to find, because an installed copy has no scripts/ directory to run it from.
Name: "model"; Description: "Explicit-content scoring model (328 MB download)"; Types: full

[Files]
; The whole publish tree, wwwroot included. recursesubdirs is what keeps the exe and its
; wwwroot together — the failure this installer exists to prevent.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Components: app; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; models/ is downloaded after install, so it is not in the [Files] manifest and would
; otherwise be left behind along with the directory holding it.
Type: filesandordirs; Name: "{app}\models"

[Code]
var
  DownloadPage: TDownloadWizardPage;

{ ---------------------------------------------------------------------------------------
  WebView2.

  SnapZap draws its window with WebView2. It is present on every up-to-date Windows 10 and 11
  through Edge, so this is a fallback rather than a routine step — but when it is missing the
  app degrades to a browser tab, and fixing that at install time is far kinder than explaining
  it in a dialog later. Detection reads the Evergreen runtime's registry key, per Microsoft's
  documented check, in both the per-machine and per-user locations.
--------------------------------------------------------------------------------------- }
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);

  if Result then
    Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    DownloadPage.SetProgress(Progress, ProgressMax);
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

{ Verifies a download against the checksum pinned at the top of this file, exactly as
  scripts/install-deps.sh does. A 328 MB file fetched over the network is not put in front of
  the inference engine on trust. }
function VerifyAndPlace(const Temp, Dest, ExpectedSha, What: String): Boolean;
begin
  Result := False;

  if not FileExists(Temp) then
  begin
    MsgBox('The download for ' + What + ' did not produce a file.', mbError, MB_OK);
    Exit;
  end;

  if CompareText(GetSHA256OfFile(Temp), ExpectedSha) <> 0 then
  begin
    MsgBox('The checksum for ' + What + ' does not match the expected value, so it has not ' +
           'been installed.' + #13#10#13#10 +
           'SnapZap is fully usable without it — every other feature is unaffected.',
           mbError, MB_OK);
    DeleteFile(Temp);
    Exit;
  end;

  ForceDirectories(ExpandConstant('{app}\models'));
  Result := FileCopy(Temp, Dest, False);
  DeleteFile(Temp);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedsModel, NeedsWebView2: Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  NeedsModel := WizardIsComponentSelected('model');
  NeedsWebView2 := not WebView2Installed;
  if not (NeedsModel or NeedsWebView2) then Exit;

  DownloadPage.Clear;
  if NeedsModel then
  begin
    DownloadPage.Add('{#ModelUrl}', 'nsfw.onnx', '');
    DownloadPage.Add('{#ConfigUrl}', 'preprocessor_config.json', '');
  end;
  if NeedsWebView2 then
    DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      { A failed download must not fail the install. Everything downloaded here is optional:
        without the model SnapZap loses one filter, and without WebView2 it opens in a browser. }
      { Note: a continuation line may not begin with '#', or the preprocessor reads it as a
        directive and the compile aborts. Keep the #13#10 pairs off the start of a line. }
      MsgBox('An optional download did not complete:' + #13#10#13#10 + GetExceptionMessage
             + #13#10#13#10 + 'Setup will continue. SnapZap works without it.',
             mbInformation, MB_OK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  Installer: String;
begin
  if CurStep <> ssPostInstall then Exit;

  if WizardIsComponentSelected('model') then
  begin
    if VerifyAndPlace(ExpandConstant('{tmp}\nsfw.onnx'),
                      ExpandConstant('{app}\models\nsfw.onnx'),
                      '{#ModelSha}', 'the explicit-content model') then
      VerifyAndPlace(ExpandConstant('{tmp}\preprocessor_config.json'),
                     ExpandConstant('{app}\models\preprocessor_config.json'),
                     '{#ConfigSha}', 'the model configuration');
  end;

  Installer := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  if FileExists(Installer) then
    { Silent, per-user: matches this installer's own scope, so it needs no elevation either. }
    Exec(Installer, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

{ ---------------------------------------------------------------------------------------
  Uninstall.

  The catalogue, thumbnails and export manifests live in %LOCALAPPDATA%\SnapZap, deliberately
  outside anything SnapZap scans. Removing it is offered, not assumed: it is the record of
  which photos were already reviewed, and re-scanning a large library to rebuild it is slow.
  Photos themselves are never touched by any of this.
--------------------------------------------------------------------------------------- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  DataDir := ExpandConstant('{localappdata}\SnapZap');
  if not DirExists(DataDir) then Exit;

  { A silent uninstall must never take this branch.

    When message boxes are suppressed, Inno does not return the button marked MB_DEFBUTTON2 —
    it returns a fixed default per box type, and for MB_YESNO that is IDYES. So the "keep my
    catalogue" default silently inverts into "delete it", and `unins000.exe /VERYSILENT
    /SUPPRESSMSGBOXES` wipes the user's scan history without ever asking. Found the hard way,
    on a real catalogue.

    Deleting user data may only ever happen in response to an answer an actual person gave. }
  if UninstallSilent then Exit;

  if MsgBox('Also delete SnapZap''s catalogue and thumbnails?' + #13#10#13#10 +
            DataDir + #13#10#13#10 +
            'This is the record of what has already been scanned and reviewed — not your ' +
            'photos, which are never stored here. Keep it if you plan to reinstall.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
