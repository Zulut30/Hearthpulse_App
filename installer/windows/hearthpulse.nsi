; Hearthpulse setup wizard for Windows (NSIS 3, Modern UI 2).
; Built by installer/windows/build.sh, which passes:
;   /DVERSION=x.y.z  /DSOURCE_DIR=<published app>  /DOUT_FILE=<setup exe>  /DUNINSTALL_LIST=<generated nsh>
; Per-user install (no administrator prompt) into %LOCALAPPDATA%\Programs\Hearthpulse.

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef VERSION
  !define VERSION "0.11.4"
!endif
!ifndef SOURCE_DIR
  !error "Pass /DSOURCE_DIR=<folder with the published app>"
!endif
!ifndef OUT_FILE
  !define OUT_FILE "Hearthpulse-Setup-${VERSION}-win-x64.exe"
!endif
!ifndef UNINSTALL_LIST
  !error "Pass /DUNINSTALL_LIST=<generated list of installed files>"
!endif

!define APP_NAME "Hearthpulse"
!define APP_EXE "Hearthpulse.exe"
!define UNINSTALLER "Uninstall Hearthpulse.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Hearthpulse"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"
!define ASSETS "${__FILEDIR__}\..\assets"

Name "${APP_NAME}"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Hearthpulse"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Hearthpulse ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "Hearthpulse"
VIAddVersionKey /LANG=0 "FileDescription" "Hearthpulse setup"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "LegalCopyright" "Hearthpulse"

; ---- Look ----------------------------------------------------------------------------------
!define MUI_ICON "${ASSETS}\hearthpulse.ico"
!define MUI_UNICON "${ASSETS}\hearthpulse.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "${ASSETS}\wizard-side.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${ASSETS}\wizard-side.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "${ASSETS}\wizard-header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "${ASSETS}\wizard-header.bmp"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "Software\Hearthpulse"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"

; ---- Pages ---------------------------------------------------------------------------------
!define MUI_WELCOMEPAGE_TITLE "$(WelcomeTitle)"
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_HEADER_TEXT "$(NoticeHeader)"
!define MUI_PAGE_HEADER_SUBTEXT "$(NoticeSubheader)"
!define MUI_LICENSEPAGE_BUTTON "$(^NextBtn)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(NoticeTop)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "$(NoticeBottom)"
!insertmacro MUI_PAGE_LICENSE "${__FILEDIR__}\notice.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(RunNow)"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Italian"
!insertmacro MUI_RESERVEFILE_LANGDLL

; ---- Texts ---------------------------------------------------------------------------------
LangString WelcomeTitle ${LANG_ENGLISH} "Welcome to Hearthpulse ${VERSION}"
LangString WelcomeTitle ${LANG_ITALIAN} "Benvenuto in Hearthpulse ${VERSION}"
LangString WelcomeText ${LANG_ENGLISH} "Hearthpulse is a Hearthstone companion: deck tracker, Battlegrounds advisor, in-game overlay and match history.$\r$\n$\r$\nIt only reads Hearthstone's own log files and a few read-only values. It never changes the game and never shows hidden information.$\r$\n$\r$\nClose Hearthpulse before continuing. Click Next to continue."
LangString WelcomeText ${LANG_ITALIAN} "Hearthpulse è un compagno per Hearthstone: deck tracker, consigli per Battaglia, overlay in gioco e cronologia delle partite.$\r$\n$\r$\nLegge solo i file di log di Hearthstone e alcuni valori in sola lettura. Non modifica mai il gioco e non mostra informazioni nascoste.$\r$\n$\r$\nChiudi Hearthpulse prima di continuare. Fai clic su Avanti per continuare."
LangString NoticeHeader ${LANG_ENGLISH} "Before you install"
LangString NoticeHeader ${LANG_ITALIAN} "Prima di installare"
LangString NoticeSubheader ${LANG_ENGLISH} "How Hearthpulse works, shortcuts and credits."
LangString NoticeSubheader ${LANG_ITALIAN} "Come funziona Hearthpulse, scorciatoie e crediti."
LangString NoticeTop ${LANG_ENGLISH} "Please read the following notes about Hearthpulse."
LangString NoticeTop ${LANG_ITALIAN} "Leggi le note seguenti su Hearthpulse."
LangString NoticeBottom ${LANG_ENGLISH} "Click Next to continue."
LangString NoticeBottom ${LANG_ITALIAN} "Fai clic su Avanti per continuare."
LangString RunNow ${LANG_ENGLISH} "Start Hearthpulse now"
LangString RunNow ${LANG_ITALIAN} "Avvia Hearthpulse ora"
LangString SecAppName ${LANG_ENGLISH} "Hearthpulse (required)"
LangString SecAppName ${LANG_ITALIAN} "Hearthpulse (obbligatorio)"
LangString SecAppDesc ${LANG_ENGLISH} "The app, its overlay and the uninstaller."
LangString SecAppDesc ${LANG_ITALIAN} "L'app, il suo overlay e il programma di disinstallazione."
LangString SecDesktopName ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktopName ${LANG_ITALIAN} "Collegamento sul desktop"
LangString SecDesktopDesc ${LANG_ENGLISH} "Puts a Hearthpulse icon on your desktop."
LangString SecDesktopDesc ${LANG_ITALIAN} "Aggiunge l'icona di Hearthpulse al desktop."
LangString SecMenuName ${LANG_ENGLISH} "Start menu shortcut"
LangString SecMenuName ${LANG_ITALIAN} "Collegamento nel menu Start"
LangString SecMenuDesc ${LANG_ENGLISH} "Adds Hearthpulse to the Start menu."
LangString SecMenuDesc ${LANG_ITALIAN} "Aggiunge Hearthpulse al menu Start."
LangString UnSecAppName ${LANG_ENGLISH} "Hearthpulse"
LangString UnSecAppName ${LANG_ITALIAN} "Hearthpulse"
LangString UnSecAppDesc ${LANG_ENGLISH} "Removes the app, its shortcuts and its autostart entry."
LangString UnSecAppDesc ${LANG_ITALIAN} "Rimuove l'app, i collegamenti e l'avvio automatico."
LangString UnSecDataName ${LANG_ENGLISH} "My match history and settings"
LangString UnSecDataName ${LANG_ITALIAN} "Cronologia partite e impostazioni"
LangString UnSecDataDesc ${LANG_ENGLISH} "Also deletes your recorded games, settings and cached card art. Leave unticked to keep them for a later reinstall."
LangString UnSecDataDesc ${LANG_ITALIAN} "Elimina anche le partite registrate, le impostazioni e le immagini in cache. Lascia deselezionato per conservarle."
LangString ClosingApp ${LANG_ENGLISH} "Closing Hearthpulse…"
LangString ClosingApp ${LANG_ITALIAN} "Chiusura di Hearthpulse…"
LangString Needs64Bit ${LANG_ENGLISH} "Hearthpulse needs 64-bit Windows 10 or 11.$\r$\n$\r$\nThis PC runs 32-bit Windows, so Hearthpulse cannot run on it."
LangString Needs64Bit ${LANG_ITALIAN} "Hearthpulse richiede Windows 10 o 11 a 64 bit.$\r$\n$\r$\nQuesto PC usa Windows a 32 bit, quindi Hearthpulse non può funzionare."
LangString NeedsWin10 ${LANG_ENGLISH} "Hearthpulse needs Windows 10 or 11."
LangString NeedsWin10 ${LANG_ITALIAN} "Hearthpulse richiede Windows 10 o 11."
LangString NeedsWin11Arm ${LANG_ENGLISH} "On PCs with an ARM processor, Hearthpulse needs Windows 11."
LangString NeedsWin11Arm ${LANG_ITALIAN} "Sui PC con processore ARM, Hearthpulse richiede Windows 11."

