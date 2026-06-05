#ifndef AppName
  #define AppName "CanAnalyzer"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef AppPublisher
  #define AppPublisher "Gyrari B.V."
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef ExeName
  #define ExeName "CanAnalyzer.exe"
#endif

[Setup]
AppId={{8A360A2A-EC03-46F8-B74B-D6B1E4DB226E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=CanAnalyzer-Setup-{#AppVersion}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#ExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
