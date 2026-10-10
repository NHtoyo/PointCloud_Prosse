# Windows Player終了時クラッシュ調査

調査日: 2026-10-10 (JST)
対象: Unity 6000.4.7f1 / 第4次検証で記録されたWindows Player終了時異常
結論: **根本原因は未特定。旧クラッシュは新しい隔離環境で再現せず、製品コードは変更していない。**

## 結論

旧証拠では、終了コード`-1073741819` (`0xC0000005`)と`-1073740771` (`0xC000041D`)は別の異常で、同じプロセスで前者の後に後者が出たケースもある。意図的な強制終了やrunner timeoutの`-1`とは区別できる。

既存PDBで解決できた`0xC0000005`の一部はUnityネイティブ終了処理中のstatic破棄、別の障害箇所はPlayerウィンドウメッセージ処理に位置する。障害命令・呼び出し位置は絞れたが、その状態を作った先行操作や所有者までは分からず、Unity本体、先行するメモリ破壊、プラグイン、アプリ側ライフサイクルのいずれかを根本原因と断定できない。

新規QAルートでは、最新ローカル作業ツリーから作ったprobeなしPlayerで空シーン5/5、合成10万点読込・Octree準備後の通常ウィンドウ終了10/10が終了コード0だった。probeを使った同等の終了、編集・解析・復旧後も正常終了した。したがって、今回の試験では旧障害は再現せず、修正対象を安全に特定できなかった。

## 既存証拠とプロセス別対応

読み取り元は`E:\pcwb-qa-20261009\FourthAuditFinal7\Artifacts`、Application Error / WER、`%LOCALAPPDATA%\CrashDumps`。旧QAルートとログ・ダンプは変更していない。PIDは16進イベント値と10進ダンプ名の対応を示す。

| 旧実行 | PID (hex / dec) | 例外・場所・JST | 実行結果 / 根拠 |
|---|---:|---|---|
| `player_workflow` | `0x8DE4` / 36324 | 00:02:50 `0xC0000005`、00:02:52 `0xC000041D`。両方`UnityPlayer.dll+0x11C0538` | Runnerの終了コードは`-1073740771`。機能statusはPASS、終了はFAIL。`player_workflow.log`、`fourth-player-20261009-150208-f5b8eb08.tsv` |
| `player_load_race` | `0x87D8` / 34776 | 00:03:12 `0xC0000005`、`UnityPlayer.dll+0x107AE1` | 終了コード`-1073741819`。機能statusはPASS、終了はFAIL。`player_load_race.log`、`fourth-player-20261009-150258-1ee23fdf.tsv` |
| `player_load_tiers` | `0x8D58` / 36184 | 00:03:24 `0xC0000005`、`UnityPlayer.dll+0x107AE1` | 終了コード`-1073741819`。機能statusはPASS、終了はFAIL。`player_load_tiers.log`、`fourth-player-20261009-150318-1477383c.tsv` |
| 復旧writer | 14284 | Probeがcheckpoint書込後に`Process.GetCurrentProcess().Kill()`を実行 | 意図した異常終了。結果`-1`はNT例外コードではない。対応するApplication Error / dumpなし。分類A |
| `crash_recovery` verify | `0x5EF4` / 24308 | 00:03:40 `0xC0000005`、00:03:41 `0xC000041D`。両方`UnityPlayer.dll+0x11C0538` | 終了コード`-1073740771`。復旧後チェックの初回集計はrunner不具合でFAIL扱い。statusを個別に再評価した第4次報告では復旧・継続操作は成功、終了はFAIL |
| 可視Player UI | `0x9A2C` / 39468 | 00:08:29 `0xC0000005`、`UnityPlayer.dll+0x107AE1` | WER / dumpあり。`player_process_results.json`に対応行がなく、runnerによる終了コードは未記録。`visible_player_ui.log`は通常シーン起動後のUI操作記録。終了操作との厳密な対応は未確定 |

各終了コードは以下の通り。`0xC0000005`はアクセス違反、`0xC000041D`は別のWindows例外statusであり、同一扱いしない。PID 36324と24308では、Application Error 1000に前者・後者の両方が連続記録され、runnerが返したコードは後者だった。時系列だけから、どちらが先行原因かは決められない。

