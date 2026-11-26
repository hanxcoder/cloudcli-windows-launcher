Unicode true
ManifestDPIAware true
ManifestDPIAwareness PerMonitorV2
!include "MUI2.nsh"
!include "FileFunc.nsh"

Name "CloudCLI"
OutFile "CloudCLI-Setup-2.0.0.exe"
InstallDir "$LOCALAPPDATA\Programs\CloudCLI"
InstallDirRegKey HKCU "Software\CloudCLI" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
Icon "icon.ico"
UninstallIcon "icon.ico"

VIProductVersion "2.0.0.0"
VIAddVersionKey "ProductName" "CloudCLI"
VIAddVersionKey "FileDescription" "CloudCLI desktop window (WebView2)"
VIAddVersionKey "FileVersion" "2.0.0.0"
VIAddVersionKey "ProductVersion" "2.0.0.0"
VIAddVersionKey "LegalCopyright" "local"

!define MUI_ABORTWARNING
!define MUI_ICON "icon.ico"
!define MUI_UNICON "icon.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\CloudCLI.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"

LangString UNINST_LNK ${LANG_ENGLISH} "Uninstall CloudCLI"
LangString UNINST_LNK ${LANG_SIMPCHINESE} "卸载 CloudCLI"
LangString UNINST_LNK ${LANG_TRADCHINESE} "解除安裝 CloudCLI"

Function .onInit
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  IfErrors closeLauncher
  StrCmp $R1 "" 0 closeLauncher
  SetSilent silent
  ReadRegStr $R0 HKCU "Software\CloudCLI" "InstallDir"
  StrCmp $R0 "" missingInstall
  IfFileExists "$R0\CloudCLI.exe" 0 missingInstall
  StrCpy $INSTDIR $R0

  closeLauncher:
    nsExec::Exec 'taskkill /IM CloudCLI.exe /F'
    Pop $R0
    Return

  missingInstall:
    SetErrorLevel 2
    Abort
FunctionEnd

Section "CloudCLI" SecMain
  SetOutPath "$INSTDIR"
  File "launcher-service.cjs"
  File "CloudCLI.exe"
  File "icon.ico"
  File "Microsoft.Web.WebView2.Core.dll"
  File "Microsoft.Web.WebView2.WinForms.dll"
  File "WebView2Loader.dll"
  File "LICENSE"
  File "WebView2-LICENSE.txt"
  File "WebView2-NOTICE.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$DESKTOP\CloudCLI.lnk" "$INSTDIR\CloudCLI.exe" "" "$INSTDIR\icon.ico" 0
  CreateDirectory "$SMPROGRAMS\CloudCLI"
  CreateShortcut "$SMPROGRAMS\CloudCLI\CloudCLI.lnk" "$INSTDIR\CloudCLI.exe" "" "$INSTDIR\icon.ico" 0
  Delete "$SMPROGRAMS\CloudCLI\$${UNINST_LNK}.lnk"
  CreateShortcut "$SMPROGRAMS\CloudCLI\$(UNINST_LNK).lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\CloudCLI" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI" "DisplayName" "CloudCLI"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI" "DisplayIcon" "$INSTDIR\icon.ico"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI" "DisplayVersion" "2.0.0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI" "Publisher" "local"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI" "UninstallString" "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\CloudCLI.lnk"
  Delete "$SMPROGRAMS\CloudCLI\CloudCLI.lnk"
  Delete "$SMPROGRAMS\CloudCLI\Uninstall CloudCLI.lnk"
  Delete "$SMPROGRAMS\CloudCLI\卸载 CloudCLI.lnk"
  Delete "$SMPROGRAMS\CloudCLI\解除安裝 CloudCLI.lnk"
  RMDir "$SMPROGRAMS\CloudCLI"
  RMDir /r "$INSTDIR\logs"
  RMDir /r "$INSTDIR\data"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\WebView2-LICENSE.txt"
  Delete "$INSTDIR\WebView2-NOTICE.txt"
  Delete "$INSTDIR\launcher-service.cjs"
  Delete "$INSTDIR\CloudCLI.exe"
  Delete "$INSTDIR\icon.ico"
  Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
  Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
  Delete "$INSTDIR\WebView2Loader.dll"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\CloudCLI"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CloudCLI"
SectionEnd