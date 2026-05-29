# 検索・調査ワークフロー

## 基本方針

Antigravityに直接すべての検索を頑張らせるのではなく、調査タスクを発行し、Codex CLIや検索MCPに調査させる。

## 使い分け

- Context7 / Ref MCP:
  - ライブラリ、API、フレームワークの公式ドキュメント確認。
- Tavily / Brave Search:
  - 一般Web検索、エラー事例、技術記事、実装事例。
- Google Developer Knowledge:
  - Google Cloud、Firebase、Android、MapsなどGoogle製品の公式確認。
- GitHub Search / GitHub MCP:
  - 公式コード、README、requirements、issue、licenseの確認。
- Semantic Scholar / arXiv / OpenReview / Crossref:
  - 論文検索、メタデータ確認、引用確認。

## 調査の流れ

1. 何を知りたいかを1〜3個の質問に分解する。
2. `docs/research_requests/` に調査依頼書を作る。
3. 公式ドキュメントがある場合は、公式を優先する。
4. 一般Web情報は補助として使う。
5. 検索結果を大量に貼り付けず、必要な要点だけまとめる。
6. 結果を `docs/research/` に保存する。
7. 実装判断が必要なら `docs/implementation_plans/` に実装計画を作る。

## 調査メモに必ず書くこと

- 調査目的
- 検索語
- 参照URL
- 分かったこと
- 実装に使う判断
- 不確かな点
- 次にAntigravityが行うこと
