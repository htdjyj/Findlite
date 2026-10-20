Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!define VERSION "1.0.1"
!ifdef RELEASE_TEST
  !define PRODUCT "FindLite Release Test"
  !define APPKEY "FindLite.ReleaseSelfTest.20261020"
  OutFile "..\FindLite-1.0.1-TestSetup.exe"
!else
  !define PRODUCT "FindLite"
  !define APPKEY "FindLite.Desktop"
  OutFile "..\FindLite-1.0.1-Setup-x64.exe"
!endif
!define UNKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPKEY}"
Name "${PRODUCT} ${VERSION}"
InstallDir "$LOCALAPPDATA\Programs\FindLite"
InstallDirRegKey HKCU "${UNKEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "FindLite · 本地文件搜索"
VIProductVersion "1.0.1.0"
VIAddVersionKey /LANG=2052 "ProductName" "FindLite"
VIAddVersionKey /LANG=2052 "CompanyName" "FindLite"
VIAddVersionKey /LANG=2052 "FileDescription" "FindLite 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "1.0.1.0"
VIAddVersionKey /LANG=2052 "ProductVersion" "1.0.1"
VIAddVersionKey /LANG=2052 "LegalCopyright" "FindLite"
!define MUI_ICON "FindLite.ico"
!define MUI_UNICON "FindLite.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "欢迎使用 FindLite"
!define MUI_WELCOMEPAGE_TEXT "一个关键词，找到所需。$\r$\n$\r$\n轻量的本地文件搜索，支持模糊匹配、类型筛选和回收站删除。$\r$\n$\r$\n仅为当前用户安装，无需管理员权限。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\FindLite.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 FindLite"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Function CheckApp
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\FindLite.Desktop.v1") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONINFORMATION "请先关闭 FindLite，再继续安装。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd
Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此版本需要 64 位 Windows。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "此发布版本支持 Windows 10 / 11。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  SetRegView 64
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} $0 < 528040
    MessageBox MB_OK|MB_ICONSTOP "需要 Microsoft .NET Framework 4.8 或更高版本。请先通过 Windows 更新或微软官方网站安装。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  Call CheckApp
FunctionEnd
Section "FindLite 主程序（必选）" Main
  SectionIn RO
  SetShellVarContext current
  SetRegView 64
  SetOutPath "$INSTDIR"
  File "..\portable\FindLite.exe"
  File "..\portable\使用说明.txt"
  File "..\portable\隐私说明.txt"
  File "..\portable\THIRD-PARTY.txt"
  File "..\portable\第三方许可-NSIS.txt"
  File "..\portable\LICENSE.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${PRODUCT}"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\FindLite.lnk" "$INSTDIR\FindLite.exe"
  CreateShortcut "$SMPROGRAMS\${PRODUCT}\卸载 FindLite.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${UNKEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNKEY}" "Publisher" "FindLite"
  WriteRegStr HKCU "${UNKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNKEY}" "DisplayIcon" "$INSTDIR\FindLite.exe"
  WriteRegStr HKCU "${UNKEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNKEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNKEY}" "EstimatedSize" 256
SectionEnd
Section /o "创建桌面快捷方式" Desktop
  SetShellVarContext current
  CreateShortcut "$DESKTOP\${PRODUCT}.lnk" "$INSTDIR\FindLite.exe"
SectionEnd
Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadRegStr $0 HKCU "${UNKEY}" "InstallLocation"
  ${If} $0 != $INSTDIR
    MessageBox MB_OK|MB_ICONSTOP "安装路径与注册信息不一致，已停止卸载。" /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\FindLite.Desktop.v1") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONINFORMATION "请先关闭 FindLite，再继续卸载。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd
Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Delete "$INSTDIR\FindLite.exe"
  Delete "$INSTDIR\使用说明.txt"
  Delete "$INSTDIR\隐私说明.txt"
  Delete "$INSTDIR\THIRD-PARTY.txt"
  Delete "$INSTDIR\第三方许可-NSIS.txt"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$SMPROGRAMS\${PRODUCT}\FindLite.lnk"
  Delete "$SMPROGRAMS\${PRODUCT}\卸载 FindLite.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT}"
  Delete "$DESKTOP\${PRODUCT}.lnk"
  DeleteRegKey HKCU "${UNKEY}"
  RMDir "$INSTDIR"
  ; User index data and unknown files are deliberately preserved.
SectionEnd
