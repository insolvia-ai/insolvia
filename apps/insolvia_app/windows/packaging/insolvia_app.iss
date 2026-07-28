; Inno Setup script for the unsigned Windows installer (issue #14, MVP_PLAN 4.6).
;
; WHY THIS FILE EXISTS
;   `flutter build windows` emits a *directory*
;   (build\windows\x64\runner\Release\ — an .exe next to flutter_windows.dll,
;   plugin DLLs and a data\ folder). Every one of those files is required at
;   runtime, so "hand someone the .exe" does not work. Inno Setup wraps the
;   directory into the single self-contained setup.exe that D8's
;   distribution model ("distribution is just a download link") assumes.
;   It is also already installed on the `windows-latest` GitHub runner image,
;   so it costs no extra toolchain.
;
; UNSIGNED, DELIBERATELY
;   No SignTool directive here, and none should be added casually. Under D8
;   (docs/MVP_PLAN.md) no code-signing certificate is procured yet, so the
;   installer carries Mark of the Web and SmartScreen will show
;   "Windows protected your PC" — More info -> Run anyway. That is a known,
;   accepted cost, not a bug in this script.
;
; INVOKED BY CI AS
;   iscc /DInsolviaEnv=staging /DAppVersion=0.1.0 ^
;        /DOutputBase=insolvia_app-staging-0.1.0-abc1234-setup ^
;        /DSourceDir=..\..\build\windows\x64\runner\Release ^
;        windows\packaging\insolvia_app.iss
;   Every variable is passed in so the filename convention lives in one place
;   (the workflow) rather than being split across two files.

#ifndef InsolviaEnv
  #define InsolviaEnv "local"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef OutputBase
  #define OutputBase "insolvia_app-" + InsolviaEnv + "-" + AppVersion + "-setup"
#endif
#ifndef SourceDir
  ; Relative to *this file*, which is what Inno resolves against.
  #define SourceDir "..\..\build\windows\x64\runner\Release"
#endif

; The environment is compiled into the binary via --dart-define=INSOLVIA_ENV
; (lib/src/config/environment.dart), so a staging installer and a production
; installer are genuinely different products. They therefore get different
; AppIds and different display names: installing a staging build must never
; silently upgrade-over-the-top of someone's production install, and the
; Add/Remove Programs entry has to say which one they have. Same reasoning as
; the environment tag in the artifact filename.
#if InsolviaEnv == "production"
  #define AppId "{A141D7EE-DE44-4B0C-A18F-326103158224}"
  #define AppDisplayName "Insolvia"
  #define AppDirName "Insolvia"
#elif InsolviaEnv == "staging"
  #define AppId "{1CF23854-79EC-4D7F-AF54-D91007D7C93A}"
  #define AppDisplayName "Insolvia (Staging)"
  #define AppDirName "Insolvia Staging"
#else
  #define AppId "{76C0AFD7-021A-47AF-9A87-A24112FE8B3A}"
  #define AppDisplayName "Insolvia (Local)"
  #define AppDirName "Insolvia Local"
#endif

[Setup]
AppId={{#AppId}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
; Matches the macOS target's identity strings
; (macos/Runner/Configs/AppInfo.xcconfig) so the two desktop builds present
; themselves as the same product.
AppPublisher=ai.insolvia
AppCopyright=Copyright (C) 2026 ai.insolvia. All rights reserved.
VersionInfoVersion={#AppVersion}

; Per-user install into %LOCALAPPDATA%. Deliberate: it needs no administrator,
; so there is no UAC elevation prompt stacked on top of the SmartScreen warning
; an unsigned installer already triggers. Attorneys on IT-managed machines
; frequently are not local admins; a machine-wide install would simply fail for
; them. Revisit if/when D8 is reversed and the installer is signed.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppDirName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
; Nothing to configure — this is a hello-world shell app, so the install
; wizard should get out of the way.
DisableDirPage=auto
DisableReadyPage=yes

OutputDir=..\..\build\windows\installer
OutputBaseFilename={#OutputBase}
SetupIconFile=..\runner\resources\app_icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Flutter Windows is x64-only; refuse to install on anything else rather than
; installing a binary that cannot run. Spelled `x64` rather than the newer
; `x64compatible` on purpose: `x64compatible` is a hard error on Inno Setup
; older than 6.3, whereas `x64` is merely deprecated on 6.3+. Since we take
; whatever version the GitHub runner image happens to ship, the spelling that
; degrades to a warning is the safe one.
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole Flutter release directory — the .exe alone is not runnable. Files
; are enumerated by wildcard rather than listed, so a new plugin DLL is picked
; up without editing this script.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\insolvia_app.exe"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\insolvia_app.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\insolvia_app.exe"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent
