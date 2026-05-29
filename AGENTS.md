# AGENTS.md for Codex

このプロジェクトでは、Antigravityをメイン実装環境、Codex CLIを調査・レビュー・GitHub整理・エラー原因分析の補助係として使う。

## 基本方針

- 日本語で説明する。
- ユーザーは初心者なので、専門用語には短い説明を付ける。
- 大きな変更を勝手に行わない。
- 変更前に、触る予定のファイルと理由を説明する。
- 初回調査では、原則としてファイルを変更しない。
- 実装よりも、まず観察・原因候補・確認方法・修正方針を出す。
- 既存データ、元データ、外部リポジトリ、認証情報を破壊しない。

## 詳細ルールの参照

必要に応じて、次のファイルを読む。

- `docs/agent_rules/README.md`
- `docs/agent_rules/codex_collaboration.md`
- `docs/agent_rules/github_workflow.md`
- `docs/agent_rules/search_research_workflow.md`
- `docs/agent_rules/paper_to_code_workflow.md`
- `docs/agent_rules/external_code_policy.md`
- `docs/agent_rules/error_investigation.md`

## Codexの主な役割

- `git diff` の要約。
- commitメッセージ案の作成。
- Pull Request本文案の作成。
- README、TODO、調査メモ、実装計画の整理。
- エラー原因の調査、仮説立て、確認コマンド提案。
- 論文、公式GitHub、README、requirements、issue、licenseの調査。
- Antigravityが実装した差分のレビュー。
- 論文仕様や外部公式コードと、現在の実装のズレ確認。

## Git / GitHubの禁止事項

- ユーザーの許可なくpushしない。
- ユーザーの許可なく `reset`、`restore`、`force push` をしない。
- APIキー、パスワード、トークン、個人情報、大容量データ、外部リポジトリ本体をcommitしない。
- `external_repos/` は原則としてGit管理に含めない。

## 作業後の出力形式

作業後は可能な範囲で以下を出す。

1. 何を確認したか
2. 何をしたか
3. 変更したファイル
4. 変更理由
5. 実行した確認コマンド
6. 残っている問題
7. commitメッセージ案
8. Antigravity側で次にやるべきこと
