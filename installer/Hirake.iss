; Hirake インストーラ（ISSUE #40 設計 6-3）
;
; 管理者権限を要求しない。%LocalAppData%\Programs\Hirake へユーザー単位で入れる。
; .NET ランタイムは自己完結発行で同梱するため、前提の導入は不要。
;
; ローカルでのビルド手順は installer/README.md を参照。
; CI からは次の形で版を注入する:
;   ISCC.exe /DAppVersion=1.0.0 installer\Hirake.iss

; 版は必ず外から渡す。既定値を持たせると、配布できてしまう 0.0.0 の
; インストーラをうっかり作れてしまい「版の出所は csproj だけ」が崩れる。
#ifndef AppVersion
  #error AppVersion が指定されていません。/DAppVersion=1.2.3 を付けてください（installer/README.md 参照）。
#endif

; publish 一式の場所。既定はリポジトリ直下の publish\。
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "Hirake"

[Setup]
; AppId は一度決めたら恒久的に固定する。変えると別アプリ扱いになり、
; 旧版が残ったまま二重にインストールされる。
AppId={{8E9D2C41-7B36-4F58-9A0E-1D5C6F2B4A73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Hirake contributors
AppPublisherURL=https://github.com/soramaru777/hirake-markdown
AppSupportURL=https://github.com/soramaru777/hirake-markdown/issues
AppUpdatesURL=https://github.com/soramaru777/hirake-markdown/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; ライセンス同意（MIT）とコンポーネント選択は出さない。構成は 1 つだけ。
DisableDirPage=no
DisableReadyPage=no

; 管理者権限を要求しない。要求すると .NET ランタイム同梱の意味が薄れる。
; PrivilegesRequiredOverridesAllowed は設定しない（インストールの種類を選ぶ
; 画面が増えるうえ、全ユーザー向けを選ばれると {localappdata} の意味が変わる）。
PrivilegesRequired=lowest

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; csproj の TargetPlatformMinVersion（10.0.17763.0）と揃える。
MinVersion=10.0.17763

; 実行中なら終了を促す（SingleInstanceManager.MutexName と一致させること）。
AppMutex=Hirake_SingleInstance_Mutex

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Hirake.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=..\dist
OutputBaseFilename=HirakeSetup-{#AppVersion}
SetupIconFile=..\src\Hirake\app.ico

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "associate"; Description: ".md ファイルを {#AppName} で開けるようにする"; GroupDescription: "追加の設定:"
Name: "startmenuicon"; Description: "スタートメニューにショートカットを作成する"; GroupDescription: "追加の設定:"
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加の設定:"; Flags: unchecked

[InstallDelete]
; 上書きインストールでは、前版にだけ存在したファイルが残る。自己完結発行は
; SDK の更新で構成（DLL 名・サテライトフォルダ）が変わるため、放置すると
; 古い DLL が残り続け、修正済みの不具合を含むバイナリが読まれ得る。
;
; 消す対象は「発行が出力するもの」だけを名指しする。?? のようなパターンは
; 使わない。インストール先は変更できるため、利用者の db\ や ui\ といった
; フォルダを巻き込んで再帰削除してしまう。
; unins000.exe / unins000.dat は消さない（消すとアンインストールできなくなる）。
;
; さらに Check で「前版がこの場所に入っている」ことを条件にする。名指しにしても、
; 利用者が既存のフォルダをインストール先に選べば、そこに元からあった scripts\ や
; *.json を消してしまう。消してよいのは自分が置いたものだけ。
;
; 発行の構成を変えたとき（新しい拡張子・新しいフォルダが増えたとき）は、
; ここにも足すこと。installer/README.md の上書きインストール確認で気付ける。
Type: filesandordirs; Name: "{app}\Assets"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\scripts"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\runtimes"; Check: IsUpgradeOfThisLocation
; サテライトアセンブリのフォルダ（.NET / WPF が出力する 13 カルチャ）。
Type: filesandordirs; Name: "{app}\cs"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\de"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\es"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\fr"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\it"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\ja"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\ko"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\pl"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\pt-BR"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\ru"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\tr"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\zh-Hans"; Check: IsUpgradeOfThisLocation
Type: filesandordirs; Name: "{app}\zh-Hant"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\*.dll"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\*.json"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\*.pdb"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\*.lib"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\*.xml"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\Hirake.exe"; Check: IsUpgradeOfThisLocation
Type: files; Name: "{app}\createdump.exe"; Check: IsUpgradeOfThisLocation

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; 関連付けの登録／解除はインストーラで再実装せず、テスト済みのスクリプトを呼ぶ。
Source: "..\scripts\association-lib.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\register.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\unregister.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Hirake.exe"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Hirake.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Hirake.exe"; Description: "{#AppName} を起動する"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; [Code] で作った目印はアンインストールログに載らないため、明示的に消す。
Type: files; Name: "{app}\.installed-by-hirake-setup"

[Code]
// ---- 関連付けの所有権 ------------------------------------------------
//
// unregister.ps1 の契約は「今ある Hirake の関連付けをすべて解除する」であって、
// 「このインストーラが作ったものだけを消す」ではない。素直に呼ぶと、
// 利用者が自分でビルドした Hirake に対して手で登録した関連付けまで消える。
//
// そこで「インストーラが登録したこと」と「その時の exe」を記録し、
// 記録があり、かつ今の登録先がこのインストール先を指しているときだけ解除する。
const
  OwnerKey = 'Software\Hirake\Installer';
  OwnerValue = 'AssociatedExePath';
  CommandKey = 'Software\Classes\Hirake.md\shell\open\command';
  SchemeCommandKey = 'Software\Classes\hirake\shell\open\command';
  AppIdGuid = '{8E9D2C41-7B36-4F58-9A0E-1D5C6F2B4A73}';
  // Inno が作るアンインストール情報のキー（AppId + "_is1"）。
  UninstallKey =
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E9D2C41-7B36-4F58-9A0E-1D5C6F2B4A73}_is1';
  // このインストーラがそのフォルダを管理していることを示す目印。
  OwnerMarkerName = '.installed-by-hirake-setup';

// 前版の Hirake が、まさにこの場所に入っているか。
//
// [InstallDelete] はインストール先に元からあったファイルも消すため、
// 「自分が前に置いたものを片付ける」ときだけに限定する。利用者が既存の
// フォルダをインストール先に選んだ場合、そこの scripts\ や *.json は他人のもの。
//
// 判定に Hirake.exe の有無は使わない。ウイルス対策ソフトに exe だけ隔離された、
// 更新が途中で失敗して exe だけ欠けた、といった状態で「新規扱い」になり、
// 前版の DLL が残り続けてしまう。目印そのものを見る。
function IsOurInstallAt(const Dir: String): Boolean;
var
  Location, Target, MarkerText: String;
  Marker: AnsiString;
begin
  Result := False;

  // 前版のアンインストール情報が無い＝新規インストール。
  if not RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey, 'InstallLocation', Location) then
    Exit;

  Location := RemoveBackslashUnlessRoot(RemoveQuotes(Trim(Location)));
  Target := RemoveBackslashUnlessRoot(Dir);

  if CompareText(Location, Target) <> 0 then
    Exit;

  // 記録だけでなく、そのフォルダに実体があることも確かめる。
  // 目印が第一だが、書き込みに失敗していた場合の逃げ道として exe も見る
  // （どちらも無ければ「前に入れた場所ではない」と判断する）。
  if LoadStringFromFile(AddBackslash(Target) + OwnerMarkerName, Marker) then
  begin
    MarkerText := Marker;
    if CompareText(Trim(MarkerText), AppIdGuid) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;

  Result := FileExists(AddBackslash(Target) + 'Hirake.exe');
end;

function IsUpgradeOfThisLocation(): Boolean;
begin
  Result := IsOurInstallAt(ExpandConstant('{app}'));
end;

// 「このフォルダはインストーラが管理している」ことを書き残す。
function WriteOwnerMarker(): Boolean;
var
  Marker: AnsiString;
begin
  Marker := AppIdGuid;
  Result := SaveStringToFile(
    AddBackslash(ExpandConstant('{app}')) + OwnerMarkerName, Marker, False);
end;

// フォルダに「.」「..」以外の中身があるか。
function DirHasEntries(const Dir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not FindFirst(AddBackslash(Dir) + '*', FindRec) then
    Exit;

  try
    repeat
      if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// インストール先が「他人のファイルが入っているフォルダ」なら中止する。
//
// 上書きインストールでは、前版が置いたものを片付けるために scripts\ や
// *.json をまとめて消す。そこへ最初から他人のファイルが混ざっていると、
// 次の更新でそれらを消してしまう。混ざる前に断る。
//
// ウィザードでもサイレントでも通る PrepareToInstall で見る
// （空文字列を返せば続行、文字列を返せばその内容を出して中止）。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Target: String;
begin
  Result := '';
  Target := ExpandConstant('{app}');

  if IsOurInstallAt(Target) then
    Exit; // 前版が入っている場所。片付けてよい。

  if not DirExists(Target) then
    Exit; // これから作るフォルダ。

  if not DirHasEntries(Target) then
    Exit; // 空のフォルダ。

  Result :=
    'インストール先に Hirake 以外のファイルがあります。' + #13#10 + #13#10 +
    Target + #13#10 + #13#10 +
    'このフォルダはインストーラが管理し、更新のたびに' + #13#10 +
    '前の版のファイルを削除します。他のファイルを置いたままにすると' + #13#10 +
    '次回の更新で消えてしまうため、インストールを中止しました。' + #13#10 + #13#10 +
    '空のフォルダか、新しいフォルダを指定してください。';
end;

// 関連付けスクリプトを 1 本実行する。成功したかどうかだけを返す。
function RunAssociationScript(const ScriptName, Extra: String): Boolean;
var
  ResultCode: Integer;
  Params: String;
begin
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\scripts\') + ScriptName + '"' + Extra;

  Result :=
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
         Params, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

// 関連付けの登録は [Run] ではなく ssPostInstall で行う。
// [Run] は終了コードを見られず、失敗を握りつぶしてしまうため
// （関連付けが無いことに気付けるのは、ユーザーが .md を開こうとした時になる）。
//
// 失敗してもインストールはロールバックしない。関連付けが無いだけで
// Hirake 自体は起動でき、後から手動で登録できる（設計 5-1）。
procedure RegisterAssociations();
begin
  if RunAssociationScript('register.ps1',
       ExpandConstant(' -ExePath "{app}\Hirake.exe"')) then
  begin
    // 「このインストーラが、この exe で登録した」ことを残す。
    RegWriteStringValue(HKEY_CURRENT_USER, OwnerKey, OwnerValue,
      ExpandConstant('{app}\Hirake.exe'));
    Exit;
  end;

  // SuppressibleMsgBox を使う。MsgBox は /VERYSILENT でも表示しようとして
  // 応答が無いまま止まる（無人インストールが永久に固まる）。
  SuppressibleMsgBox(
    'ファイルの関連付けの登録に失敗しました。' + #13#10 + #13#10 +
    'Hirake のインストール自体は完了しています。' + #13#10 +
    '関連付けは、後から次のコマンドで登録できます。' + #13#10 + #13#10 +
    'powershell -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\scripts\register.ps1') + '" -ExePath "' +
      ExpandConstant('{app}\Hirake.exe') + '"',
    mbInformation, MB_OK, IDOK);
end;

// 所有権の記録があるか（現在も所有しているかは問わない）。
function HasOwnershipRecord(): Boolean;
var
  Owned: String;
begin
  Result := RegQueryStringValue(HKEY_CURRENT_USER, OwnerKey, OwnerValue, Owned);
end;

// レジストリのコマンド行が、渡した exe を指しているか。
// register.ps1 が書く形は "<exe>" "%1" と決まっているため、完全一致で見る。
// 部分一致にすると "C:\Apps\Hirake.exe.bak" や、引数にたまたま同じパスを
// 含む別アプリのコマンドまで自分のものと誤判定する。
function CommandPointsTo(const Key, ExePath: String): Boolean;
var
  Current: String;
begin
  Result := False;
  if not RegQueryStringValue(HKEY_CURRENT_USER, Key, '', Current) then
    Exit;

  Result := CompareText(Trim(Current), '"' + ExePath + '" "%1"') = 0;
end;

// このインストーラが登録した関連付けが、今もそのまま生きているか。
//
// unregister.ps1 は .md / .markdown / Hirake.md / hirake:// をまとめて解除する。
// そのため「.md はこちらを指しているが hirake:// は別の Hirake.exe に
// 差し替えられている」状態で解除すると、利用者が自分で入れ替えた登録まで消える。
// 両方が自分を指しているときだけ、自分のものと見なす。
function OwnsCurrentAssociation(): Boolean;
var
  Owned: String;
begin
  Result := False;

  if not RegQueryStringValue(HKEY_CURRENT_USER, OwnerKey, OwnerValue, Owned) then
    Exit;

  Result := CommandPointsTo(CommandKey, Owned) and CommandPointsTo(SchemeCommandKey, Owned);
end;

procedure ForgetOwnership();
begin
  RegDeleteValue(HKEY_CURRENT_USER, OwnerKey, OwnerValue);
  // 値が無くなったキーは残さない（空のキーだけが残るのを避ける）。
  RegDeleteKeyIfEmpty(HKEY_CURRENT_USER, OwnerKey);
  RegDeleteKeyIfEmpty(HKEY_CURRENT_USER, 'Software\Hirake');
end;

// 解除して所有権の記録を消す。失敗したときだけ true 以外を返す。
function UnregisterAssociations(): Boolean;
begin
  Result := RunAssociationScript('unregister.ps1', '');
  if Result then
    ForgetOwnership();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  // 目印が書けないと、次回の更新で前版のファイルを片付けられなくなる。
  // インストール自体は成功しているので中止はしないが、黙って進めない。
  if not WriteOwnerMarker() then
    SuppressibleMsgBox(
      'インストール先に管理用のファイルを作成できませんでした。' + #13#10 + #13#10 +
      '次回このフォルダへ上書きインストールするとき、前の版の' + #13#10 +
      'ファイルが残ることがあります。気になる場合は、いったん' + #13#10 +
      'アンインストールしてから入れ直してください。',
      mbInformation, MB_OK, IDOK);

  if WizardIsTaskSelected('associate') then
  begin
    RegisterAssociations();
    Exit;
  end;

  // 上書きインストールで「関連付ける」を外した場合。前版でこのインストーラが
  // 登録したままだと、チェックを外したのに関連付けが残る。
  if OwnsCurrentAssociation() then
  begin
    if not UnregisterAssociations() then
      SuppressibleMsgBox(
        'ファイルの関連付けの解除に失敗しました。' + #13#10 + #13#10 +
        '.md の関連付けが残っている可能性があります。' + #13#10 +
        '次のコマンドで解除できます。' + #13#10 + #13#10 +
        'powershell -ExecutionPolicy Bypass -File "' +
          ExpandConstant('{app}\scripts\unregister.ps1') + '"',
        mbInformation, MB_OK, IDOK);
    Exit;
  end;

  // 所有していない（利用者が登録先を差し替えた等）。関連付けには触らないが、
  // 「関連付けない」を選んだ以上、こちらの所有権は放棄する。残しておくと、
  // 後でコマンドがたまたま同じパスに戻ったときに解除してしまう。
  if HasOwnershipRecord() then
    ForgetOwnership();
end;

// アンインストール時の解除。[UninstallRun] ではなくここで行うのは、
// 終了コードを見るため。失敗に気付けないと、存在しない exe を指したままの
// レジストリが残り、解除スクリプトも一緒に消えて直せなくなる。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall はファイル削除の前。順序が逆だと unregister.ps1 自体が消えている。
  if CurUninstallStep <> usUninstall then
    Exit;

  if not OwnsCurrentAssociation() then
  begin
    // このインストーラの登録ではない（利用者が自分で登録した／別の場所を指している）。
    // 記録だけ落として、関連付けには触らない。
    if HasOwnershipRecord() then
    begin
      ForgetOwnership();

      // 登録したのはこちらなのに、今は自分を指していない。差し替えたのは
      // 利用者なので消さないが、こちらを指す登録が残っている可能性は伝える。
      SuppressibleMsgBox(
        '.md や hirake:// の関連付けが、インストール時とは' + #13#10 +
        '別の場所を指しているため、解除しませんでした。' + #13#10 + #13#10 +
        '削除済みの Hirake を指す登録が残っている場合は、' + #13#10 +
        'リポジトリの scripts\unregister.ps1 で解除できます。',
        mbInformation, MB_OK, IDOK);
    end;
    Exit;
  end;

  if UnregisterAssociations() then
    Exit;

  SuppressibleMsgBox(
    'ファイルの関連付けの解除に失敗しました。' + #13#10 + #13#10 +
    'アンインストールは続行しますが、.md の関連付けが' + #13#10 +
    '削除済みの Hirake を指したまま残る可能性があります。' + #13#10 + #13#10 +
    '残った場合は、次のいずれかで解除してください。' + #13#10 +
    '・Hirake を入れ直してからアンインストールする' + #13#10 +
    '・リポジトリの scripts\unregister.ps1 を実行する',
    mbInformation, MB_OK, IDOK);
end;