対応するWER dumpは`PointCloudVR_QA.exe.36324.dmp`、`(1).36324.dmp`、`.34776.dmp`、`.36184.dmp`、`.24308.dmp`、`(1).24308.dmp`、`.39468.dmp`。別に23:56頃のPID 37756 / 38284 / 1464のdumpもあるが、Final7の各PlayerRunsとのPID・パス対応を確定できないため、同じ実行へ帰属させていない。Final7の対応dumpでは例外thread IDとexception recordを確認し、PID 36324・24308は各2 dumpで異なる例外記録を持つ。

### dump / symbolの読み取り

- PDB `UnityPlayer_Win64_player_mono_x64.pdb`で`UnityPlayer.dll+0x107AE1`を`StaticDestroy<RuntimeStatic<PlatformAccessibilityManager>>+0x51`と解決。ダンプの命令は`mov rcx, qword ptr [rcx+0xe0]`、`RCX=0`で、null readと整合する。周辺stackには`RuntimeCleanup`、`DestroyGfxDevice`、`UnloadMono`、`UnityMainImpl`があり、Unityの終了・解放経路内である。
- `UnityPlayer.dll+0x11C0538`は`PlayerMainWndProc+0x8b8`。stackにUSER32の`CallWindowProcW`、`GetFocus`、`PeekMessageW`がある。これも発生位置の特定であって、ウィンドウ処理が根本原因である証明ではない。
- 追加導入済みデバッガーによる完全な解析はできていない。既存PDBとdumpから得た範囲を超えて、staticの所有状態や先行メモリ破壊を推定しない。

## A〜Eの切り分け

| 分類 | 判定 | 根拠 |
|---|---|---|
| A 意図的な強制終了 | **該当あり** | 復旧writer probeがcheckpoint後に自プロセスをkill。`-1`、crash event/dumpなし。 |
| B runner timeout / kill | **該当あり** | 今回の探索中、hidden Playerの`-quit`試行は60.2秒、probe smokeは120.1秒、load-race probeは180.2秒でtimeoutとなりrunnerが終了。結果は`-1`。該当Application Error / dumpはなく、製品のaccess violationとして数えない。非表示・非フォーカス時の実行停止またはprobe待ちとの切り分けは未了。 |
| C Playerの予期しないクラッシュ | **旧実行で確認** | 上表の5 PlayerプロセスでApplication Error 1000 / WER 1001および対応dumpあり。workflow等はprobe完了後もrunnerの通常終了待ちで、第四次runnerの通常実行ループに強制kill / timeout経路はない。 |
| D test probeの不具合 | **根本原因の証拠なし** | probeありの正常終了も複数回確認。timeoutはprobe/harnessの待ち方に問題がある可能性を残すが、native crashの説明にはならない。旧可視UI実行はprobe flagなしに見える一方、当時のPlayer自体はprobe入りビルドの可能性を排除できない。今回のprobe完全除外Playerでは再現しなかった。 |
| E Unity / native plugin / driver | **未確定** | Faulting moduleは`UnityPlayer.dll`。該当命令はUnityの終了処理・WndProc内だが、Unity内部バグ、先行破壊、plugin / driverとの相互作用の証拠は不足。 |

FourthAuditの`run_summary.json`は、当初復旧継続をFAILと記録している。第4次報告ではrunnerがstatus項目の順番を誤認したと説明し、同じstatusの独立再評価で復旧後の操作をPASSと訂正している。これは機能statusの集計問題であり、Player終了時の`0xC000041D`とは別件。

## 最新作業ツリーでの隔離再現

新規QAルート: `E:\pcwb-qa-20261010\PlayerExitTask1-20261010-010353`。元作業ツリーから隔離コピーし、synthetic PLYのみ使用。probeあり/なしを分離し、通常終了は`Process.CloseMainWindow()`によるwindow-close要求か、probeからの`Application.Quit()`かを記録した。強制killは復旧writerだけ。

