using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using Newtonsoft.Json.Linq;
namespace WebUI.Editor {
    public static class Demo {
        [MenuItem("WebUI/Create demo scene")]
        public static void Create(){
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Game camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.08f,.16f,.28f);
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.transform.position=new Vector3(0,0,4);cube.transform.rotation=Quaternion.Euler(20,25,0);
            new GameObject("EventSystem",typeof(EventSystem),typeof(StandaloneInputModule));
            var canvas=new GameObject("Chromium overlay layer",typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster)).GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=32000;
            var go=new GameObject("Browser",typeof(RectTransform),typeof(RawImage));go.transform.SetParent(canvas.transform,false);
            var rect=(RectTransform)go.transform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;
            var overlay=go.AddComponent<ChromiumOverlay>();overlay.Width=640;overlay.Height=360;
            overlay.RenderMode=Environment.GetCommandLineArgs().Contains("-webui-cpu")?BrowserRenderMode.CPU:BrowserRenderMode.GPURequired;
            overlay.IndependentComposition=Environment.GetCommandLineArgs().Contains("-webui-composition");
            go.AddComponent<ExampleBridge>();
            go.AddComponent<ValidationProbe>();
            string package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ChromiumOverlay).Assembly).resolvedPath;
            string content=Path.Combine(Application.dataPath,"StreamingAssets/WebUIContent");Directory.CreateDirectory(content);
            File.Copy(Path.Combine(package,"Samples~/Overlay/index.html"),Path.Combine(content,"index.html"),true);
            AssetDatabase.Refresh();EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),"Assets/WebUIDemo.unity");
        }
    }
    public sealed class DeployRuntime : IPostprocessBuildWithReport {
        public int callbackOrder=>100;
        public void OnPostprocessBuild(BuildReport report){
            if(report.summary.platform!=BuildTarget.StandaloneWindows64)throw new BuildFailedException("WebUI currently supports Windows x64 only");
            string package=UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ChromiumOverlay).Assembly).resolvedPath;
            string source=RuntimeInstaller.ReadyRoot;
            string exe=report.summary.outputPath;string target=Path.Combine(Path.GetDirectoryName(exe),Path.GetFileNameWithoutExtension(exe)+"_Data/StreamingAssets/WebUI");Copy(source,target);
        }
        static void Copy(string from,string to){Directory.CreateDirectory(to);foreach(string file in Directory.GetFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)),true);foreach(string directory in Directory.GetDirectories(from))Copy(directory,Path.Combine(to,Path.GetFileName(directory)));}
    }
    [InitializeOnLoad]
    public static class Validation {
        static Validation(){if(SessionState.GetBool("WebUI.Test",false))EditorApplication.update+=Poll;}
        static string Report=>Environment.GetCommandLineArgs().FirstOrDefault(s=>s.StartsWith("-webui-report="))?.Substring(14);
        public static async void Run(){
            if(string.IsNullOrEmpty(Report))throw new ArgumentException("Pass -webui-report=<absolute JSON path>");
            if(File.Exists(Report))throw new InvalidOperationException("Use a new report filename");
            try{await RuntimeInstaller.EnsureAsync();}catch(Exception e){Debug.LogException(e);EditorApplication.Exit(3);return;}
            Demo.Create();SessionState.SetBool("WebUI.Test",true);SessionState.SetFloat("WebUI.Start",(float)EditorApplication.timeSinceStartup);
            EditorApplication.update-=Poll;EditorApplication.update+=Poll;EditorApplication.isPlaying=true;
        }
        public static async void RunDebug(){
            if(string.IsNullOrEmpty(Report)||File.Exists(Report))throw new ArgumentException("Pass a new -webui-report path");
            try{await RuntimeInstaller.EnsureAsync();}catch(Exception e){Debug.LogException(e);EditorApplication.Exit(3);return;}
            DebugScene.Create();SessionState.SetBool("WebUI.Test",true);SessionState.SetFloat("WebUI.Start",(float)EditorApplication.timeSinceStartup);
            EditorApplication.update-=Poll;EditorApplication.update+=Poll;EditorApplication.isPlaying=true;
        }
        public static void BuildPlayer(){
            string output=Environment.GetCommandLineArgs().FirstOrDefault(s=>s.StartsWith("-webui-player="))?.Substring(14);
            if(string.IsNullOrEmpty(output))throw new ArgumentException("Pass -webui-player=<absolute exe path>");
            Demo.Create();PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[]{UnityEngine.Rendering.GraphicsDeviceType.Direct3D11});
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{"Assets/WebUIDemo.unity"},locationPathName=output,target=BuildTarget.StandaloneWindows64,options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new BuildFailedException("Windows player build failed");
        }
        static void Poll(){
            if(File.Exists(Report)){var result=JObject.Parse(File.ReadAllText(Report));SessionState.SetBool("WebUI.Test",false);EditorApplication.Exit((bool)result["pass"]?0:1);}
            else if(EditorApplication.timeSinceStartup-SessionState.GetFloat("WebUI.Start",0)>90){SessionState.SetBool("WebUI.Test",false);Debug.LogError("WebUI validation timed out");EditorApplication.Exit(2);}
        }
    }
}
