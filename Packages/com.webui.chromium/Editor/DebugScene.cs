using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace WebUI.Editor {
    public static class DebugScene {
        [MenuItem("WebUI/Create cube interaction debug scene")]
        public static void Create(){
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Scene camera").AddComponent<Camera>();camera.tag="MainCamera";
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.045f,.085f);camera.fieldOfView=42;
            var key=new GameObject("Key light").AddComponent<Light>();key.type=LightType.Directional;key.intensity=1.4f;key.transform.rotation=Quaternion.Euler(35,-35,0);
            var fill=new GameObject("Fill light").AddComponent<Light>();fill.type=LightType.Directional;fill.color=new Color(.3f,.55f,1);fill.intensity=.7f;fill.transform.rotation=Quaternion.Euler(10,135,0);
            RenderSettings.ambientLight=new Color(.25f,.3f,.4f);
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name="Drag to rotate";cube.transform.position=new Vector3(2,0,7);cube.transform.localScale=Vector3.one*1.8f;cube.transform.rotation=Quaternion.Euler(24,-28,12);
            new GameObject("EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));
            var canvas=new GameObject("HTML overlay",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster)).GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=32000;
            var go=new GameObject("Transparent main menu",typeof(RectTransform),typeof(RawImage));go.transform.SetParent(canvas.transform,false);
            var rect=(RectTransform)go.transform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;
            var overlay=go.AddComponent<ChromiumOverlay>();overlay.AutoSize=true;overlay.RenderMode=BrowserRenderMode.GPURequired;overlay.InitialURL="https://ue.local/debug-menu.html";
            var controller=go.AddComponent<DebugCubeController>();controller.Overlay=overlay;controller.View=camera;controller.Cube=cube.transform;
            go.AddComponent<DebugSceneProbe>();
            string package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ChromiumOverlay).Assembly).resolvedPath;
            string content=Path.Combine(Application.dataPath,"StreamingAssets/WebUIContent");Directory.CreateDirectory(content);
            File.Copy(Path.Combine(package,"Samples~/Overlay/debug-menu.html"),Path.Combine(content,"debug-menu.html"),true);
            AssetDatabase.Refresh();EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/WebUIDebug.unity");
        }
    }
}
