using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WebUI {
    // Explicit automation only; does not run in the user's interactive scene.
    public sealed class DebugSceneProbe : MonoBehaviour {
        DebugCubeController controller;float start;bool running;
        string Report=>Environment.GetCommandLineArgs().First(s=>s.StartsWith("-webui-report=")).Substring(14);
        void Start(){enabled=Environment.GetCommandLineArgs().Contains("-webui-debug-test");controller=GetComponent<DebugCubeController>();start=Time.realtimeSinceStartup;Application.runInBackground=true;}
        void Update(){
            if(Application.isBatchMode)controller.View.Render();
            if(!running&&controller.PageReady&&controller.Overlay.PresentedFrames>0){running=true;_ = Check();}
            if(Time.realtimeSinceStartup-start>35){Write(new JObject{["pass"]=false,["error"]="Debug scene timed out"});}
        }
        async Task Check(){
            var result=new JObject();
            try{
                var overlay=controller.Overlay;var session=overlay.Session;
                await Task.Delay(400);
                var rect=await session.CallAsync("debugButton",JValue.CreateNull());
                Vector2 menu=new Vector2((float)rect["x"],Screen.height-(float)rect["y"]);
                Vector2 cube=controller.View.WorldToScreenPoint(controller.Cube.position);
                Physics.SyncTransforms();
                bool menuBlocks=overlay.IsPointerOverUI(menu)&&!controller.TryBeginDrag(menu);
                bool cubePasses=!overlay.IsPointerOverUI(cube)&&controller.TryBeginDrag(cube);
                var original=controller.Cube.rotation;controller.DragTo(cube+new Vector2(90,35));controller.EndDrag();
                bool rotates=Quaternion.Angle(original,controller.Cube.rotation)>10;
                session.Mouse("move",(int)rect["x"],(int)rect["y"]);
                session.Mouse("down",(int)rect["x"],(int)rect["y"],16);session.Mouse("up",(int)rect["x"],(int)rect["y"]);
                for(int i=0;i<100&&controller.MenuClicks==0;i++)await Task.Delay(20);
                bool resets=controller.MenuClicks==1&&Quaternion.Angle(original,controller.Cube.rotation)<.1f;
                var field=await session.CallAsync("debugInput",JValue.CreateNull());
                Vector2 inputPoint=new Vector2((float)field["x"],Screen.height-(float)field["y"]);
                bool inputBlocks=overlay.IsPointerOverUI(inputPoint)&&!controller.TryBeginDrag(inputPoint);
                session.Focus(true);session.Mouse("move",(int)field["x"],(int)field["y"]);
                session.Mouse("down",(int)field["x"],(int)field["y"],16);session.Mouse("up",(int)field["x"],(int)field["y"]);
                foreach(char letter in "Hello"){session.Key(0,char.ToUpperInvariant(letter));session.Key(3,letter);session.Key(2,char.ToUpperInvariant(letter));}
                var value=await session.CallAsync("debugInput",JValue.CreateNull());bool typing=(string)value["value"]=="Hello";
                result=new JObject{["pass"]=menuBlocks&&cubePasses&&rotates&&resets&&inputBlocks&&typing,["menuBlocks"]=menuBlocks,["cubePasses"]=cubePasses,["dragRotates"]=rotates,["htmlResetWorks"]=resets,["inputBlocks"]=inputBlocks,["typingWorks"]=typing,["width"]=overlay.Width,["height"]=overlay.Height};
                await Task.Delay(150);Capture();
            }catch(Exception e){result["pass"]=false;result["error"]=e.ToString();}
            Write(result);
        }
        void Capture(){
            var overlay=controller.Overlay;var rt=RenderTexture.GetTemporary(overlay.Width,overlay.Height,24,RenderTextureFormat.ARGB32);
            var old=RenderTexture.active;var target=controller.View.targetTexture;
            var read=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
            var material=new Material(Shader.Find("WebUI/PremultipliedOverlay"));
            try{controller.View.targetTexture=rt;controller.View.Render();controller.View.targetTexture=target;
                Graphics.Blit(overlay.BrowserTexture,rt,material);RenderTexture.active=rt;
                read.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);read.Apply();File.WriteAllBytes(Path.ChangeExtension(Report,"png"),read.EncodeToPNG());
            }finally{controller.View.targetTexture=target;RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);Destroy(read);Destroy(material);}
        }
        void Write(JObject result){if(!enabled)return;File.WriteAllText(Report,result.ToString());enabled=false;if(!Application.isEditor)Application.Quit((bool)result["pass"]?0:1);}
    }
}
