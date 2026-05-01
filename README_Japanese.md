<img src="README_Image/LargeIcon.png" width="60" height="60" alt="icon" align="left" />

# WindowTabs

**Language:** [English](README.md)

WindowTabs はインターフェースを持たない Windows アプリケーションや、異なる実行ファイル間でタブ UI を有効にするユーティリティです。Chrome と Edge をタブで管理、複数の Excel や Word のウィンドウをタブで管理が可能になります。

![Tabs](README_Image/Tabs.png)

私の作成しているこのバージョン (ss_jp_yyyy.mm.dd) は payaneco 氏のリポジトリからフォーク、leafOfTree 氏が行ったコード実装の一部が組み込まれています。メンテナンスは、[Satoshi Yamamoto (@standard-software)](https://github.com/standard-software) が行っています。フォーク元の系譜は [プロジェクトの経緯](#プロジェクトの経緯) を参照してください。

Visual Studio 2026 Community Edition でコンパイルできます。
- https://github.com/standard-software/WindowTabs

## 目次
- [バージョン](#バージョン)
- [ダウンロード](#ダウンロード)
- [インストール](#インストール)
- [使用方法](#使用方法)
- [機能](#機能)
- [設定](#設定)
- [リンク](#リンク)
- [プロジェクトの経緯](#プロジェクトの経緯)
- [ライセンス](#ライセンス)
- [コメント](#コメント)

## バージョン

最新のバージョン: **ss_jp_2026.05.01**

詳細な更新履歴と変更ログについては、[version.md](version.md) を参照してください。


## ダウンロード

**対応している OS:** Windows 10、 Windows 11

<a href="https://github.com/standard-software/WindowTabs/releases">![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/standard-software/windowtabs/total)</a>

[releases](https://github.com/standard-software/WindowTabs/releases) ページからビルド済みのファイルをダウンロードできます。

2 つのダウンロードオプションがあります:

- **WtSetup.msi** - 自動インストールとアンインストールをサポートしている Windows インストーラーパッケージ版
- **WindowTabs.zip** - 任意の場所で展開して実行可能なポータブル版

## インストール

### MSI インストーラー版の使用方法 (WtSetup.msi)

1. [Releases](https://github.com/standard-software/WindowTabs/releases) ページから `WtSetup.msi` をダウンロード
2. インストーラーを実行してインストールウィザードに従って操作します
3. インストール先のディレクトリを選択 (既定: Program Files\WindowTabs)
4. デスクトップとスタートメニューにショートカットが自動で作成されます
5. オプションでインストール後に WindowTabs を起動

### ポータブル版の使用方法 (WindowTabs.zip)

1. [Releases](https://github.com/standard-software/WindowTabs/releases) ページから `WindowTabs.zip` をダウンロード
2. アーカイブを任意の場所に展開します
3. `WindowTabs.exe` を実行
4. WindowTabs がバックグラウンドで実行され、トレイアイコンが表示されます


## 使用方法

- `WindowTabs.exe` を実行
- トレイアイコンを右クリックで設定にアクセスできます
- 設定の[プログラム]のタブからタブ化したいプログラムを選びます
- 指定のプログラムのWindowにタブがつきます。
- タブを右クリックでタブ固有のオプションにアクセスできます
- タブをドラッグ&ドロップでウィンドウを整理できます

## 機能

### タブのドラッグ&ドロップ
- タブをドラッグして同じグループ内で順番を変更
- タブをドラッグして新規ウィンドウに分割、別グループに連結

### タブの管理

- **タブのコンテキストメニュー**
  - 新規起動 : (exe名)起動
    - 新しいタブ : このタブ((exe名))の右
    - 新しいウィンドウ 位置指定 (「位置移動」と同じサブメニュー、先頭に「同じ位置」項目あり)
    - 新しいウィンドウ 他のグループへ連結
  - 位置移動
    - 左スナップ
    - 右スナップ
    - スナップ その他
      - 上スナップ
      - 下スナップ
      - スナップ 90%
        - 左 / 右 / 上 / 下
        - 左上 / 右上 / 左下 / 右下
        - 中央 / 水平方向に中央 / 垂直方向に中央
      - スナップ 70% / 50% / 30% (スナップ 90% と同じ項目)
      - スナップ ディスプレイ全体
      - スナップ デスクトップ全体
    - 移動
      - 左端 / 右端 / 上端 / 下端
      - 左上 / 右上 / 左下 / 右下
    - (各ディスプレイのサブメニュー。先頭に「このディスプレイと同じ位置」)
  - 他のグループへ連結
  - タブの分離と分割
    - このタブを分離して位置移動 (「位置移動」と同じサブメニュー)
    - 他のグループへ連結
    - 左側{N}タブを分割して位置移動
    - 左側{N}タブを分割して他のグループへ連結
    - 右側{N}タブを分割して位置移動
    - 右側{N}タブを分割して他のグループへ連結
  - タブを閉じる
    - このタブを閉じる : (タブ名)
    - 左側の{N}タブを閉じる
    - 右側の{N}タブを閉じる
    - 他のタブを閉じる
    - 全てのタブを閉じる
  - スナップ時のタブの余白
    - 上に余白をあける
  - タブの位置
    - 全てのタブを左寄せ
    - 全てのタブを右寄せ
    - 個別タブ配置
      - このタブを (左寄せ|右寄せ) にする : (タブ名)
      - 左側{N}タブを (左寄せ|右寄せ) にする
      - 右側{N}タブを (左寄せ|右寄せ) にする
  - タブのピン止め
    - このタブをピン止め : (タブ名)
    - このタブのピン止めを外す : (タブ名)
    - 左側{N}タブをピン止め
    - 左側{N}タブのピン止めを外す
    - 右側{N}タブをピン止め
    - 右側{N}タブのピン止めを外す
  - タブの色設定
    - このタブの色を設定 : (タブ名)
      - 赤 / 青 / 緑 / 黄色 / 紫 / オレンジ / ピンク
      - (同じ7色の下線バリエーション)
      - (同じ7色の枠線バリエーション)
    - このタブの色設定を解除
    - 左側{N}タブの色を設定 (同じ色選択)
    - 左側{N}タブの色設定を解除
    - 右側{N}タブの色を設定 (同じ色選択)
    - 右側{N}タブの色設定を解除
  - タブ名の編集
    - タブの名前を変更
    - タブの名前をリセット
  - システム
    - (exe名) のパスをコピー
    - ウィンドウタイトルをコピー : (タイトル)
    - (exe名) のフォルダを開く
    - このプロセスの強制終了
  - 設定...

### 新規起動

![Popup Menu](README_Image/PopupMenu.png)

### 位置移動

![Popup Menu Move Other](README_Image/PopupMenuMoveOther.png)

### 他のグループへ連結

![](README_Image/MoveTabGroupToGroup.png)

### このタブを分離 / 右側/左側を分割

![Tab Split Move Position](README_Image/SplitTabs.png)

### タブを閉じる

![Popup Menu Close Tab](README_Image/PopupMenuCloseTab.png)

### ピン止めタブ

![Pinned Tabs Icon](README_Image/PinnedTabIcon.png)
![Pinned Tabs Width](README_Image/PinnedTabWidth.png)
![Pinned Tabs Menu](README_Image/PinnedTabMenu.png)

### タブのカラー

![Pinned Tab Color Tab](README_Image/PinnedColorTab.png)

### タブごとの寄せ設定

タブグループ内で、タブごとに個別に左寄せ・右寄せを設定できます:

### ダークモード / ライトモード

ライトモードが既定ですが、スクリーンショットのようにコンテキストメニュー (ポップアップメニュー) でのダークモードをサポートしています。

- 外観設定の「ダークモードメニュー」のチェックボックスで切り替え
- タブやタスクトレイのコンテキストメニューへの対応
- 設定ダイアログへの対応

### マルチディスプレイと高 DPI をサポート

- 適切なウィンドウの配置によるマルチディスプレイのサポート
- DPI を考慮したウィンドウの配置
- ドロップ時にウィンドウサイズを自動で変更してディスプレイのサイズが超えてしまう問題を防止

### 仮想デスクトップをサポート

WindowTabs は Windows の仮想デスクトップ (Win+Tab) をサポートしています:

- 仮想デスクトップを切り替えてもタブグループを保持
- UWP アプリ（設定、電卓など）が他の仮想デスクトップにある場合は適切に非表示
- WindowTabs の再起動時に全ての仮想デスクトップのタブグループ状態を復帰

### UWP アプリをサポート

- UWP (Universal Windows Platform) をサポート
- UWP ウィンドウの Z オーダーを自動で処理し、タブの表示を適切に維持
- UWP アプリで作業する際もタブの表示を維持
- 他の仮想デスクトップにあるアプリのクローク状態を適切に検出

### 多言語をサポート

- 英語と日本語、簡体と繁体の中国語をサポート
- 日本語の関西弁、東北弁版を同梱
- 言語ファイルでのあらゆる言語をサポート **(WtProgram/Language)**
- 再起動なしで言語を変更可能
- トレイメニューから言語を変更可能

![Task Tray Menu](README_Image/TaskTrayMenuImage.png)

### 無効にする機能

トレイメニューから WindowTabs の起動を一時的に無効にできます:

### タブグループの永続化

WindowTabs は再起動時や無効化時にタブグループの設定を保持します:

### Watchdog による自動再起動

- 以下の状況で WindowTabs がフリーズする場合があります:
  - モニターの切り替え
  - スリープや休止状態からの復帰
  - Windows のディスプレイ設定の変更
- Watchdog 機構が無応答を検出し、自動的に再起動します
- 再起動後もタブグループの設定は保持・復元されます

## 設定

トレイアイコンを右クリックで「設定」を選択するか、タブを右クリックで「設定...」を選択して設定にアクセスします。

### プログラムタブ

タブと自動グループ化の動作を使用するプログラムを構成できます。

- **タブ**: プログラムごとにタブ機能の有効/無効を設定
- **自動グループ化**: 有効にすると、同じプログラムのウィンドウが自動で同じタブグループにまとめられます
- **カテゴリー 1-10**: プログラムにカテゴリーを割り当てて、異なるアプリ間の自動グループ化が可能
  - 同じカテゴリーに属するプログラムは、実行ファイルが異なっても自動でグループ化されます
  - 例えば、Word・Excel・PowerPoint などを同じカテゴリーに設定すれば、Office 系アプリが自動でグループ化されます
  - カテゴリー列は、自動グループ化が有効なプログラムにのみ表示されます
- **すべての設定を表示**: チェックボックスで、現在実行していないプログラムの設定も表示可能
- **削除ボタン [x]**: 実行中でないプロセスの設定を削除

![Settings Programs](README_Image/SettingsPrograms.png)

### タブの外観

タブの視覚的な外観をカスタマイズできます:
- カスタムカラーテーマ機能
  - よいカラーテーマを作られた方は、ぜひ [GitHub Issues](https://github.com/standard-software/WindowTabs/issues) に投稿してください。既定のカラーテーマとして組み込ませていただく場合もあります。

![Settings Appearance](README_Image/SettingsAppearance.png)
![Settings AppearanceColorTheme](README_Image/SettingsAppearanceColorTheme.png)
![Settings AppearanceColorThemeClipboard](README_Image/SettingsAppearanceColorThemeClipboard.png)

### タブの動作

タブの動作を構成することができます:

![Settings Behavior](README_Image/SettingsBehavior.png)

### ワークスペースタブ

これは元の WindowTabs の機能から変更されていません。

## ソースからビルド

### 前提条件

- Visual Studio 2026 Community Edition
- WiX Toolset v3.11 またはそれ以降 (MSI インストーラー版のビルド)

### ビルドスクリプト

プロジェクトのルートにビルドスクリプトが用意されています:

- **build_release.bat** - MSI インストーラー版とポータブル ZIP 版の両方をビルド
  - 出力: `exe\installer\WtSetup.msi`
  - 出力: `exe\zip\WindowTabs.zip`

バッチファイルを実行で配布パッケージを作成することができます。


## リンク

### 日本語のリソース

- WindowTabs のダウンロード・使い方 - フリーソフト100
  https://freesoft-100.com/review/windowtabs.html

- どんなウィンドウもタブにまとめられる「WindowTabs」に日本語派生プロジェクトが誕生（窓の杜） - Yahoo!ニュース
  https://news.yahoo.co.jp/articles/523e4c5b9db424bb1edfc582d647c1624a9b7502 (404 Not Found)

- どんなウィンドウもタブにまとめられる「WindowTabs」に日本語派生プロジェクトが誕生 - 窓の杜
  https://forest.watch.impress.co.jp/docs/news/2067165.html

- WindowTabs のダウンロードと使い方 - ｋ本的に無料ソフト・フリーソフト
  https://www.gigafree.net/utility/window/WindowTabs.html

- C# - WindowTabs というオープンソースを改良してみたいのですがビルドができません。何か必要なものがありますか？ - スタック・オーバーフロー
  https://ja.stackoverflow.com/questions/53770/windowtabs-というオープンソースを改良してみたいのですがビルドができません-何か必要なものがありますか

- 全Windowタブ化。Setsで頓挫した夢の操作性をオープンソースのWindowTabsで再現する。 #Windows - Qiita
  https://qiita.com/standard-software/items/dd25270fa3895365fced

## プロジェクトの経緯

元々は Maurice Flanagan 氏によって2009年に開発され、当時は無料版と有料版が提供されていました。開発者は現在、このユーティリティをオープンソース化しています。

- https://github.com/mauricef/WindowTabs (404 Not Found)

redgis 氏がフォークし、VS2017 / .NET 4.0 に移行しました。

- https://github.com/redgis/WindowTabs

medlir 氏がコードを配置しています。
- https://github.com/medlir/WindowTabs

コミットログをみると、Mossy Flanagan 氏が初期のコミットを行っています。
- https://github.com/mossy-xyz

payaneco 氏がmedlir/WindowTabsのコードをフォークしました。
- https://github.com/payaneco/WindowTabs
- https://github.com/payaneco/WindowTabs/network/members
- https://ja.stackoverflow.com/a/53822

leafOfTree 氏も様々な改良を加えたフォークを作成しています:
- https://github.com/leafOfTree/WindowTabs
- https://github.com/leafOfTree/WindowTabs/network/members

## ライセンス

このプロジェクトはオープンソースであり、MIT ライセンスに基づいています。

## クレジット

- オリジナルの開発者: Maurice Flanagan
- フォークの貢献者: redgis、payaneco、leafOfTree
- 現在のメンテナー: Satoshi Yamamoto (standard-software)

## コメント

何か問題がありましたら、GitHub Issues またはメールでお問い合わせください: `standard.software.net@gmail.com`