| ケース | 回数 / 結果 | 終了経路・確認 |
|---|---:|---|
| 空シーン・runtime probeを含まないPlayer | 5/5 exit 0 | 可視windowを確認後、`CloseMainWindow()`。`empty_noprobe_close_results.json` |
| 通常Main scene・probeなし・synthetic PLY 100,000点 | 10/10 exit 0 | 点群読込とOctree readyを確認後、可視windowを`CloseMainWindow()`。`normal_noprobe_cloud_close_results.json` |
| 空シーン・exit probeあり | 10/10 exit 0 | probe ready後に`Application.Quit()`。`empty_probe_visible_results.json` |
| Main scene・100,000点・renderer有効・Octree ready・probeあり | 10/10 exit 0 | 状態確認後に`Application.Quit()`。`main_cloud_quit_visible_results.json` |
| Octree後にRendererをdisableして終了 | 3/3 exit 0 | Rendererの`OnDisable`経路を含む。`renderer_disable_visible_results.json` |
| 実操作相当のworkflow probe | 1/1 exit 0 | 分類、削除/復元、Undo/Redo、保存/再読込、sphere、downsample、C2C、StemDiameter、cancel/retry、反復編集・保存を実行。37.559秒。`workflow_full_visible_01_result.json` |
| Recovery writer / verify | writerは意図的kill、verify 1/1 exit 0 | 新規QAのwriter PID 36024が混在ラベルcheckpointを作成後に終了。再起動後に復旧適用、編集/保存/切替を確認。`recovery_write_result.json`、`recovery_verify_result.json` |
| OpenXR loaderをQAコピーで除外したPlayer | 5/5 exit 0 | synthetic 100,000点・Octree ready後にprobe終了。起動ログにOpenXR初期化なし。`noxr_cloud_quit_results.json` |

試験ログ・TSV・JSONは新規QAの`Artifacts`以下に保存した。新規QA実行時間帯（01:00–02:00 JST）のApplication Error 1000は0件、当該Playerに対応する新規WER dumpも0件。空sceneのhidden `-quit`試行(60.2秒)、probe smokeの2回目(120.1秒)、hidden load-race(180.2秒)はrunner timeoutで強制終了された。これらはexit code `-1`のtimeoutとして分け、通常終了10/10などの分母には混ぜていない。

### OpenXR / graphics APIについて

第4次以前の隔離`NoXR2`ではloaderを無効にし、`m_InitManagerOnStart=0`としたPlayerでも、Octree ready後に`UnityPlayer.dll+0x107AE1`の`0xC0000005`が複数回記録されている。別ログにはD3D11の明示記録もあり同じ障害位置だった。よってOpenXRやD3D12は**必要条件ではない**。今回のOpenXR無効試験5/5は再現しなかったが、条件付き競合で無関係と証明するものでもない。graphics APIは今回多条件比較していない。

## 終了処理のコード確認

本番ソースを読み、終了処理の候補は確認したが、原因と結びつく再現は得ていない。以下は仮説候補であり、修正根拠ではない。

| コンポーネント | 読み取った終了経路 | 未解決リスク / 今回の証拠 |
|---|---|---|
| `PointCloudRenderer` | `OnDisable` / `OnDestroy`でOctree buildをcancelしComputeBufferをrelease。worker Taskはjoin/awaitしない | 遅延workerとUnity object破棄の競合は検査対象。ただしRenderer disable 3/3、Octree ready後のprobeなし終了10/10で未再現 |
| `PointCloudLoader` | `async void LoadPointCloud`、PLY解析を`Task.Run`し、`OnDestroy`はoperationをcancelするが完了を待たない | shutdown中のparser完了競合は未注入。読込完了後の通常終了では未再現 |
| `PointCloudManager` | `OnDestroy`で比較処理をcancelしtextureを破棄 | 処理中の終了競合は未検証 |
| `StemDiameterUI` / `PythonBridge` | `StemDiameterUI.OnDestroy`から`StopProcessAsync()`をfire-and-forget。Python kill/wait・stream callbackあり | Python解析完了後のworkflow終了は1/1。Python子プロセス稼働中のPlayer終了をこの新規QAで独立反復していない。OnDestroy時の非同期終了順は未検証 |
| 全体 | 製品コード内に`OnApplicationQuit`は見つからず。QA probeは通常試験で`Application.Quit`、recovery writerだけ明示kill | Player標準終了とprobeの強制終了を分離済み |

