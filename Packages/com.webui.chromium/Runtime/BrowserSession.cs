using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Win32.SafeHandles;
namespace WebUI {
    /// <summary>One independent CEF process and bounded asynchronous pipe. Pump() delivers public callbacks on Unity's main thread.</summary>
    public sealed class BrowserSession : IDisposable {
        int id,width,height;readonly int ownerId;readonly bool cpu,d3d12;
        readonly object surfaceGate=new object();int generation=1;
        readonly string runtimeRoot,contentRoot;
        readonly HashSet<string> trusted;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly ConcurrentQueue<byte[]> outgoing=new ConcurrentQueue<byte[]>();
        readonly SemaphoreSlim wake=new SemaphoreSlim(0);
        readonly ConcurrentQueue<Action> main=new ConcurrentQueue<Action>();
        readonly ConcurrentDictionary<long,TaskCompletionSource<JToken>> pending=new ConcurrentDictionary<long,TaskCompletionSource<JToken>>();
        readonly HitRegions regions=new HitRegions();
        readonly TaskCompletionSource<bool> created=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NamedPipeServerStream pipe;Process host;Task reader,writer,releases;
        long queuedBytes,requestId;int disposed,mainCount,resourceCount;
        volatile string html;
        public event Action<string,JToken> Message;
        public event Action<string> Loaded;
        public event Action<string> Error;
        public Func<string,JToken,Task<JToken>> RequestHandler {get;set;}
        public int NativeId=>id;
        public bool IsCPU=>cpu;
        public long FramesReceived=>Interlocked.Read(ref framesReceived);
        long framesReceived;
        public string URL {get;private set;}="about:blank";
        internal BrowserSession(int nativeId,int width,int height,bool cpu,string runtimeRoot,string contentRoot,IEnumerable<string> origins,bool d3d12=false){
            this.d3d12=d3d12;
            id=ownerId=nativeId;this.width=width;this.height=height;this.cpu=cpu;this.runtimeRoot=Path.GetFullPath(runtimeRoot);
            this.contentRoot=Path.GetFullPath(contentRoot);trusted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var origin in origins??Array.Empty<string>())if(Uri.TryCreate(origin,UriKind.Absolute,out var uri))trusted.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        public async Task StartAsync(string initialURL){
            if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(nameof(BrowserSession));
            string exe=Path.Combine(runtimeRoot,"WebUIHost.exe"),cef=Path.Combine(runtimeRoot,"CEF");
            if(!File.Exists(exe)||!File.Exists(Path.Combine(cef,"libcef.dll")))throw new FileNotFoundException("Run Scripts/Build.ps1 from the WebUI-Unity repository; CEF runtime is missing",exe);
            string name="UnityWebUI-"+Guid.NewGuid().ToString("N");
            var handlePipe=new SafePipeHandle(Native.UW_CreatePipe("\\\\.\\pipe\\"+name),true);
            if(handlePipe.IsInvalid){handlePipe.Dispose();throw new IOException("Cannot create owner-only browser pipe");}
            pipe=new NamedPipeServerStream(PipeDirection.InOut,true,false,handlePipe);
            uint high=0,low=0;if(!cpu&&Native.UW_Adapter(id,out high,out low)==0)throw new InvalidOperationException("No D3D11 adapter LUID");
            string cache=Path.Combine(Path.GetTempPath(),"UnityWebUI",Guid.NewGuid().ToString("N"));
            string args="--pipe="+Quote("\\\\.\\pipe\\"+name)+" --runtime="+Quote(cef)+" --resources="+Quote(Path.Combine(runtimeRoot,"BrowserResources"))+" --cache="+Quote(cache)+" --url=about:blank --fps=120 --adapter="+high+","+low+(cpu?" --cpu":"");
            var start=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=cef};
            // Unity's protocol consumes deltas regardless of unrelated Unreal profiling overrides.
            start.EnvironmentVariables["WEBUI_INCREMENTAL_DOM"]="1";start.EnvironmentVariables["WEBUI_AB"]="0";start.EnvironmentVariables.Remove("WEBUI_GPU_PROFILE");
            host=Process.Start(start);if(host==null)throw new InvalidOperationException("CEF process did not start");
            if(Native.UW_TrackProcess(id,host.Id)==0)throw new IOException("Cannot attach CEF to lifetime job");
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){
                timeout.CancelAfter(TimeSpan.FromSeconds(15));await pipe.WaitForConnectionAsync(timeout.Token);
            }
            if(Volatile.Read(ref disposed)!=0)throw new ObjectDisposedException(nameof(BrowserSession));
            writer=Task.Run(WriteLoop);reader=Task.Run(ReadLoop);releases=Task.Run(ReleaseLoop);
            if(await Task.WhenAny(created.Task,Task.Delay(15000,lifetime.Token))!=created.Task)throw new TimeoutException("CEF browser creation timed out");
            PublishSurface();
            // Host sends created before processing load/pool commands on its UI thread.
            LoadURL(initialURL);
        }
        void PublishSurface(){
            if(cpu){if(Native.UW_Duplicate(id,host.Id,0,out var handle)==0)throw new IOException("Cannot share CPU frame mapping");
                Send(new JObject{["op"]="cpuPool",["handle"]=handle.ToString(),["width"]=width,["height"]=height});
            }else{
                var slots=new JArray();for(int i=0;i<3;i++){if(Native.UW_Duplicate(id,host.Id,i,out var handle)==0)throw new IOException("Cannot share GPU ring");slots.Add(new JObject{["id"]=i,["handle"]=handle.ToString()});}
                Send(new JObject{["op"]="pool",["generation"]=generation,["width"]=width,["height"]=height,["slots"]=slots,["d3d12"]=d3d12});
            }
            Send(new JObject{["op"]="resize",["width"]=width,["height"]=height});
        }
        // Replace only the pixel transport. The CEF process, document and JS state survive.
        internal void ResizeSurface(int nativeId,int newWidth,int newHeight){
            lock(surfaceGate){
                int previous=id;id=nativeId;width=newWidth;height=newHeight;++generation;
                PublishSurface();
                if(previous!=ownerId)Native.UW_Close(previous);
            }
        }
        static string Quote(string s){if(s.Contains("\"")||s.Contains("\r")||s.Contains("\n"))throw new ArgumentException("Invalid process argument");return "\""+s+"\"";}
        void Post(Action action){if(Interlocked.Increment(ref mainCount)>4096){Interlocked.Decrement(ref mainCount);return;}main.Enqueue(action);}
        public void Pump(){for(int n=0;n<256&&main.TryDequeue(out var action);n++){Interlocked.Decrement(ref mainCount);action();}}
        public bool HitTest(float u,float v)=>regions.Hit(u,v,width,height);
        void Send(JObject packet){
            if(Volatile.Read(ref disposed)!=0)return;
            byte[] bytes=Encoding.UTF8.GetBytes(packet.ToString(Formatting.None));
            if(bytes.Length>16*1024*1024)throw new ArgumentException("Packet exceeds 16 MiB");
            if(Interlocked.Add(ref queuedBytes,bytes.Length)>64*1024*1024){Interlocked.Add(ref queuedBytes,-bytes.Length);throw new IOException("Browser send queue exceeded 64 MiB");}
            outgoing.Enqueue(bytes);wake.Release();
        }
        async Task WriteLoop(){try{while(!lifetime.IsCancellationRequested){await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
            while(outgoing.TryDequeue(out var bytes)){Interlocked.Add(ref queuedBytes,-bytes.Length);var prefix=BitConverter.GetBytes(bytes.Length);
                await pipe.WriteAsync(prefix,0,4,lifetime.Token).ConfigureAwait(false);await pipe.WriteAsync(bytes,0,bytes.Length,lifetime.Token).ConfigureAwait(false);}
        }}catch(Exception e){Fail(e);}}
        async Task ReadExactly(byte[] bytes){int offset=0;while(offset<bytes.Length){int n=await pipe.ReadAsync(bytes,offset,bytes.Length-offset,lifetime.Token).ConfigureAwait(false);if(n==0)throw new EndOfStreamException("CEF pipe closed");offset+=n;}}
        async Task ReadLoop(){try{while(!lifetime.IsCancellationRequested){var prefix=new byte[4];await ReadExactly(prefix).ConfigureAwait(false);int count=BitConverter.ToInt32(prefix,0);
            if(count<=0||count>16*1024*1024)throw new IOException("Invalid frame length");var data=new byte[count];await ReadExactly(data).ConfigureAwait(false);Receive(JObject.Parse(Encoding.UTF8.GetString(data)));
        }}catch(Exception e){Fail(e);}}
        async Task ReleaseLoop(){try{while(!lifetime.IsCancellationRequested){lock(surfaceGate){int slot;while((slot=Native.UW_Release(id))>=0)Send(new JObject{["op"]="release",["generation"]=generation,["slot"]=slot});}await Task.Delay(2,lifetime.Token).ConfigureAwait(false);}}catch(Exception e){Fail(e);}}
        void Fail(Exception error){if(Volatile.Read(ref disposed)==0){foreach(var entry in pending)entry.Value.TrySetException(error);Post(()=>Error?.Invoke(error.Message));}}
        bool Trusted(string address)=>Uri.TryCreate(address,UriKind.Absolute,out var uri)&&trusted.Contains(uri.GetLeftPart(UriPartial.Authority));
        void Receive(JObject p){
            switch((string)p["op"]){
                case "created":created.TrySetResult(true);break;
                case "frame": lock(surfaceGate){if((int?)p["generation"]==generation){Interlocked.Increment(ref framesReceived);Native.UW_Frame(id,(int)p["slot"],ulong.Parse((string)p["sequence"]));}}break;
                case "regionsDelta": regions.Update(p);break;
                case "loading":regions.Clear();break;
                case "url":string url=(string)p["url"];Post(()=>URL=url);break;
                case "loaded":string loaded=(string)p["url"];if(loaded!="about:blank")Post(()=>{URL=loaded;Loaded?.Invoke(loaded);});break;
                case "error":string error=(string)p["message"];Post(()=>Error?.Invoke(error));break;
                case "resource":
                    if(Interlocked.Increment(ref resourceCount)>16){Interlocked.Decrement(ref resourceCount);ResourceReply((int)p["id"],429,"text/plain",Array.Empty<byte>());break;}
                    _=Task.Run(()=>{try{Resource(p);}catch(Exception e){ResourceReply((int)p["id"],500,"text/plain",Encoding.UTF8.GetBytes(e.Message));}finally{Interlocked.Decrement(ref resourceCount);}});break;
                case "event":
                    if(!Trusted((string)p["originUrl"]))break;
                    string name=(string)p["name"],callback=(string)p["callback"];JToken value=JToken.Parse((string)p["data"]??"null");
                    if(name=="__unity_reply"){
                        long key=(long)value["id"];if(pending.TryRemove(key,out var done)){if(value["error"]!=null)done.TrySetException(new Exception((string)value["error"]));else done.TrySetResult(value["value"]??JValue.CreateNull());}break;
                    }
                    Post(()=>Deliver(name,value,callback));break;
            }
        }
        async void Deliver(string name,JToken value,string callback){
            try{Message?.Invoke(name,value);if(!string.IsNullOrEmpty(callback)){
                var answer=RequestHandler!=null?await RequestHandler(name,value):new JObject{["error"]="No request handler"};Reply(callback,answer);
            }}catch(Exception e){Error?.Invoke(e.Message);if(!string.IsNullOrEmpty(callback))Reply(callback,new JObject{["error"]=e.Message});}
        }
        void Reply(string callback,JToken answer)=>Evaluate("globalThis.ue?.interface["+JsonConvert.SerializeObject(callback)+"]?.("+(answer??JValue.CreateNull()).ToString(Formatting.None)+");");
        void ResourceReply(int key,int status,string mime,byte[] bytes)=>Send(new JObject{["op"]="resource",["id"]=key,["status"]=status,["mime"]=mime,["body"]=Convert.ToBase64String(bytes)});
        void Resource(JObject p){
            int key=(int)p["id"];var uri=new Uri((string)p["url"]);string path=Uri.UnescapeDataString(uri.AbsolutePath);
            if(uri.Host!="ue.local"){ResourceReply(key,403,"text/plain",Array.Empty<byte>());return;}
            if(path=="/index.html"&&html!=null){ResourceReply(key,200,"text/html",Encoding.UTF8.GetBytes(html));return;}
            string root=path.StartsWith("/__webui/",StringComparison.Ordinal)?Path.Combine(runtimeRoot,"BrowserResources"):contentRoot;
            string relative=path.StartsWith("/__webui/",StringComparison.Ordinal)?path.Substring(9):path.TrimStart('/');
            if(string.IsNullOrEmpty(relative))relative="index.html";
            string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!full.StartsWith(Path.GetFullPath(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(full)){ResourceReply(key,404,"text/plain",Array.Empty<byte>());return;}
            if(new FileInfo(full).Length>8*1024*1024){ResourceReply(key,413,"text/plain",Array.Empty<byte>());return;}
            string ext=Path.GetExtension(full).ToLowerInvariant();string mime=ext==".html"?"text/html":ext==".js"||ext==".mjs"?"text/javascript":ext==".css"?"text/css":ext==".json"?"application/json":ext==".svg"?"image/svg+xml":ext==".png"?"image/png":ext==".jpg"?"image/jpeg":ext==".wasm"?"application/wasm":ext==".woff2"?"font/woff2":"application/octet-stream";
            ResourceReply(key,200,mime,File.ReadAllBytes(full));
        }
        public void LoadURL(string url){if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||(uri.Scheme!="http"&&uri.Scheme!="https"))throw new ArgumentException("Use an HTTP(S) URL or LoadHTML");html=null;Send(new JObject{["op"]="load",["url"]=url});}
        public void LoadHTML(string content){if(content==null||Encoding.UTF8.GetByteCount(content)>8*1024*1024)throw new ArgumentException("HTML must be <=8 MiB");html=content;Send(new JObject{["op"]="html",["url"]="https://ue.local/index.html"});}
        public void Evaluate(string script)=>Send(new JObject{["op"]="eval",["script"]=script});
        public async Task<JToken> CallAsync(string name,JToken argument,TimeSpan? timeout=null){
            long key=Interlocked.Increment(ref requestId);var done=new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);pending[key]=done;
            try{Evaluate("Promise.resolve().then(()=>ue.interface["+JsonConvert.SerializeObject(name)+"]("+(argument??JValue.CreateNull()).ToString(Formatting.None)+")).then(value=>ue5('__unity_reply',{id:"+key+",value:value??null}),error=>ue5('__unity_reply',{id:"+key+",error:String(error)}));");
                var delay=Task.Delay(timeout??TimeSpan.FromSeconds(5),lifetime.Token);if(await Task.WhenAny(done.Task,delay)!=done.Task)throw new TimeoutException("JavaScript call timed out or browser closed");return await done.Task;
            }finally{pending.TryRemove(key,out _);}
        }
        public void Mouse(string kind,int x,int y,int modifiers=0,int button=0,int delta=0)=>Send(new JObject{["op"]="mouse",["kind"]=kind,["x"]=x,["y"]=y,["modifiers"]=modifiers,["button"]=button,["count"]=1,["delta"]=delta});
        public void Key(int type,int key,int modifiers=0)=>Send(new JObject{["op"]="key",["type"]=type,["key"]=key,["native"]=0,["modifiers"]=modifiers,["system"]=false});
        public void Focus(bool focus)=>Send(new JObject{["op"]="focus",["focus"]=focus});
        public void Dispose(){
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            lifetime.Cancel();pipe?.Dispose();
            foreach(var entry in pending)entry.Value.TrySetCanceled();pending.Clear();
            // Kill only the process we created. Host detects pipe loss; descendants exit with CEF.
            try{if(host!=null&&!host.HasExited)host.Kill();}catch(InvalidOperationException){}host?.Dispose();
            lock(surfaceGate){Native.UW_Close(id);if(ownerId!=id)Native.UW_Close(ownerId);}
        }
    }
}
