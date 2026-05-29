using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class VRProjectSetup
{
    [MenuItem("VR/Configure Project")]
    public static void ConfigureVR()
    {
        Debug.Log("=========================================");
        Debug.Log("Starting VR Project Configuration...");
        Debug.Log("=========================================");

        // 1. Switch active build target to Android
        BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
        if (activeTarget != BuildTarget.Android)
        {
            Debug.Log("Switching build target to Android...");
            bool success = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            if (!success)
            {
                Debug.LogError("Failed to switch build target to Android!");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("Successfully switched build target to Android.");
        }
        else
        {
            Debug.Log("Build target is already Android.");
        }

        // 2. Assign OpenXR Loader for Standalone (PC VR Link) and Android (Standalone Quest)
        // We use reflection for safety against missing packages, though they should be present.
        try
        {
            Type metadataStoreType = Type.GetType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore, Unity.XR.Management.Editor");
            if (metadataStoreType == null)
            {
                Debug.LogError("Could not find XRPackageMetadataStore class. Is XR Plug-in Management installed?");
                EditorApplication.Exit(1);
                return;
            }

            MethodInfo assignLoaderMethod = metadataStoreType.GetMethod("AssignLoader", BindingFlags.Public | BindingFlags.Static);
            if (assignLoaderMethod == null)
            {
                Debug.LogError("Could not find AssignLoader method on XRPackageMetadataStore.");
                EditorApplication.Exit(1);
                return;
            }

            Type generalSettingsType = Type.GetType("UnityEngine.XR.Management.XRGeneralSettings, Unity.XR.Management");
            if (generalSettingsType == null)
            {
                Debug.LogError("Could not find XRGeneralSettings class.");
                EditorApplication.Exit(1);
                return;
            }

            MethodInfo getSettingsMethod = generalSettingsType.GetMethod("GetSettingsForBuildTarget", BindingFlags.Public | BindingFlags.Static);
            if (getSettingsMethod == null)
            {
                Debug.LogError("Could not find GetSettingsForBuildTarget method.");
                EditorApplication.Exit(1);
                return;
            }

            string loaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";

            // Process Standalone
            object standaloneSettings = getSettingsMethod.Invoke(null, new object[] { BuildTargetGroup.Standalone });
            if (standaloneSettings != null)
            {
                PropertyInfo managerProp = generalSettingsType.GetProperty("Manager");
                object manager = managerProp.GetValue(standaloneSettings);
                bool assigned = (bool)assignLoaderMethod.Invoke(null, new object[] { manager, loaderTypeName, BuildTargetGroup.Standalone });
                Debug.Log($"Standalone OpenXR Loader Assignment: {assigned}");
            }
            else
            {
                Debug.LogWarning("Standalone settings not found.");
            }

            // Process Android
            object androidSettings = getSettingsMethod.Invoke(null, new object[] { BuildTargetGroup.Android });
            if (androidSettings != null)
            {
                PropertyInfo managerProp = generalSettingsType.GetProperty("Manager");
                object manager = managerProp.GetValue(androidSettings);
                bool assigned = (bool)assignLoaderMethod.Invoke(null, new object[] { manager, loaderTypeName, BuildTargetGroup.Android });
                Debug.Log($"Android OpenXR Loader Assignment: {assigned}");
            }
            else
            {
                Debug.LogWarning("Android settings not found. Attempting to initialize XR Settings...");
                // Triggering Creation of XR Settings if missing
                Type xrSettingsCreatorType = Type.GetType("UnityEditor.XR.Management.XRSettingsManager, Unity.XR.Management.Editor");
                if (xrSettingsCreatorType != null)
                {
                    // Access settings creation or settings instance
                    Debug.Log("XRSettingsManager found, please open Project Settings in Unity to fully initialize if loader assignment failed.");
                }
            }

            // 3. Enable Meta Quest Feature on Android via OpenXR Settings
            try
            {
                Type featureHelpersType = Type.GetType("UnityEditor.XR.OpenXR.Features.FeatureHelpers, Unity.XR.OpenXR.Editor");
                if (featureHelpersType != null)
                {
                    // Enable Meta Quest Feature
                    // Under Unity 6 / OpenXR, Meta Quest support might be in Unity.XR.OpenXR.Features.MetaQuestSupport or similar.
                    // We search for a feature subclass matching "MetaQuest" in the assembly.
                    Assembly editorAssembly = Assembly.Load("Unity.XR.OpenXR.Editor");
                    Assembly runtimeAssembly = Assembly.Load("Unity.XR.OpenXR");
                    
                    Type metaQuestFeatureType = runtimeAssembly.GetType("UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature");
                    if (metaQuestFeatureType != null)
                    {
                        MethodInfo getFeatureMethod = featureHelpersType.GetMethod("GetFeatureWithId", new Type[] { typeof(BuildTargetGroup) });
                        // But GetFeatureWithId is generic. Let's find GetFeatureWithId<T>(BuildTargetGroup group) or just search through features list.
                        // OpenXRSettings.Instance.Features contains all features.
                        Type openXRSettingsType = runtimeAssembly.GetType("UnityEngine.XR.OpenXR.OpenXRSettings");
                        if (openXRSettingsType != null)
                        {
                            MethodInfo getSettingsForTarget = openXRSettingsType.GetMethod("GetSettingsForBuildTarget", BindingFlags.Public | BindingFlags.Static);
                            object openXROngoingSettings = getSettingsForTarget.Invoke(null, new object[] { BuildTargetGroup.Android });
                            if (openXROngoingSettings != null)
                            {
                                MethodInfo getFeaturesMethod = openXROngoingSettings.GetType().GetMethod("GetFeatures", BindingFlags.Public | BindingFlags.Instance);
                                if (getFeaturesMethod != null)
                                {
                                    Array featuresArray = (Array)getFeaturesMethod.Invoke(openXROngoingSettings, null);
                                    foreach (object feature in featuresArray)
                                    {
                                        if (feature != null && feature.GetType().Name == "MetaQuestFeature")
                                        {
                                            PropertyInfo enabledProp = feature.GetType().GetProperty("enabled");
                                            enabledProp.SetValue(feature, true);
                                            Debug.Log("Successfully enabled Meta Quest Feature in OpenXR.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to configure specific OpenXR Features: {ex.Message}");
            }

        }
        catch (Exception ex)
        {
            Debug.LogError($"Error in VR configuration: {ex}");
            EditorApplication.Exit(1);
            return;
        }

        // Save project changes
        AssetDatabase.SaveAssets();
        Debug.Log("=========================================");
        Debug.Log("VR Project Configuration Completed!");
        Debug.Log("=========================================");
    }
}