Unity APIをbackground callbackから呼んでいないか、GPU処理中のbuffer破棄、static event/delegate解除順、Python stdout/stderr callbackとUnity終了の競合は、さらに実行時の遅延注入が必要。コード上の候補だけで根本原因を決めつけない。

## バイナリ・ソース識別

SHA-256の全一覧は`E:\pcwb-qa-20261010\PlayerExitTask1-20261010-010353\Artifacts\player_exit_task1_hashes.tsv`、probeなしQAプロジェクトのソース/設定等246ファイルのmanifestは`no_probe_source_snapshot_sha256.tsv`に保存した。manifestファイル自体のSHA-256は`79AD31A1DE7574863A2B653A23E7D6C8BA2E544DF094A968377DEF90E62D60E7`。

| 対象 | EXE SHA-256 | `UnityPlayer.dll` SHA-256 | `Assembly-CSharp.dll` SHA-256 |
|---|---|---|---|
| 第4次 Final7 異常Player | `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7` | `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B` | `4D93F1F608FC9E47586737024497C7BC274098B9C5E324AE2B9D7BCBF7FAAB26` |
| 今回 clean no-probe Player | `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7` | `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B` | `86112055A3C9693A7C8007657B97FB392E9873298D501E014C6F6CD774A9D0C8` |

EXEとUnityPlayer.dllは一致し、C#アセンブリは異なる。従って、旧/新の差は「Unity native DLLが違う」ためではないが、C#コード差が結果に影響した可能性は残る。これはUnity単独原因の証明ではない。Unity PDB SHA-256は`88F5A13EC77743DD64D630FAE8C80CA2FA55D88CF2BEBE6B2C95D7AC51E3F894`。Unityログのversion文字列は`6000.4.7.50116`（Editor表記`6000.4.7f1`）。

## 原因の確信度と次の検証

- **高確度:** 旧Final7の複数プロセスが終了時にUnityPlayer.dll内で予期せず落ちたこと、0xC0000005と0xC000041Dが別コードであること、`0x107AE1`がnull readを伴うUnity cleanup関数に解決されること。
- **中確度:** 障害はアプリの終了・WndProc周辺に顕在化している。ダンプ位置は再現性があるが、直接原因と先行操作は不明。
- **低/未確定:** Unity native bug、特定の製品C#処理、OpenXR/GPU driver、非同期worker、probeのいずれが根本原因か。現状は帰属できない。

次段階で必要なこと:

1. 旧Final7と同じC#アセンブリ・設定・probe操作列を再構成し、同じ終了要求で再現試験する。今回の新ソースでのpassを旧ビルドの修正証明にしない。
2. 最初にクラッシュする境界を見つけたら、同条件を独立起動10回以上で実行し、timeout/kill/normal exit/access violationを別々に記録する。
3. Octree Task cancel直後、PLY parse中、GPU buffer更新中、Python子process稼働中、`OnDestroy`後callback、recovery適用直後に遅延イベントを注入する。
4. 取得済dumpをネイティブデバッガで完全解析できる環境が利用可能になった場合のみ、例外thread全stackとstatic状態を追加確認する。WERレジストリ設定変更や有償ツール導入は行っていない。以前のWinDbg導入試行はpackage deploy error `0x80073cff`で止め、システム設定は変更していない。

## 作業範囲・保護

- 今回更新したリポジトリ内ファイル: 本調査記録のみ。製品C#、Python、Unity本番設定・sceneは変更していない。
- 既存未コミット変更、実測PLY、ライセンス資料、`Assets/_Recovery/`、既存QA証拠は変更していない。新規試験は別QAルートとsynthetic dataのみ。
- Unity Editorは終了していない。調査後もEditor PID 30824、21068、15860の稼働を確認した。
- commit / push / stageはしていない。
