using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
namespace WebUI {
    // Enabled only by the explicit automated test command line; normal demos remain interactive.
    public sealed class ValidationProbe : MonoBehaviour {
        ChromiumOverlay overlay;BrowserSession session;float start,readAt;int phase;
        bool jsToCs,csToJs,clicked,typed,transparent,opaque,hit,clear;int directOpaque=-1;string failure="";
        bool resizeRequested,resized;BrowserSession beforeResize;
        void Start(){if(!Environment.GetCommandLineArgs().Contains("-webui-test")){enabled=false;return;}Application.runInBackground=true;overlay=GetComponent<ChromiumOverlay>();start=Time.realtimeSinceStartup;}
        void Update(){
            // Batchmode does not continuously paint a Game View. Render the actual
            // Unity camera explicitly so native callbacks run on Unity's render thread.
            if(Application.isBatchMode&&Camera.allCamerasCount>0)Camera.allCameras[0].Render();
            if(overlay.Session!=null&&session==null){session=overlay.Session;session.RequestHandler=(name,data)=>Task.FromResult<JToken>(new JObject{["ok"]=true,["echo"]=data});
                session.Message+=(name,data)=>{if(name=="ready")jsToCs=(bool?)data["ok"]==true;if(name=="clicked")clicked=true;if(name=="typed")typed=(string)data=="x";};session.Error+=e=>failure=e;
            }
            float elapsed=Time.realtimeSinceStartup-start;
            if(session!=null&&session.URL!="about:blank"&&phase==0){phase=1;_ = Call();}
            if(phase==1&&overlay.PresentedFrames>=1&&jsToCs&&csToJs){
                if(Environment.GetCommandLineArgs().Contains("-webui-resize-test")&&!resizeRequested){
                    resizeRequested=true;beforeResize=session;
                    session.Evaluate("window.__resizeToken='preserved';ue.interface.resizeProbe=()=>({token:window.__resizeToken,width:innerWidth,height:innerHeight});");
                    Screen.SetResolution(900,600,FullScreenMode.Windowed);phase=6;return;
                }
                hit=session.HitTest(50f/overlay.Width,50f/overlay.Height);clear=!session.HitTest(.98f,.98f);
                session.Focus(true);session.Mouse("move",50,50);session.Mouse("down",50,50,16);session.Mouse("up",50,50);
                phase=2;
            }
            if(phase==2&&clicked){session.Mouse("down",340,40,16);session.Mouse("up",340,40);session.Key(0,88);session.Key(3,120);session.Key(2,88);phase=3;}
            if(phase==3&&typed){phase=4;readAt=Time.realtimeSinceStartup+.5f;}
            if(phase==4&&Time.realtimeSinceStartup>=readAt){if(overlay.UsingCPU){ReadPixels();Finish();}else{Native.UW_RequestProbe(session.NativeId);phase=5;}}
            if(phase==5&&Native.UW_ProbeAlpha(session.NativeId)!=-1){ReadPixels();Finish();}
            if(phase==6&&overlay.Width==900&&overlay.Height==600&&overlay.PresentedFrames>0){phase=7;_ = CheckResize();}
            else if(elapsed>35){failure="timeout; "+failure+"; "+overlay.Status;Finish();}
        }
        async Task Call(){try{var answer=await session.CallAsync("echo",new JObject{["unicode"]="Grüße 🚀",["nested"]=new JArray(1,true,JValue.CreateNull())});csToJs=(string)answer["received"]?["unicode"]=="Grüße 🚀"&&answer["received"]?["nested"]?.Count()==3;}catch(Exception e){failure=e.Message;}}
        async Task CheckResize(){try{var answer=await session.CallAsync("resizeProbe",JValue.CreateNull());resized=ReferenceEquals(beforeResize,overlay.Session)&&(string)answer["token"]=="preserved"&&(int)answer["width"]==900&&(int)answer["height"]==600;phase=1;}catch(Exception e){failure=e.Message;}}
        void ReadPixels(){
            var direct=AsyncGPUReadback.Request(overlay.BrowserTexture,0,TextureFormat.RGBA32);direct.WaitForCompletion();
            if(!direct.hasError){directOpaque=0;foreach(var c in direct.GetData<Color32>())if(c.a>240)++directOpaque;}
            var rt=RenderTexture.GetTemporary(overlay.Width,overlay.Height,0,RenderTextureFormat.ARGB32);var old=RenderTexture.active;
            var read=new Texture2D(overlay.Width,overlay.Height,TextureFormat.RGBA32,false);
            try{Graphics.Blit(overlay.BrowserTexture,rt);RenderTexture.active=rt;read.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);read.Apply();
                foreach(var c in read.GetPixels32()){if(c.a==0)transparent=true;if(c.a>240)opaque=true;}
                string path=ReportPath();File.WriteAllBytes(Path.ChangeExtension(path,"png"),read.EncodeToPNG());
            }finally{RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);Destroy(read);}
        }
        static string ReportPath()=>Environment.GetCommandLineArgs().First(s=>s.StartsWith("-webui-report=")).Substring(14);
        void Finish(){
            var result=new JObject{["pass"]=jsToCs&&csToJs&&clicked&&typed&&transparent&&opaque&&hit&&clear,["jsToCs"]=jsToCs,["csToJs"]=csToJs,["clicked"]=clicked,["typed"]=typed,["transparent"]=transparent,["opaque"]=opaque,["hit"]=hit,["clickThrough"]=clear,["frames"]=overlay.PresentedFrames,["receivedFrames"]=session?.FramesReceived??0,["renderCallbacks"]=overlay.RenderCallbacks,["cpu"]=overlay.UsingCPU,["graphics"]=SystemInfo.graphicsDeviceType.ToString(),["error"]=failure};
            result["nativeOpaquePixels"]=session==null?-1:Native.UW_ProbeAlpha(session.NativeId);
            result["unityDirectOpaquePixels"]=directOpaque;
            result["width"]=overlay.Width;result["height"]=overlay.Height;result["resizePreservedState"]=resized;
            if(Environment.GetCommandLineArgs().Contains("-webui-resize-test"))result["pass"]=(bool)result["pass"]&&resized;
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath()));File.WriteAllText(ReportPath(),result.ToString());Debug.Log("WEBUI_VALIDATION "+result);enabled=false;
            if(!Application.isEditor)Application.Quit((bool)result["pass"]?0:1);
        }
    }
}
