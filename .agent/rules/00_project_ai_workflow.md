# Project AI Workflow Rule for Antigravity

このワークスペースでは、Antigravityを主な実装環境として使い、Codex CLIを補助的な調査・レビュー・GitHub整理係として使う。

## 常時守ること

- 推測で実装しない。
- 仕様・論文・API・外部コードが関係する場合は、まず調査する。
- 調査結果は `docs/research/`、実装計画は `docs/implementation_plans/` に保存する。
- 外部リポジトリは、まずGitHub上でREADME、license、requirementsを確認する。
- 深く読む・実行する必要がある場合だけ `external_repos/` にcloneする。
- `external_repos/` はGit管理に含めない。

## 詳細ルール

必要時は以下を読む。

- `docs/agent_rules/github_workflow.md`
- `docs/agent_rules/codex_collaboration.md`
- `docs/agent_rules/search_research_workflow.md`
- `docs/agent_rules/paper_to_code_workflow.md`
- `docs/agent_rules/external_code_policy.md`
- `docs/agent_rules/error_investigation.md`

## ユーザーが「プッシュして」と言った場合

1. `git status` を確認する。
2. `git diff --stat` で変更ファイルを確認する。
3. 必要ならCodex CLIに差分要約とcommitメッセージ案を作らせる。
4. 秘密情報、大容量データ、外部リポジトリ本体が含まれていないか確認する。
5. 問題なければcommitしてpushする。
6. 結果を日本語で報告する。

## ユーザーが「調べて」「論文を探して」「この論文を実装して」と言った場合

1. まず `docs/research_requests/` に調査依頼書を作る。
2. Codex CLIに調査依頼書を読ませる。
3. 論文API、検索MCP、GitHub検索を使って調査する。
4. 結果を `docs/research/` に保存する。
5. 必要なら `docs/implementation_plans/` に実装計画を作る。
6. Antigravityは実装計画を読んで、最小実装から作る。
