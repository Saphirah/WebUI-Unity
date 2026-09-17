using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Debug=UnityEngine.Debug;

namespace WebUI.Editor {
    [InitializeOnLoad]
    public static class RuntimeInstaller {
        [Serializable] sealed class Manifest {
            public string version,archive,url,sha256,hostSha256;
            public long size;
        }
        static Task<string> installation;
        static string Package=>UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ChromiumOverlay).Assembly).resolvedPath;
        static Manifest ReadManifest()=>JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(Package,"Binaries~/runtime.json")));
        static string Root(Manifest manifest)=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Library/WebUI",manifest.sha256.Substring(0,16)+"-"+manifest.hostSha256.Substring(0,16)));
        public static string ReadyRoot {
            get {var manifest=ReadManifest();var root=Root(manifest);if(!File.Exists(Path.Combine(root,"ready")))throw new InvalidOperationException("WebUI runtime is not ready. Wait for automatic setup, or use WebUI/Install browser runtime.");Copy(Path.Combine(Package,"BrowserResources"),Path.Combine(root,"BrowserResources"));return root;}
        }
        static RuntimeInstaller(){
            RuntimePreparation.PrepareEditorRuntime=EnsureAsync;
            if(Application.platform==RuntimePlatform.WindowsEditor)EditorApplication.delayCall+=AutoInstall;
        }
        static async void AutoInstall(){try{await EnsureAsync();}catch(Exception e){Debug.LogError("WebUI runtime setup failed: "+e.Message+". Retry with WebUI/Install browser runtime.");}}
        [MenuItem("WebUI/Install browser runtime")]
        public static void Install(){AutoInstall();}
        public static Task<string> EnsureAsync(){
            if(installation==null||installation.IsFaulted||installation.IsCanceled)installation=InstallAsync();
            return installation;
        }
        static async Task<string> InstallAsync(){
            if(Application.platform!=RuntimePlatform.WindowsEditor)throw new PlatformNotSupportedException("WebUI supports Windows x64 only");
            string package=Package;var manifest=ReadManifest();string root=Root(manifest);
            if(File.Exists(Path.Combine(root,"ready"))){Copy(Path.Combine(package,"BrowserResources"),Path.Combine(root,"BrowserResources"));return root;}
            string cache=Path.GetFullPath(Path.Combine(Application.dataPath,"../Library/WebUI/Downloads"));Directory.CreateDirectory(cache);
            string archive=Path.Combine(cache,manifest.archive);
            Debug.Log("WebUI: preparing Chromium runtime (first install downloads approximately 197 MB). No native compilation is required.");
            if(!File.Exists(archive)){
                // Optional predownloaded, checksum-verified archive for offline/CI installation.
                string offline=Environment.GetEnvironmentVariable("WEBUI_CEF_ARCHIVE");
                if(!string.IsNullOrEmpty(offline))File.Copy(offline,archive,true);
                else{
                    string partial=archive+".partial";
                    using(var request=UnityWebRequest.Get(manifest.url)){
                        request.downloadHandler=new DownloadHandlerFile(partial){removeFileOnAbort=true};request.timeout=1800;
                        var operation=request.SendWebRequest();while(!operation.isDone)await Task.Delay(100);
                        if(request.result!=UnityWebRequest.Result.Success)throw new IOException("CEF download: "+request.error);
                    }
                    try{await Task.Run(()=>Verify(partial,manifest.sha256,manifest.size));File.Move(partial,archive);}
                    catch{if(File.Exists(partial))File.Delete(partial);throw;}
                }
            }
            try{await Task.Run(()=>Verify(archive,manifest.sha256,manifest.size));}
            catch{File.Delete(archive);throw;}
            await Task.Run(()=>{
                string unpack=Path.Combine(cache,manifest.archive.Replace(".tar.bz2",""));
                string marker=Path.Combine(unpack,".webui-extracted");
                if(!File.Exists(marker)){
                    var info=new ProcessStartInfo("tar.exe","-xf "+Quote(archive)+" -C "+Quote(cache)){UseShellExecute=false,CreateNoWindow=true};
                    using(var process=Process.Start(info)){
                        if(!process.WaitForExit(300000)){process.Kill();throw new TimeoutException("CEF extraction timed out");}
                        if(process.ExitCode!=0)throw new IOException("CEF extraction failed");
                    }
                    File.WriteAllText(marker,manifest.sha256);
                }
                string host=Path.Combine(package,"Binaries~/WebUIHost.exe");Verify(host,manifest.hostSha256,null);
                Directory.CreateDirectory(root);string cef=Path.Combine(root,"CEF");Directory.CreateDirectory(cef);
                foreach(string file in Directory.GetFiles(Path.Combine(unpack,"Release"))){string extension=Path.GetExtension(file);if(extension!=".lib"&&extension!=".pdb")File.Copy(file,Path.Combine(cef,Path.GetFileName(file)),true);}
                Copy(Path.Combine(unpack,"Resources"),cef);
                foreach(string file in Directory.GetFiles(unpack,"*LICENSE*"))File.Copy(file,Path.Combine(cef,Path.GetFileName(file)),true);
                Copy(Path.Combine(package,"BrowserResources"),Path.Combine(root,"BrowserResources"));
                File.Copy(host,Path.Combine(root,"WebUIHost.exe"),true);
                File.WriteAllText(Path.Combine(root,"ready"),manifest.version);
            });
            Debug.Log("WebUI: Chromium runtime ready. Open a WebUI demo scene and press Play.");return root;
        }
        static void Verify(string file,string expected,long? size){
            if(size.HasValue&&new FileInfo(file).Length!=size.Value)throw new IOException("Unexpected CEF archive size");
            using(var stream=File.OpenRead(file))using(var hash=SHA256.Create()){
                string actual=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                if(!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("SHA-256 mismatch: "+Path.GetFileName(file));
            }
        }
        static string Quote(string value){if(value.Contains("\""))throw new ArgumentException("Invalid path");return "\""+value+"\"";}
        static void Copy(string source,string target){Directory.CreateDirectory(target);foreach(string file in Directory.GetFiles(source))if(!file.EndsWith(".meta"))File.Copy(file,Path.Combine(target,Path.GetFileName(file)),true);foreach(string child in Directory.GetDirectories(source))Copy(child,Path.Combine(target,Path.GetFileName(child)));}
    }
}
