# 外部論文コード・GitHubリポジトリの扱い

## 基本方針

外部コードは、自分のプロジェクトに丸ごと混ぜない。

まずGitHub上またはGitHub MCPで確認し、使えそうな場合のみ参照用にcloneする。

## 手順

1. READMEを確認する。
2. licenseを確認する。
3. requirements / environment.yml を確認する。
4. 主要ファイルを確認する。
5. データセットと学習済みモデルの場所を確認する。
6. issueでよく出ているエラーを確認する。
7. 使えそうか判断する。
8. 深く読む・検索する・実行する必要がある場合だけ `external_repos/` にcloneする。

## cloneする場合

基本は浅いcloneを使う。

```bash
git clone --depth 1 https://github.com/OWNER/REPO.git external_repos/REPO
```

巨大リポジトリの場合は、必要に応じて sparse-checkout を検討する。

## 禁止事項

- 外部リポジトリ本体を自分のGitHubにpushしない。
- ライセンス不明のコードをコピーしない。
- 大容量データを勝手にダウンロードしない。
- APIキーや認証情報を含むファイルをcommitしない。

## 記録すること

`docs/research/` に以下を記録する。

- 論文名
- GitHub URL
- cloneした場合のcommit hash
- license
- 参照したファイル
- 参考にした処理
- 自分の実装に取り込む考え方
- コピーではなく再実装した範囲
