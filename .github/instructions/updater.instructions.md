---
description: "実装の更新・バグ修正・機能追加のあとにドキュメントを同期するためのルール。ユーザーに影響する変更をしたら CHANGELOG.md と README.md / README.ja-jp.md を更新して整合を保つ。Use when implementing a feature, fixing a bug, changing behavior, adding or removing a command/setting/supported file, or otherwise making user-facing changes — keep the CHANGELOG and both READMEs (English + Japanese) in sync."
applyTo: ["src/**", "package.json"]
---
# ドキュメント更新ルール

実装の更新・修正・機能追加を行い、**ユーザーに影響する変更**が生じたら、作業の締めくくりに次のドキュメントを更新する。

## いつ更新するか

- 更新が必要: 機能追加 / 挙動変更 / バグ修正 / 対応ファイル・コマンド・設定・動作要件・制限事項・タブ構成の変化。
- 原則不要: 挙動が変わらない内部リファクタリングやコメント修正のみ。

## CHANGELOG.md

- [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) 形式・[Semantic Versioning](https://semver.org/) に従う。
- 追記先は必ず `## [Unreleased]` セクション。新しいバージョン番号やリリース日付は勝手に作らない（リリース作業時のみ）。
- 変更種別ごとに見出しを使い分ける。無ければ作成する。
  - `### Added` … 新機能
  - `### Changed` … 既存機能の変更
  - `### Deprecated` … 非推奨化
  - `### Removed` … 削除
  - `### Fixed` … バグ修正
  - `### Security` … セキュリティ修正
- 実装詳細ではなく、ユーザー視点の簡潔な説明を書く。既存エントリと同じ言語（英語）で記述する。

## README.md / README.ja-jp.md

- 2 ファイルは英語版・日本語版の対訳。**必ず両方を同時に更新**し、内容・節構成を揃える（片方だけ更新して乖離させない）。
- 次が変わったら README を更新する: 機能一覧 / タブ構成（「14-tab」などの個数表記を含む）/ コマンド表 / 設定 / 対応ファイル / 動作要件 / 制限事項 / ロードマップ。
- 節を増減した場合は、各ファイル冒頭の目次（Table of Contents / 目次）も更新する。
- 新しいコマンドや設定を追加したら、対応する表（コマンド表・設定）にも忘れず反映する。

## 整合性チェック

- タブ数・機能数などの数値表記が README.md と README.ja-jp.md で一致していること。
- 機能が完成してロードマップの項目を満たした場合は、該当項目のチェック状態を見直す。
- `package.json` の `version` と CHANGELOG のバージョン表記に矛盾がないこと。
