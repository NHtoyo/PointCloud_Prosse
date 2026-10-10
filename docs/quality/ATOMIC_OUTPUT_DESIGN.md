# 解析結果の世代保存設計

## 目的

ひとまとまりの結果が複数ファイル（JSON/CSV/画像/metadata/report）にまたがるとき、途中失敗で異なるrunを混ぜない。Windows上で複数ファイルrenameが一括原子的とは仮定しない。

## publication protocol

1. 呼出元が一意のoperation/run IDを作る。
2. 出力rootのruns配下に新しいgeneration staging directoryを作る。既存generationは変更しない。
3. 各artifactをstagingへ書き、閉じる。artifact名、byte size、SHA-256を集める。
4. manifestをstatus=completeとして最後に書く。
5. staging directoryを一意run directoryへrenameする。
6. current_run.jsonを同じrootの一時ファイルへ書き、flush後にreplace/moveする。この単一pointerが公開commit point。
7. 読込側はpointer、manifest hash/status/run ID、各artifact size/hashを検証してから結果を使用する。
8. pointer更新前に失敗すれば旧pointerは有効なまま。新generationは診断用orphanとして残り得る。自動cleanupは正常世代を消さない。

## 実装範囲

- Python helper: python_backend/output_generations.py。
- noise results: result_writer.py / run_noise_filter.py。
- stem results: stem_diameter_output.py / run_stem_diameter.py。failure JSONは別run IDのerrors領域へ保存。
- Unity reader: OutputGenerationStore.cs validates pointer, manifest and artifact hashes.
- Downsampled PLYは小さな単一file出力なのでstaging PLYを検証してからnew GUID final pathへmove。計測sidecar失敗時はPLYを保持してwarning。

## 検証結果

Python testsは最初/中間artifact、metadata、removal report、CSV、pointer replace失敗、concurrent isolated writerを注入し、旧pointer/旧artifactを検証する。Windowsでr+b fsyncを通す回帰を含む。58 tests全pass。

## 境界・リスク

- Rename/pointer replaceの原子性は同一volumeとOS/filesystem挙動に依存する。電源断、OS crash、アンチウイルス/file lock、disk full、ACL failureを実OSで注入していない。
- directory rename後・pointer更新前のgenerationはorphanとなり得る。容量整理ポリシーは別途必要。
- readerが全artifactをhashするため大きな成果物では読込I/O/時間が増える。測定していない。
- Python writer concurrent testは協調したtemporary generationを検証するが、異常なprocess killを含まない。
