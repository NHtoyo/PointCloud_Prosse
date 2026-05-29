# 情報系論文から実装するワークフロー

## 基本方針

論文から直接完成コードを生成しない。

必ず、次の順に進める。

1. 論文・関連コードを調査する。
2. 実装に必要な情報を抽出する。
3. 実装仕様書へ変換する。
4. 最小実装を作る。
5. 実行確認・テストを行う。
6. 必要に応じて拡張する。

## 見るべき情報

- 入力形式
- 出力形式
- 前処理
- アルゴリズムの手順
- 数式とコードの対応
- 損失関数
- 評価指標
- データセット
- 必要ライブラリ
- 公式コードの有無
- README
- requirements / environment.yml
- license
- issueで報告されているエラー
- 再現性

## 優先順位

1. 著者公式コード
2. 論文本文
3. supplementary material
4. README / config / requirements
5. issue / discussion
6. 第三者実装
7. ブログ記事

## 保存先

- 調査依頼書: `docs/research_requests/YYYY-MM-DD_topic.md`
- 論文調査メモ: `docs/research/YYYY-MM-DD_topic_papers.md`
- 実装計画: `docs/implementation_plans/YYYY-MM-DD_topic_plan.md`
- 再現メモ: `docs/reproduction/YYYY-MM-DD_topic_repro.md`

## Antigravityの実装前チェック

- 入力データ形式は分かったか。
- 出力形式は分かったか。
- 必要な前処理は分かったか。
- 論文中の数式とコード上の処理が対応しているか。
- 公式コードは確認したか。
- requirementsは確認したか。
- 現在のライブラリバージョンで動くか。
- 最小実装の範囲を決めたか。
- テスト方法を決めたか。
