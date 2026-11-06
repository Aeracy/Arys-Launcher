Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!ifndef AppIcon
!define AppIcon "${__FILEDIR__}\src\ArysLauncher\Assets\app.ico"
!endif
!define MUI_ICON "${AppIcon}"
!define MUI_UNICON "${AppIcon}"

Name "Ary's Launcher"
OutFile "${OutputFile}"
InstallDir "$LOCALAPPDATA\Programs\ArysLauncher"
InstallDirRegKey HKCU "Software\Aeracy\ArysLauncher" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 16
BrandingText "Aeracy"
VIProductVersion "${AppVersion}.0"
VIAddVersionKey "ProductName" "Ary's Launcher"
VIAddVersionKey "CompanyName" "Aeracy"
VIAddVersionKey "FileDescription" "Ary's Launcher Setup"
VIAddVersionKey "FileVersion" "${AppVersion}"
VIAddVersionKey "LegalCopyright" "Copyright © 2026 Aeracy"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\app\ArysLauncher.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"

Section "Launcher"
    SetOutPath "$INSTDIR\app"
    File "${AppDirectory}\ArysLauncher.exe"
    File "${AppDirectory}\release-channel.json"
    File "${AppDirectory}\README.md"
    !ifdef BundledPack
        File /oname=Arys-Modpack.mrpack "${BundledPack}"
    !endif
    SetOutPath "$INSTDIR\app\Licenses"
    File "${AppDirectory}\Licenses\*.txt"
    File "${AppDirectory}\Licenses\ThirdPartyNotices.md"
    SetOutPath "$INSTDIR\app"
    SetRegView 32
    ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    ${If} $0 == ""
        ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
    ${EndIf}
    ${If} $0 == ""
        File /oname=BrowserRuntime.cab "${AppDirectory}\BrowserRuntime.cab"
    ${EndIf}
    FileOpen $1 "$INSTDIR\launcher.marker" w
    FileClose $1
    CreateDirectory "$SMPROGRAMS\Ary's Launcher"
    CreateShortcut "$SMPROGRAMS\Ary's Launcher\Ary's Launcher.lnk" "$INSTDIR\app\ArysLauncher.exe"
    CreateShortcut "$DESKTOP\Ary's Launcher.lnk" "$INSTDIR\app\ArysLauncher.exe"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "Software\Aeracy\ArysLauncher" "InstallDir" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArysLauncher" "DisplayName" "Ary's Launcher"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArysLauncher" "DisplayVersion" "${AppVersion}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArysLauncher" "Publisher" "Aeracy"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArysLauncher" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
SectionEnd

Section "Uninstall"
    Delete "$INSTDIR\app\ArysLauncher.exe"
    Delete "$INSTDIR\app\release-channel.json"
    Delete "$INSTDIR\app\README.md"
    Delete "$INSTDIR\app\BrowserRuntime.cab"
    Delete "$INSTDIR\app\Arys-Modpack.mrpack"
    !include "${UninstallLicenses}"
    RMDir "$INSTDIR\app\Licenses"
    RMDir "$INSTDIR\app"
    Delete "$INSTDIR\launcher.marker"
    Delete "$INSTDIR\Uninstall.exe"
    Delete "$DESKTOP\Ary's Launcher.lnk"
    Delete "$SMPROGRAMS\Ary's Launcher\Ary's Launcher.lnk"
    RMDir "$SMPROGRAMS\Ary's Launcher"
    RMDir "$INSTDIR"
    DeleteRegKey HKCU "Software\Aeracy\ArysLauncher"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ArysLauncher"
SectionEnd
