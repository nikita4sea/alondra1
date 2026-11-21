; Alondra setup. Compile with Inno Setup 6 (ISCC.exe).

#define MyAppName "Alondra"
#define MyAppVersion "1.3.0"
#define MyAppExeName "Alondra.exe"
; Keep this id: it is what lets a newer setup upgrade an existing Alondra installation in place.
#define MyAppId "{{8F4C2A91-6B1E-4D77-9C3A-1E5B7A0D4F22}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppName}
VersionInfoVersion=1.3.0.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion=1.3.0.0
VersionInfoDescription=Alondra setup
DefaultDirName={localappdata}\Programs\Alondra
DefaultGroupName=Alondra
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\artifacts
OutputBaseFilename=Alondra-setup
SetupLogging=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Alondra artwork: side panel on Welcome/Finish, bird in the page header. One file per DPI scale (100-250%); regenerate with branding\make-branding.ps1.
WizardImageFile=branding\wizard-164.bmp,branding\wizard-205.bmp,branding\wizard-246.bmp,branding\wizard-287.bmp,branding\wizard-328.bmp,branding\wizard-369.bmp,branding\wizard-410.bmp
WizardSmallImageFile=branding\small-55.bmp,branding\small-69.bmp,branding\small-82.bmp,branding\small-96.bmp,branding\small-110.bmp,branding\small-124.bmp,branding\small-138.bmp
WizardImageAlphaFormat=none
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Alondra
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\Alondra.App\Assets\alondra.ico
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\Alondra"; Filename: "{app}\{#MyAppExeName}"; Comment: "Alondra"
Name: "{autodesktop}\Alondra"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Alondra"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, Personal mail, tasks, and credentials live in {localappdata}\alondra and are kept on uninstall.

[Messages]
WelcomeLabel1=Welcome to Alondra
WelcomeLabel2=Alondra brings your mail, calendar, tasks, notes, and contacts together in one calm, local-first app.%n%nThis will install [name/ver] on your computer. Your settings and data stay in their own folder and are kept across updates.
FinishedHeadingLabel=Alondra is ready
FinishedLabelNoIcons=Setup has installed [name] on your computer.
FinishedLabel=Setup has installed [name] on your computer. Open it from the Start menu or the shortcut.

[Code]
// Alondra palette (TColor is $00BBGGRR): text #2A2940, muted #5F5E7A, accent #55569E,
// chrome #F6F4FA, and #FBF9FE to match the top of the side image.
const
  AlondraText   = $0040292A;
  AlondraMuted  = $007A5E5F;
  AlondraAccent = $009E5655;
  AlondraChrome = $00FAF4F6;
  AlondraPage   = $00FEF9FB;

procedure InitializeWizard;
begin
  WizardForm.Color := AlondraChrome;
  WizardForm.WelcomePage.Color := AlondraPage;
  WizardForm.FinishedPage.Color := AlondraPage;
  WizardForm.MainPanel.Color := clWhite;

  WizardForm.WelcomeLabel1.Font.Color := AlondraText;
  WizardForm.WelcomeLabel2.Font.Color := AlondraMuted;
  WizardForm.FinishedHeadingLabel.Font.Color := AlondraText;
  WizardForm.FinishedLabel.Font.Color := AlondraMuted;

  WizardForm.PageNameLabel.Font.Color := AlondraText;
  WizardForm.PageDescriptionLabel.Font.Color := AlondraMuted;
  WizardForm.SelectDirLabel.Font.Color := AlondraText;
  WizardForm.ReadyLabel.Font.Color := AlondraText;
  WizardForm.StatusLabel.Font.Color := AlondraAccent;
  WizardForm.FilenameLabel.Font.Color := AlondraMuted;
end;
