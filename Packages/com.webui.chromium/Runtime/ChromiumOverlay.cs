using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
namespace WebUI {
    public enum BrowserRenderMode { GPUWithCPUFallback, GPURequired, CPU }
    /// <summary>Fullscreen transparent uGUI layer. Legacy input adapter; custom input systems can use Session directly.</summary>
    [RequireComponent(typeof(RawImage))]
    [DisallowMultipleComponent]
    public sealed class ChromiumOverlay : MonoBehaviour,ICanvasRaycastFilter {
        public string InitialURL="https://ue.local/index.html";
        public string ContentRoot="";
        public string RuntimeRoot="";
        public string[] TrustedOrigins={"https://ue.local"};
        [Tooltip("Match the Game View/player window pixel dimensions automatically.")]
        public bool AutoSize=true;
        [Min(1)] public int Width=1280,Height=720;
        public BrowserRenderMode RenderMode=BrowserRenderMode.GPUWithCPUFallback;
        [Tooltip("Present above the entire Windows player client area on a separate DirectComposition thread.")]
        public bool IndependentComposition;
        [Range(1,360)] public int BrowserFrameRate=120;
        public bool ForwardLegacyInput=true;
        public BrowserSession Session {get;private set;}
        public Texture BrowserTexture=>texture;
        public bool BrowserFocused {get;private set;}
        public bool UsingCPU {get;private set;}
        public int PresentedFrames=>UsingCPU?uploads:Session==null?0:Native.UW_Copies(Session.NativeId);
        public string Status {get;private set;}="Stopped";
        public int RenderCallbacks {get;private set;}
        public bool IndependentPresentationActive=>usingComposition&&Session!=null&&Native.UW_CompositionActive(Session.NativeId)!=0;
        public string IndependentCompositionStatus {get;private set;}="Disabled";
        public event Action<BrowserSession> Started;
        RawImage image;Texture texture;Material material;byte[] pixels;int uploads,buttons;Vector3 lastMouse;
        bool stopped,ready;CommandBuffer command;int imported,lifecycle;
        bool usingComposition,compositionSuppressed,compositionVisible,imageConfigured;
        Color previousImageColor;
        bool previousCullTransparentMesh;
        int appliedFrameRate;
        async void OnEnable(){
            int activation=++lifecycle;stopped=false;ready=false;uploads=imported=0;image=GetComponent<RawImage>();
            try {
                if(Application.platform!=RuntimePlatform.WindowsEditor&&Application.platform!=RuntimePlatform.WindowsPlayer)throw new PlatformNotSupportedException("Windows x64 only");
                if(AutoSize){Width=Screen.width;Height=Screen.height;}
                Width=Mathf.Max(1,Width);Height=Mathf.Max(1,Height);
                Status="Preparing Chromium runtime";
                string root=await ResolveRuntimeRoot();if(stopped||activation!=lifecycle)return;
                if(AutoSize){Width=Mathf.Max(1,Screen.width);Height=Mathf.Max(1,Screen.height);}
                string content=string.IsNullOrEmpty(ContentRoot)?Path.Combine(Application.streamingAssetsPath,"WebUIContent"):Path.GetFullPath(ContentRoot);
                CheckSize(Width,Height);int id=0;UsingCPU=RenderMode==BrowserRenderMode.CPU||(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11&&!IsD3D12);
                if(RenderMode==BrowserRenderMode.GPURequired&&UsingCPU)throw new NotSupportedException("The GPU path requires Direct3D11 or Direct3D12");
                usingComposition=IndependentComposition;
                IndependentCompositionStatus=usingComposition?"Starting":"Disabled";
                if(usingComposition){
                    if(Application.platform!=RuntimePlatform.WindowsPlayer||Application.isBatchMode||UsingCPU||
                        Screen.fullScreenMode==FullScreenMode.ExclusiveFullScreen)
                        throw new NotSupportedException("DirectComposition requires a windowed or borderless Windows player using Direct3D 11 or 12");
                    id=Native.UW_CreateComposition(Width,Height,out int error);
                    if(id==0)throw new InvalidOperationException($"Cannot initialize DirectComposition (0x{error:X8})");
                }else if(!UsingCPU){
                    // A persistent sampled texture avoids Unity render-target discard/clear bookkeeping.
                    var target=new Texture2D(Width,Height,TextureFormat.RGBA32,false,true){name="Chromium GPU overlay"};
                    texture=target;
                    id=CreateGPU(target.GetNativeTexturePtr());
                    if(id==0){Destroy(target);texture=null;if(RenderMode==BrowserRenderMode.GPURequired)throw new InvalidOperationException("Shared GPU ring creation failed: "+Native.UW_CreateError());UsingCPU=true;}
                    else{texture=Texture2D.CreateExternalTexture(Width,Height,TextureFormat.RGBA32,false,true,Native.UW_Output(id));Destroy(target);}
                }
                if(UsingCPU){id=Native.UW_CreateCPU(Width,Height);pixels=new byte[Width*Height*4];texture=new Texture2D(Width,Height,TextureFormat.BGRA32,false,true){name="Chromium CPU fallback"};}
                if(id==0)throw new InvalidOperationException("Browser texture transport initialization failed");
                if(usingComposition){
                    previousImageColor=image.color;
                    previousCullTransparentMesh=image.canvasRenderer.cullTransparentMesh;
                    imageConfigured=true;
                    image.canvasRenderer.cullTransparentMesh=false;
                    image.color=Color.clear;
                }else{
                    var shader=Shader.Find("WebUI/PremultipliedOverlay");if(!shader){Native.UW_Close(id);throw new InvalidOperationException("Overlay shader missing");}
                    material=new Material(shader);image.material=material;image.texture=texture;
                }
                image.raycastTarget=true;
                appliedFrameRate=Mathf.Clamp(BrowserFrameRate,1,360);
                try{Session=new BrowserSession(id,Width,Height,UsingCPU,root,content,TrustedOrigins,!usingComposition&&IsD3D12,usingComposition,appliedFrameRate);}
                catch{Native.UW_Close(id);throw;}
                Session.Error+=e=>{Status=e;Debug.LogError("WebUI: "+e,this);};
                Session.Loaded+=url=>Status=(usingComposition?"DirectComposition: ":UsingCPU?"CPU fallback: ":"GPU rendering: ")+url;
                if(usingComposition){
                    compositionVisible=!compositionSuppressed;
                    Session.ShowComposition(compositionVisible);
                }else{
                    command=new CommandBuffer{name="Chromium texture import"};command.IssuePluginEvent(Native.UW_GetRenderEvent(),id);
                    Camera.onPreRender+=BeforeCamera;RenderPipelineManager.beginContextRendering+=BeforeSRP;
                }
                Status=UsingCPU?"Starting explicit CPU fallback":"Starting GPU browser";
                var starting=Session;await starting.StartAsync(InitialURL);
                if(!stopped&&Session==starting){ready=true;Started?.Invoke(starting);}
            }catch(Exception e){if(!stopped&&activation==lifecycle){Status=e.Message;if(IndependentComposition)IndependentCompositionStatus=e.Message;Debug.LogException(e,this);Stop();}}
        }
        async Task<string> ResolveRuntimeRoot(){
            if(!string.IsNullOrEmpty(RuntimeRoot))return Path.GetFullPath(RuntimeRoot);
            #if UNITY_EDITOR
            if(RuntimePreparation.PrepareEditorRuntime==null)throw new InvalidOperationException("WebUI Editor runtime installer is unavailable");
            return await RuntimePreparation.PrepareEditorRuntime();
            #else
            return Path.Combine(Application.streamingAssetsPath,"WebUI");
            #endif
        }
        void BeforeCamera(Camera camera){if(!UsingCPU&&Session!=null&&GraphicsSettings.currentRenderPipeline==null){++RenderCallbacks;Graphics.ExecuteCommandBuffer(command);GL.InvalidateState();}}
        void BeforeSRP(ScriptableRenderContext context,List<Camera> cameras){if(!UsingCPU&&Session!=null){++RenderCallbacks;context.ExecuteCommandBuffer(command);}}
        void Update(){
            Session?.Pump();if(Session==null)return;
            if(ready&&appliedFrameRate!=Mathf.Clamp(BrowserFrameRate,1,360)){
                appliedFrameRate=Mathf.Clamp(BrowserFrameRate,1,360);
                Session.SetFrameRate(appliedFrameRate);
            }
            if(usingComposition){
                int result=Native.UW_Error(Session.NativeId);
                if(result<0){
                    Status=IndependentCompositionStatus=$"DirectComposition failed (0x{result:X8})";
                    Debug.LogError("WebUI: "+Status,this);
                    Stop();
                    return;
                }
                UpdateCompositionVisibility();
                IndependentCompositionStatus=Screen.fullScreenMode==FullScreenMode.ExclusiveFullScreen?"Exclusive fullscreen":
                    compositionSuppressed?"Suppressed":IndependentPresentationActive?"Active":"Waiting for a browser frame";
            }
            if(ready&&AutoSize&&Screen.width>0&&Screen.height>0){
                int w=Mathf.Max(1,Screen.width),h=Mathf.Max(1,Screen.height);
                if(w!=Width||h!=Height){try{Resize(w,h);}catch(Exception e){Status=e.Message;Debug.LogException(e,this);Stop();return;}}
            }
            if(!usingComposition&&!UsingCPU&&PresentedFrames!=imported){((Texture2D)texture).UpdateExternalTexture(Native.UW_Output(Session.NativeId));imported=PresentedFrames;}
            if(UsingCPU&&Native.UW_ReadCPU(Session.NativeId,pixels,pixels.Length)!=0){var t=(Texture2D)texture;t.LoadRawTextureData(pixels);t.Apply(false,false);++uploads;}
            if(!UsingCPU&&Native.UW_Error(Session.NativeId)>0){Status="GPU device/synchronization failure; restart browser";Stop();return;}
            if(ForwardLegacyInput&&(!usingComposition||compositionVisible))InputTick();
        }
        bool IsD3D12=>SystemInfo.graphicsDeviceType==GraphicsDeviceType.Direct3D12;
        int CreateGPU(IntPtr value)=>IsD3D12?Native.UW_CreateGPU12(value):Native.UW_CreateGPU(value);
        static void CheckSize(int width,int height){
            if(width>SystemInfo.maxTextureSize||height>SystemInfo.maxTextureSize)throw new ArgumentOutOfRangeException("Resolution exceeds the graphics device texture limit");
            if((long)width*height*4>int.MaxValue)throw new ArgumentOutOfRangeException("Pixel buffer is too large");
        }
        void Resize(int width,int height){
            CheckSize(width,height);
            if(usingComposition){
                Session.ResizeComposition(width,height);
                Width=width;Height=height;
                lastMouse=new Vector3(float.NaN,float.NaN,0);
                return;
            }
            Texture2D replacement=null;int nativeId=0;
            try{
                if(UsingCPU){nativeId=Native.UW_CreateCPU(width,height);replacement=new Texture2D(width,height,TextureFormat.BGRA32,false,true);}
                else{
                    var bootstrap=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
                    try{nativeId=CreateGPU(bootstrap.GetNativeTexturePtr());}finally{Destroy(bootstrap);}
                    if(nativeId!=0)replacement=Texture2D.CreateExternalTexture(width,height,TextureFormat.RGBA32,false,true,Native.UW_Output(nativeId));
                }
                if(nativeId==0)throw new InvalidOperationException("Cannot resize Chromium pixel transport");
                Session.ResizeSurface(nativeId,width,height);nativeId=0;
                var old=texture;texture=replacement;replacement=null;Width=width;Height=height;
                if(UsingCPU)pixels=new byte[width*height*4];
                command.Clear();command.IssuePluginEvent(Native.UW_GetRenderEvent(),Session.NativeId);
                imported=uploads=0;image.texture=texture;lastMouse=new Vector3(float.NaN,float.NaN,0);
                Destroy(old);
            }finally{if(nativeId!=0)Native.UW_Close(nativeId);if(replacement)Destroy(replacement);}
        }
        public void SetIndependentPresentationSuppressed(bool suppressed){
            compositionSuppressed=suppressed;
            UpdateCompositionVisibility();
        }
        void UpdateCompositionVisibility(){
            if(!usingComposition||Session==null)return;
            bool visible=!compositionSuppressed&&Screen.fullScreenMode!=FullScreenMode.ExclusiveFullScreen;
            if(compositionVisible==visible)return;
            compositionVisible=visible;
            if(!visible)ReleaseBrowserFocus();
            Session.ShowComposition(visible);
        }
        public bool IsPointerOverUI(Vector2 point){return Session!=null&&(!usingComposition||compositionVisible)&&Session.HitTest(point.x/Mathf.Max(1,Screen.width),1-point.y/Mathf.Max(1,Screen.height));}
        public bool IsRaycastLocationValid(Vector2 screenPoint,Camera eventCamera)=>IsPointerOverUI(screenPoint);
        int Modifiers(){return (Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift)?2:0)|(Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl)?4:0)|(Input.GetKey(KeyCode.LeftAlt)||Input.GetKey(KeyCode.RightAlt)?8:0)|buttons;}
        void InputTick(){
            var p=Input.mousePosition;int x=Mathf.FloorToInt(p.x/Mathf.Max(1,Screen.width)*Width),y=Mathf.FloorToInt((1-p.y/Mathf.Max(1,Screen.height))*Height);
            if(p!=lastMouse){Session.Mouse(p.x<0||p.y<0||p.x>=Screen.width||p.y>=Screen.height?"leave":"move",x,y,Modifiers());lastMouse=p;}
            bool over=IsPointerOverUI(p);
            for(int i=0;i<3;i++){int mask=i==0?16:i==1?64:32;int cef=i==0?0:i==1?2:1;
                if(Input.GetMouseButtonDown(i)){if(over||buttons!=0){buttons|=mask;BrowserFocused=true;Session.Focus(true);Session.Mouse("down",x,y,Modifiers(),cef);}else{BrowserFocused=false;Session.Focus(false);}}
                if(Input.GetMouseButtonUp(i)&&(buttons&mask)!=0){buttons&=~mask;Session.Mouse("up",x,y,Modifiers(),cef);}
            }
            float scroll=Input.mouseScrollDelta.y;if(over&&scroll!=0)Session.Mouse("wheel",x,y,Modifiers(),0,Mathf.RoundToInt(scroll*120));
        }
        void OnGUI(){if(!ForwardLegacyInput||Session==null||!BrowserFocused)return;var e=Event.current;
            if(e.type==EventType.KeyDown){int vk=VirtualKey(e.keyCode);if(vk!=0)Session.Key(0,vk,Modifiers());if(e.character!='\0')Session.Key(3,e.character,Modifiers());e.Use();}
            else if(e.type==EventType.KeyUp){int vk=VirtualKey(e.keyCode);if(vk!=0)Session.Key(2,vk,Modifiers());e.Use();}
        }
        static int VirtualKey(KeyCode key){int k=(int)key;if(k>=97&&k<=122)return k-32;if(k>=48&&k<=57)return k;if(k>=282&&k<=293)return 112+k-282;
            switch(key){case KeyCode.Backspace:return 8;case KeyCode.Tab:return 9;case KeyCode.Return:return 13;case KeyCode.Escape:return 27;case KeyCode.Space:return 32;case KeyCode.Delete:return 46;case KeyCode.LeftArrow:return 37;case KeyCode.UpArrow:return 38;case KeyCode.RightArrow:return 39;case KeyCode.DownArrow:return 40;case KeyCode.Home:return 36;case KeyCode.End:return 35;case KeyCode.PageUp:return 33;case KeyCode.PageDown:return 34;default:return 0;}}
        void ReleaseBrowserFocus(){
            int held=buttons;
            buttons=0;BrowserFocused=false;
            int x=Mathf.FloorToInt(lastMouse.x/Mathf.Max(1,Screen.width)*Width);
            int y=Mathf.FloorToInt((1-lastMouse.y/Mathf.Max(1,Screen.height))*Height);
            if((held&16)!=0)Session.Mouse("up",x,y,Modifiers(),0);
            if((held&32)!=0)Session.Mouse("up",x,y,Modifiers(),1);
            if((held&64)!=0)Session.Mouse("up",x,y,Modifiers(),2);
            Session.Mouse("leave",x,y);
            Session.Focus(false);
            lastMouse=new Vector3(float.NaN,float.NaN,0);
        }
        void OnApplicationFocus(bool focus){
            if(Session==null)return;
            if(!focus)ReleaseBrowserFocus();
            else if(usingComposition&&compositionVisible)Session.ShowComposition(true);
        }
        void OnDisable(){Stop();IndependentCompositionStatus="Disabled";}
        void Stop(){++lifecycle;stopped=true;ready=false;Camera.onPreRender-=BeforeCamera;RenderPipelineManager.beginContextRendering-=BeforeSRP;Session?.Dispose();Session=null;command?.Dispose();command=null;
            if(image){
                if(imageConfigured){image.color=previousImageColor;image.canvasRenderer.cullTransparentMesh=previousCullTransparentMesh;}
                else if(texture||material){image.texture=null;image.material=null;}
            }
            if(texture)Destroy(texture);if(material)Destroy(material);texture=null;material=null;
            imageConfigured=usingComposition=false;buttons=0;BrowserFocused=false;lastMouse=new Vector3(float.NaN,float.NaN,0);}
    }
}
