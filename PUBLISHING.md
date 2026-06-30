# 公開ガイド (PUBLISHING)

Netlog Viewer を Visual Studio Code Marketplace へ公開する手順をまとめます。
ビルド・デバッグ手順は [DEVELOPMENT.md](./DEVELOPMENT.md) を参照してください。

---

## 目次

- [公開ガイド (PUBLISHING)](#公開ガイド-publishing)
  - [目次](#目次)
  - [前提](#前提)
  - [初回準備](#初回準備)
    - [1. Publisher の作成](#1-publisher-の作成)
    - [2. Personal Access Token (PAT) の発行](#2-personal-access-token-pat-の発行)
    - [3. vsce でのログイン](#3-vsce-でのログイン)
  - [リリース手順](#リリース手順)
  - [VSIX のローカル検証](#vsix-のローカル検証)
  - [バージョニング](#バージョニング)
  - [リリースチェックリスト](#リリースチェックリスト)
  - [トラブルシューティング](#トラブルシューティング)

---

## 前提

- パッケージング・公開には [`@vscode/vsce`](https://github.com/microsoft/vscode-vsce) を使用します（`devDependencies` に含まれます）。
- `package.json` の `publisher` は **`tmidorikawa`** です。Marketplace 上の Publisher と一致している必要があります。
- 公開には Azure DevOps の **Personal Access Token (PAT)** が必要です。

---

## 初回準備

### 1. Publisher の作成

[Visual Studio Marketplace の管理ページ](https://marketplace.visualstudio.com/manage) で、`package.json` の `publisher`（`tmidorikawa`）と一致する Publisher を作成します。

### 2. Personal Access Token (PAT) の発行

1. [Azure DevOps](https://dev.azure.com/) にサインインします（Marketplace と同じ Microsoft アカウント）。
2. **User settings → Personal access tokens** で新しいトークンを発行します。
   - **Organization**: `All accessible organizations`
   - **Scopes**: `Marketplace → Manage`
3. 発行されたトークンを安全に控えます（再表示できません）。

> PAT は機密情報です。リポジトリやチャットに貼り付けないでください。

### 3. vsce でのログイン

```bash
npx vsce login tmidorikawa
```

プロンプトに従って PAT を入力します（PAT はターミナルに直接入力してください）。

---

## リリース手順

1. クリーンな状態でビルドとテストが通ることを確認します。

   ```bash
   dotnet tool restore
   npm install
   npm run build
   npm test
   ```

2. `package.json` の `version` を更新し、[CHANGELOG.md](./CHANGELOG.md) に今回のバージョンの変更点を追記します（[バージョニング](#バージョニング)参照）。

3. `.vsix` を生成します。

   ```bash
   npm run package
   ```

   これは `npm run build` ののち `vsce package` を実行し、`netlog-viewer-<version>.vsix` を生成します。

   > `package` スクリプトは、まだ `repository` 未設定でもローカル生成できるよう `--no-rewrite-relative-links --allow-missing-repository` を付けています。
   > **Marketplace へ公開する際は、`package.json` に `repository`（GitHub などの URL）を設定してください。** これにより README の相対リンクが正しいリポジトリ URL へ書き換えられます。

4. 生成された `.vsix` を[ローカル検証](#vsix-のローカル検証)します。

5. Marketplace へ公開します。

   ```bash
   # 生成済みの .vsix を公開
   npx vsce publish

   # もしくはバージョンを上げつつ公開（patch/minor/major）
   npx vsce publish patch
   ```

---

## VSIX のローカル検証

公開前に、生成した `.vsix` を実際にインストールして動作を確認します。

```bash
code --install-extension netlog-viewer-<version>.vsix
```

- NetLog ログ（`*net-export*.json` など）を開き、各タブが表示されることを確認します。
- `.vsix` に不要なファイル（`src/` や `build/` 等）が含まれていないことを確認します。

  ```bash
  npx vsce ls
  ```

---

## バージョニング

- [セマンティック バージョニング](https://semver.org/lang/ja/)（`MAJOR.MINOR.PATCH`）に従います。
- `vsce publish patch|minor|major` を使うと、`package.json` の更新・タグ付けと公開をまとめて行えます。
- Marketplace は同一バージョンの再公開を許可しません。公開前にバージョンを上げてください。

---

## リリースチェックリスト

- [ ] `npm run build` と `npm test` が成功する
- [ ] `package.json` の `version` を更新した
- [ ] [CHANGELOG.md](./CHANGELOG.md) に今回のバージョンの変更点を追記した
- [ ] [README.md](./README.md) の機能・制限事項・ロードマップが最新
- [ ] `npx vsce ls` で同梱物が `dist/` と `media/` 等に限定されている
- [ ] `.vsix` をローカルインストールして主要タブの表示を確認した
- [ ] 公開後、Marketplace のページ表示を確認した

---

## トラブルシューティング

| 症状 | 対処 |
| --- | --- |
| `vsce` が見つからない | `npm install` を実行する（`@vscode/vsce` は devDependency）。 |
| 認証エラー（401/403） | PAT のスコープ（`Marketplace → Manage`）と有効期限、Publisher 名の一致を確認する。 |
| `.vsix` にソースが含まれる | [.vscodeignore](./.vscodeignore) の除外設定を確認する。 |
| 同一バージョンで公開できない | `package.json` の `version` を上げる（または `vsce publish patch`）。 |
