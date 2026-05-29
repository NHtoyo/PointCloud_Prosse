using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;

public static class CreateVRTestScene
{
    [MenuItem("VR/Create VR Test Scene")]
    public static void CreateScene()
    {
        // 1. Create a new scene
        Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // 2. Remove default Main Camera (to avoid conflict with XR Origin)
        GameObject mainCam = GameObject.Find("Main Camera");
        if (mainCam != null)
        {
            Object.DestroyImmediate(mainCam);
        }

        // 3. Create a Plane for floor
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.position = Vector3.zero;
        floor.transform.localScale = new Vector3(2f, 1f, 2f);

        // Create Point Cloud Demo GameObject
        GameObject pointCloudGo = new GameObject("PointCloudDemo");
        pointCloudGo.transform.position = new Vector3(0f, 0f, 0f); // Center of the world
        var renderer = pointCloudGo.AddComponent<PointCloudRenderer>();
        var loader = pointCloudGo.AddComponent<PointCloudLoader>();
        loader.targetRenderer = renderer;

        var editor = pointCloudGo.AddComponent<PointCloudEditor>();
        editor.targetRenderer = renderer;
        
        // Add Grab and Controller components
        pointCloudGo.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
        pointCloudGo.AddComponent<PointCloudController>();

        // 4. Try to create XR Origin using menu item
        bool created = false;
        string[] menuItems = {
            "GameObject/XR/XR Origin (VR)",
            "GameObject/XR/XR Origin (XR Rig)",
            "GameObject/XR/XR Origin",
            "GameObject/XR/Device-based/XR Origin"
        };

        foreach (var item in menuItems)
        {
            if (EditorApplication.ExecuteMenuItem(item))
            {
                Debug.Log($"Successfully created XR Origin using menu item: {item}");
                created = true;
                break;
            }
        }

        if (!created)
        {
            Debug.LogWarning("Could not create XR Origin via menu items. Creating basic XR Origin structure manually...");
            // Manual fallback
            GameObject xrOriginGo = new GameObject("XR Origin");
            XROrigin xrOrigin = xrOriginGo.AddComponent<XROrigin>();
            
            GameObject cameraOffsetGo = new GameObject("Camera Offset");
            cameraOffsetGo.transform.SetParent(xrOriginGo.transform);
            
            GameObject cameraGo = new GameObject("Main Camera");
            cameraGo.transform.SetParent(cameraOffsetGo.transform);
            Camera camera = cameraGo.AddComponent<Camera>();
            cameraGo.AddComponent<AudioListener>();
            
            System.Type tpdType = System.Type.GetType("UnityEngine.InputSystem.XR.TrackedPoseDriver, Unity.InputSystem");
            if (tpdType != null)
            {
                cameraGo.AddComponent(tpdType);
            }

            xrOrigin.CameraFloorOffsetObject = cameraOffsetGo;
            xrOrigin.Camera = camera;
        }

        // Position XR Origin slightly backward so the user spawns looking at the point cloud center
        GameObject xrOriginObj = GameObject.Find("XR Origin");
        if (xrOriginObj == null)
        {
            // Try fallback name if manual creation happened
            xrOriginObj = GameObject.Find("XR Origin");
        }
        
        if (xrOriginObj != null)
        {
            xrOriginObj.transform.position = new Vector3(0f, 0f, -2f); // Move 2 meters back
        }

        // 5. Save the scene
        EditorSceneManager.SaveScene(newScene, "Assets/VRTestScene.unity");
        Debug.Log("VR Test Scene with Point Cloud Demo created and saved to Assets/VRTestScene.unity");
    }
}
