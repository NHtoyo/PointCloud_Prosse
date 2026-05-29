using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class SceneSetupHelper
{
    [MenuItem("Tools/Setup Point Cloud Scene")]
    public static void SetupScene()
    {
        // 1. Open scene
        var scene = EditorSceneManager.OpenScene("Assets/VRTestScene.unity");

        // 2. Find PointCloudDemo
        GameObject pointCloudGo = GameObject.Find("PointCloudDemo");
        if (pointCloudGo == null)
        {
            pointCloudGo = GameObject.Find("ReferenceCloud");
        }

        if (pointCloudGo != null)
        {
            var renderer = pointCloudGo.GetComponent<PointCloudRenderer>();
            var loader = pointCloudGo.GetComponent<PointCloudLoader>();

            // Ensure PointCloudEditor is attached
            var editor = pointCloudGo.GetComponent<PointCloudEditor>();
            if (editor == null)
            {
                editor = pointCloudGo.AddComponent<PointCloudEditor>();
                Debug.Log("Attached PointCloudEditor to PointCloudDemo.");
            }
            editor.targetRenderer = renderer;

            // Ensure loader target is set
            if (loader != null)
            {
                loader.targetRenderer = renderer;
            }
        }
        else
        {
            Debug.LogError("PointCloudDemo GameObject not found in scene!");
        }

        // 3. Find Main Camera and attach CloudCompareCameraController
        GameObject mainCameraGo = GameObject.Find("Main Camera");
        if (mainCameraGo == null)
        {
            Camera cam = Camera.main;
            if (cam != null) mainCameraGo = cam.gameObject;
        }

        if (mainCameraGo != null)
        {
            var camCtrl = mainCameraGo.GetComponent<CloudCompareCameraController>();
            if (camCtrl == null)
            {
                camCtrl = mainCameraGo.AddComponent<CloudCompareCameraController>();
                Debug.Log("Attached CloudCompareCameraController to Main Camera.");
            }
            
            // Set starting values
            camCtrl.pivotPoint = new Vector3(0, 1, 0);
            camCtrl.distanceToPivot = 3f;
        }
        else
        {
            Debug.LogError("Main Camera GameObject not found in scene!");
        }

        // 4. Create PointCloudManager if needed to coordinate UI
        GameObject managerGo = GameObject.Find("PointCloudManager");
        if (managerGo == null)
        {
            managerGo = new GameObject("PointCloudManager");
            var manager = managerGo.AddComponent<PointCloudManager>();
            if (pointCloudGo != null)
            {
                manager.referenceCloud = pointCloudGo.GetComponent<PointCloudRenderer>();
            }
            Debug.Log("Created PointCloudManager.");
        }

        // 5. Remove Floor GameObject
        GameObject floorGo = GameObject.Find("Floor");
        if (floorGo != null)
        {
            Object.DestroyImmediate(floorGo);
            Debug.Log("Removed Floor GameObject as requested.");
        }

        // 6. Save scene
        EditorSceneManager.SaveScene(scene);
        Debug.Log("Scene setup completed and saved!");
    }
}
