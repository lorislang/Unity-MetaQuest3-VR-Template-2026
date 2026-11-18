// Setup checklist for the VR template.
// Opens automatically when the project is loaded and something is still missing
// (wrong editor, no Android module, Meta SDK not installed, ...).
// Can always be reopened via the menu: VR Template > Setup Checklist.

using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrTemplate.Editor
{
    public class TemplateSetupWindow : EditorWindow
    {
        // ---------------------------------------------------------------
        // Change these when you move the template to another editor/SDK.
        // ---------------------------------------------------------------
        const string RecommendedEditor = "6000.0.66f2";
        const string RecommendedEditorChangeset = "b20bc5da3050";
        const string MinimumEditorMajor = "6000";

        const string MetaCorePackage = "com.meta.xr.sdk.core";
        const string MetaInteractionPackage = "com.meta.xr.sdk.interaction.ovr";

        const string StarterScenePath = "Assets/_Project/Scenes/Main.unity";
        const AndroidSdkVersions QuestMinSdk = (AndroidSdkVersions)32;

        static string HidePrefKey => "VrTemplate.HideSetup." + Application.dataPath.GetHashCode();
        const string ShownThisSessionKey = "VrTemplate.SetupShownThisSession";

        AddAndRemoveRequest _installRequest;
        Vector2 _scroll;

        // ---------------------------------------------------------------
        // Auto-open on project load
        // ---------------------------------------------------------------
        [InitializeOnLoadMethod]
        static void AutoOpen()
        {
            if (Application.isBatchMode) return;
            if (SessionState.GetBool(ShownThisSessionKey, false)) return;
            SessionState.SetBool(ShownThisSessionKey, true);

            EditorApplication.delayCall += () =>
            {
                // Always show while something is missing; once done, respect "don't show again".
                if (AllRequiredDone() && EditorPrefs.GetBool(HidePrefKey, false)) return;
                Open();
            };
        }

        [MenuItem("VR Template/Setup Checklist", priority = 0)]
        public static void Open()
        {
            var w = GetWindow<TemplateSetupWindow>(true, "VR Template – Setup", true);
            w.minSize = new Vector2(520, 560);
            w.Show();
        }

        // ---------------------------------------------------------------
        // Checks
        // ---------------------------------------------------------------
        static bool EditorIsExact => Application.unityVersion == RecommendedEditor;
        static bool EditorIsCompatible => Application.unityVersion.StartsWith(MinimumEditorMajor + ".");
        static bool AndroidModuleInstalled => BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
        static bool AndroidIsActive => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;
        static bool HasPackage(string name) =>
            UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(p => p.name == name);

        static bool QuestSettingsApplied =>
            PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP
            && PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64
            && PlayerSettings.Android.minSdkVersion >= QuestMinSdk
            && PlayerSettings.colorSpace == ColorSpace.Linear;

        static bool StarterSceneExists => File.Exists(StarterScenePath);

        static bool AllRequiredDone() =>
            EditorIsCompatible && AndroidModuleInstalled && AndroidIsActive
            && HasPackage(MetaCorePackage) && QuestSettingsApplied && StarterSceneExists;

        // ---------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------
        void OnEnable() => EditorApplication.update += PollInstall;
        void OnDisable() => EditorApplication.update -= PollInstall;

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Meta Quest VR Template", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox(
                "Work through the list from top to bottom. Every red item must be fixed before you can build to the Quest. " +
                "Yellow items are recommendations.", MessageType.Info);

            // 1 – Editor version
            Section("1. Unity Editor");
            if (EditorIsExact)
                Status(true, $"Unity {Application.unityVersion} (recommended version).");
            else if (EditorIsCompatible)
                Warn($"You are on Unity {Application.unityVersion}. The template was made with {RecommendedEditor}. " +
                     "It will probably work, but if something breaks, switch to the recommended version.");
            else
                Status(false, $"Unity {Application.unityVersion} is too old. Meta XR SDK needs Unity {MinimumEditorMajor}.x. " +
                              $"Install {RecommendedEditor} (with Android Build Support) and reopen the project with it.");
            if (!EditorIsExact && GUILayout.Button($"Install Unity {RecommendedEditor} via Unity Hub"))
                Application.OpenURL($"unityhub://{RecommendedEditor}/{RecommendedEditorChangeset}");

            // 2 – Android module
            Section("2. Android Build Support");
            Status(AndroidModuleInstalled, AndroidModuleInstalled
                ? "Android Build Support module is installed."
                : "Android Build Support is missing. In Unity Hub: Installs > ⚙ next to this editor > Add modules > " +
                  "tick 'Android Build Support' (incl. OpenJDK and Android SDK & NDK Tools). Then restart Unity.");

            // 3 – Build target
            Section("3. Build target = Android");
            Status(AndroidIsActive, AndroidIsActive ? "Active build target is Android." : "Active build target is not Android yet.");
            using (new EditorGUI.DisabledScope(AndroidIsActive || !AndroidModuleInstalled))
                if (GUILayout.Button("Switch to Android"))
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            // 4 – Meta SDK
            Section("4. Meta XR SDK");
            bool core = HasPackage(MetaCorePackage), interaction = HasPackage(MetaInteractionPackage);
            Status(core, core ? "Meta XR Core SDK is installed." : "Meta XR Core SDK is not installed (required).");
            if (interaction) Status(true, "Meta XR Interaction SDK is installed.");
            else Warn("Meta XR Interaction SDK is not installed (recommended for grabbing, poking, hands).");

            bool busy = _installRequest != null && !_installRequest.IsCompleted;
            using (new EditorGUI.DisabledScope(busy || (core && interaction)))
                if (GUILayout.Button(busy ? "Installing… (this can take a few minutes)" : "Install Meta XR Core + Interaction SDK"))
                    InstallMeta();

            using (new EditorGUI.DisabledScope(!core))
                if (GUILayout.Button("Open Meta Project Setup Tool  →  click 'Fix All'"))
                    OpenMetaSetupTool();
            EditorGUILayout.LabelField(
                "After installing, run the Meta Project Setup Tool for the Android tab and press 'Fix All' / 'Apply All' " +
                "until nothing red is left.", EditorStyles.wordWrappedMiniLabel);

            // 5 – Player settings
            Section("5. Quest player settings");
            Status(QuestSettingsApplied, QuestSettingsApplied
                ? "IL2CPP, ARM64, min. API level 32 and Linear color space are set."
                : "Quest needs IL2CPP, ARM64, min. API level 32 and Linear color space.");
            using (new EditorGUI.DisabledScope(QuestSettingsApplied))
                if (GUILayout.Button("Apply Quest player settings"))
                    ApplyQuestSettings();

            // 6 – Starter scene
            Section("6. Starter scene");
            Status(StarterSceneExists, StarterSceneExists
                ? $"Starter scene exists: {StarterScenePath}"
                : "No starter scene yet. Install the Meta XR Core SDK first, then create it.");
            using (new EditorGUI.DisabledScope(!core))
                if (GUILayout.Button(StarterSceneExists ? "Open starter scene" : "Create starter scene (camera rig, floor, light)"))
                {
                    if (StarterSceneExists) EditorSceneManager.OpenScene(StarterScenePath);
                    else CreateStarterScene();
                }

            // Footer
            EditorGUILayout.Space(12);
            if (AllRequiredDone())
                EditorGUILayout.HelpBox("All set! Connect your Quest, then File > Build Profiles (or Build Settings) > " +
                                        "select your headset under 'Run Device' > Build And Run.", MessageType.None);

            bool hide = EditorPrefs.GetBool(HidePrefKey, false);
            bool newHide = EditorGUILayout.ToggleLeft("Don't open automatically when everything is set up", hide);
            if (newHide != hide) EditorPrefs.SetBool(HidePrefKey, newHide);

            EditorGUILayout.EndScrollView();
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        static void Status(bool ok, string text) =>
            EditorGUILayout.HelpBox(text, ok ? MessageType.None : MessageType.Error);

        static void Warn(string text) => EditorGUILayout.HelpBox(text, MessageType.Warning);

        // ---------------------------------------------------------------
        // Actions
        // ---------------------------------------------------------------
        void InstallMeta()
        {
            // No versions given → Package Manager picks the newest version compatible with this editor.
            _installRequest = Client.AddAndRemove(new[] { MetaCorePackage, MetaInteractionPackage });
        }

        void PollInstall()
        {
            if (_installRequest == null || !_installRequest.IsCompleted) return;
            if (_installRequest.Status == StatusCode.Failure)
                EditorUtility.DisplayDialog("Meta SDK install failed",
                    _installRequest.Error?.message + "\n\nCheck your internet connection, or install the packages manually " +
                    "via Window > Package Manager > My Registries > Meta XR.", "OK");
            _installRequest = null;
            Repaint();
        }

        static void OpenMetaSetupTool()
        {
            // Menu path has changed between SDK versions – try the known ones.
            string[] paths = { "Meta/Tools/Project Setup Tool", "Oculus/Tools/Project Setup Tool" };
            if (paths.Any(EditorApplication.ExecuteMenuItem)) return;
            EditorUtility.DisplayDialog("Project Setup Tool",
                "Couldn't open it automatically. Open it via Edit > Project Settings > Meta XR.", "OK");
        }

        static void ApplyQuestSettings()
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            if (PlayerSettings.Android.minSdkVersion < QuestMinSdk)
                PlayerSettings.Android.minSdkVersion = QuestMinSdk;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            AssetDatabase.SaveAssets();
        }

        static void CreateStarterScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera rig from the Meta XR Core SDK
            string rigGuid = AssetDatabase.FindAssets("OVRCameraRig t:Prefab", new[] { "Packages/" + MetaCorePackage })
                .FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == "OVRCameraRig");
            if (rigGuid != null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(rigGuid));
                PrefabUtility.InstantiatePrefab(prefab, scene);
            }
            else
            {
                Debug.LogWarning("[VR Template] OVRCameraRig prefab not found – added a normal camera instead. " +
                                 "Add a camera rig via Meta > Tools > Building Blocks.");
                var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
                cam.transform.position = new Vector3(0, 1.6f, 0);
            }

            // Light
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);

            // Floor + something to look at
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(2, 1, 2);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Cube";
            cube.transform.position = new Vector3(0, 1f, 1.5f);
            cube.transform.localScale = Vector3.one * 0.3f;

            Directory.CreateDirectory(Path.GetDirectoryName(StarterScenePath));
            EditorSceneManager.SaveScene(scene, StarterScenePath);

            // Put it first in the build list
            var others = EditorBuildSettings.scenes.Where(s => s.path != StarterScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(StarterScenePath, true) }.Concat(others).ToArray();
        }
    }
}
