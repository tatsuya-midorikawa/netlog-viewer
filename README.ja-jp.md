# netlog-viewer

`netlog-viewer` は、Chromium 系ブラウザーで採取できる `net-export`（NetLog）ログを閲覧するための Visual Studio Code 拡張機能です。

---

## 目次

- [netlog-viewer](#netlog-viewer)
  - [目次](#目次)
  - [主な機能](#主な機能)
  - [動作要件](#動作要件)
  - [インストール](#インストール)
    - [Visual Studio Code Marketplace から](#visual-studio-code-marketplace-から)
    - [VSIX ファイルから](#vsix-ファイルから)
  - [使い方](#使い方)
  - [対応ファイル](#対応ファイル)
  - [コマンド](#コマンド)
  - [設定](#設定)
  - [制限事項](#制限事項)
  - [ロードマップ](#ロードマップ)
  - [コントリビュート](#コントリビュート)
  - [スポンサー](#スポンサー)
  - [ライセンス](#ライセンス)

---

## 主な機能

- **NetLog ログのビューアー**: `net-export` で採取した `.json` ログを、VS Code 内のカスタムエディターで閲覧できます。
- **14 タブ構成**:
  - **Import** — エクスポート日時・キャプチャモード・ブラウザー/OS 情報・コマンドライン・ユーザーコメント
  - **Events** — ソース単位にグルーピングされたイベント一覧。フィルタ（`type:` / `id:` / `is:active|error` / `sort:`）・選択・イベントトレース詳細（`t=` / `st=` / `[dt=]`）
  - **Timeline** — ソケット数・リクエスト数・転送バイト数などの時系列をキャンバスに描画（ホイールでズーム、ドラッグでパン）
  - **Proxy / DNS / Sockets / StreamPool / Alt-Svc / HTTP/2 / QUIC / Reporting / Cache / Modules / Prerender** — 各サブシステムの状態テーブル
- **読み取り専用・完全オフライン**: ファイルへの書き込みや外部ネットワーク通信は行いません。
- **外部ランタイムライブラリ不使用**: パーサー・ビューアーともに標準ライブラリのみでフルスクラッチ実装（詳細は [DEVELOPMENT.md](./DEVELOPMENT.md)）。
- **`.json` / `.netlog` / `.gz`（gzip）** に対応。

---

## 動作要件

- Visual Studio Code `1.90.0` 以降
- 追加のランタイムや外部アプリケーションのインストールは **不要**

---

## インストール

### Visual Studio Code Marketplace から

1. VS Code のサイドバーから **拡張機能（Extensions）** ビューを開く（`Ctrl+Shift+X` / `⌘+Shift+X`）
2. `Netlog Viewer` を検索
3. **Install** をクリック

### VSIX ファイルから

```bash
code --install-extension netlog-viewer-<version>.vsix
```

または、コマンドパレット（`Ctrl+Shift+P` / `⌘+Shift+P`）から
**「Extensions: Install from VSIX...」** を実行し、`.vsix` ファイルを選択します。

---

## 使い方

1. Chromium 系ブラウザーでログを採取します。
   - Chrome: `chrome://net-export/`、Microsoft Edge: `edge://net-export/`
   - **Start Logging To Disk** で保存先 `.json` を指定し、再現操作を行ったのち **Stop Logging** を押します。
2. 採取した `.json` を VS Code で開きます。
   - ファイル名が[対応ファイル](#対応ファイル)のパターンに一致する場合は、自動的に Netlog Viewer で開きます。
   - 一致しない `.json` を開きたい場合は、エクスプローラーでファイルを右クリック →
     **「Open With…」→ Netlog Viewer** を選択するか、コマンド **「Netlog Viewer: Open File」** を実行します。
3. 左側のタブを切り替えて各サブシステムの状態を確認します。Events タブではフィルタ入力でソースを絞り込み、行を選択すると右ペインにイベントトレースが表示されます。

---

## 対応ファイル

カスタムエディターは、以下のファイル名パターンに自動で関連付きます（`.json` を一律には乗っ取りません）。

- `*.netlog`
- `*.netlog.json`（および `*.netlog.json.gz`）
- `*net-export*.json`（および `*net-export*.json.gz`）
- `*net_log*.json`
- `*netlog*.json`

`.gz`（gzip 圧縮）は自動的に展開されます。上記に一致しないファイルは「Netlog Viewer: Open File」コマンドから開けます。

---

## コマンド

コマンドパレット（`Ctrl+Shift+P` / `⌘+Shift+P`）から利用できます。

| コマンド | コマンドID | 説明 |
| --- | --- | --- |
| Netlog Viewer: Open File | `netlogViewer.openFile` | ファイル選択ダイアログから NetLog ログを開きます。 |

---

## 設定

現在、ユーザー設定項目はありません。描画上限や既定タブなどの設定は[ロードマップ](#ロードマップ)で検討しています。

---

## 制限事項

- **エクスポート済みログの閲覧専用**です。`net-internals` のようなライブブラウザーへのリアルタイム接続・取得には対応していません（情報タブはログに含まれる `polledData` を表示し、データが無いタブは自動的に非表示になります）。
- 対応する圧縮形式は **gzip（`.gz`）のみ**で、`.zip` には対応していません。
- 一部の情報タブ（Proxy 設定 / DNS 設定 / Reporting の clients・NEL）は、現状 JSON を整形して表示します。
- 読み取り専用のため、ログの編集・保存は行えません。

---

## ロードマップ

- [ ] ユーザー設定（描画上限・既定タブなど）
- [ ] 大容量ログ向けの仮想化・段階的描画
- [ ] 情報タブの詳細整形と、イベントソースへのリンク
- [ ] `.zip` 形式への対応

---

## コントリビュート

仕様・設計やビルド／デバッグ手順は [DEVELOPMENT.md](./DEVELOPMENT.md)、Marketplace への公開は [PUBLISHING.md](./PUBLISHING.md) を参照してください。

---

## スポンサー

本プロジェクトの開発を応援していただける方は、[GitHub Sponsors](https://github.com/sponsors/tatsuya-midorikawa) からのご支援を歓迎します。
いただいたご支援は、機能の改善や継続的なメンテナンスに活用させていただきます。

---

## ライセンス

本プロジェクトは [MIT License](./LICENSE) の下で公開されています。
