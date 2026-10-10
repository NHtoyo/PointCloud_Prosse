using UnityEngine;
using PointCloudWorkbench;

internal static class PointCloudRecoverySession
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Begin()
    {
        try
        {
            bool recoveredAfterUncleanExit = PointCloudSessionRecoveryStore.BeginSession(Application.persistentDataPath);
            if (recoveredAfterUncleanExit)
                Debug.LogWarning("[Recovery] 前回は正常終了を確認できませんでした。元PLYと点数が一致する編集スナップショットだけを復元対象にします。");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Recovery] セッション復旧を初期化できません。点群の通常利用は続行します。\n{ex}");
        }
        Application.quitting -= End;
        Application.quitting += End;
    }

    private static void End()
    {
        try
        {
            if (!PointCloudSessionRecoveryStore.EndSession(Application.persistentDataPath))
                Debug.LogWarning("[Recovery] 正常終了markerを削除できませんでした。次回起動時に復旧確認が行われます。");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Recovery] 正常終了markerの削除に失敗しました。次回起動時に復旧確認が行われます。\n{ex}");
        }
    }
}