; ---- Helpers -------------------------------------------------------------------------------
!macro CloseApp
  DetailPrint "$(ClosingApp)"
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${APP_EXE}"'
  Pop $0
  Sleep 800
!macroend

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
  ; The app is 64-bit and needs Windows 10 or later. Say so here instead of installing
  ; something Windows refuses to start ("This app can't run on your PC").
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "$(Needs64Bit)" /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 10240
    MessageBox MB_ICONSTOP|MB_OK "$(NeedsWin10)" /SD IDOK
    Abort
  ${EndIf}
  ; ARM PCs run 64-bit (x64) apps only from Windows 11 on.
  ${If} ${IsNativeARM64}
  ${AndIfNot} ${AtLeastBuild} 22000
    MessageBox MB_ICONSTOP|MB_OK "$(NeedsWin11Arm)" /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

Function un.onInit
  !insertmacro MUI_UNGETLANGUAGE
FunctionEnd

; ---- Install -------------------------------------------------------------------------------
Section "$(SecAppName)" SecApp
  SectionIn RO
  !insertmacro CloseApp
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"

  ; Apps & features entry.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Hearthpulse"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Hearthpulse"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" "$0"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "$(SecDesktopName)" SecDesktop
  CreateShortcut "$DESKTOP\Hearthpulse.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section "$(SecMenuName)" SecMenu
  CreateShortcut "$SMPROGRAMS\Hearthpulse.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "$(SecAppDesc)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "$(SecDesktopDesc)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMenu} "$(SecMenuDesc)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---- Uninstall -----------------------------------------------------------------------------
Section "un.$(UnSecAppName)" UnSecApp
  SectionIn RO
  !insertmacro CloseApp
  Delete "$DESKTOP\Hearthpulse.lnk"
  Delete "$SMPROGRAMS\Hearthpulse.lnk"
  DeleteRegValue HKCU "${RUN_KEY}" "Hearthpulse"

  ; Only the files this setup installed (generated at build time), never the whole folder blindly.
  !include "${UNINSTALL_LIST}"
  Delete "$INSTDIR\${UNINSTALLER}"
  RMDir "$INSTDIR"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\Hearthpulse"
SectionEnd

Section /o "un.$(UnSecDataName)" UnSecData
  RMDir /r "$APPDATA\Hearthpulse"
SectionEnd

!insertmacro MUI_UNFUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecApp} "$(UnSecAppDesc)"
  !insertmacro MUI_DESCRIPTION_TEXT ${UnSecData} "$(UnSecDataDesc)"
!insertmacro MUI_UNFUNCTION_DESCRIPTION_END
