using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;

public class SceneDumper
{
    [MenuItem("Tools/Dump Scene")]
    public static void Dump()
    {
        EditorSceneManager.OpenScene("Assets/VRTestScene.unity");
        
        string logPath = "E:/VR/scene_dump.txt";
        using (StreamWriter writer = new StreamWriter(logPath))
        {
            writer.WriteLine("--- Scene Dump ---");
            GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            writer.WriteLine($"Total GameObjects: {allObjects.Length}");
            
            foreach (var go in allObjects)
            {
                if (go.transform.parent != null) continue; // Root objects only for recursive search
                DumpObject(go, writer, 0);
            }
        }
        Debug.Log("Scene dump completed!");
    }

    private static void DumpObject(GameObject go, StreamWriter writer, int indent)
    {
        string prefix = new string(' ', indent * 2);
        writer.WriteLine($"{prefix}GameObject: {go.name} (Active: {go.activeSelf})");
        
        Component[] components = go.GetComponents<Component>();
        foreach (var c in components)
        {
            if (c == null)
            {
                writer.WriteLine($"{prefix}  - Component: Missing/Null Component!");
                continue;
            }
            writer.WriteLine($"{prefix}  - Component: {c.GetType().Name} (Enabled: {(c is Behaviour ? ((Behaviour)c).enabled.ToString() : "N/A")})");
            
            if (c is PointCloudLoader loader)
            {
                writer.WriteLine($"{prefix}    * targetRenderer: {(loader.targetRenderer != null ? loader.targetRenderer.name : "null")}");
                writer.WriteLine($"{prefix}    * fileName: {loader.fileName}");
            }
            if (c is PointCloudEditor editor)
            {
                writer.WriteLine($"{prefix}    * targetRenderer: {(editor.targetRenderer != null ? editor.targetRenderer.name : "null")}");
                writer.WriteLine($"{prefix}    * activeTool: {editor.activeTool}");
            }
            if (c is PointCloudManager manager)
            {
                writer.WriteLine($"{prefix}    * referenceCloud: {(manager.referenceCloud != null ? manager.referenceCloud.name : "null")}");
                writer.WriteLine($"{prefix}    * alignedCloud: {(manager.alignedCloud != null ? manager.alignedCloud.name : "null")}");
            }
            if (c is CloudCompareCameraController camCtrl)
            {
                writer.WriteLine($"{prefix}    * pivotPoint: {camCtrl.pivotPoint}");
                writer.WriteLine($"{prefix}    * distanceToPivot: {camCtrl.distanceToPivot}");
            }
        }

        for (int i = 0; i < go.transform.childCount; i++)
        {
            DumpObject(go.transform.GetChild(i).gameObject, writer, indent + 1);
        }
    }
}
