#define NOMINMAX
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <map>
#include <memory>
#include <mutex>
#include <deque>
#include <algorithm>
#include <atomic>
#include <sddl.h>
#include <string>
#include <vector>
#include "../Shared/CpuFrames.h"
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
#include "D3D12Surface.h"
struct Slot {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<IDXGIKeyedMutex> mutex;
    ComPtr<ID3D11Query> done;
    ComPtr<ID3D11ShaderResourceView> view;
    HANDLE handle=nullptr;
    uint64_t sequence=0;
    bool ready=false,retiring=false;
    ~Slot(){if(handle)CloseHandle(handle);}
};
struct Session {
    std::unique_ptr<D12Surface> d12;
    std::mutex gate;
    int width=0,height=0;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> output;
    ComPtr<ID3D11ShaderResourceView> view;
    ComPtr<ID3D11UnorderedAccessView> uav;
    ComPtr<ID3D11ComputeShader> convert;
    ComPtr<ID3D11DeviceContext1> context1;
    ComPtr<ID3DDeviceContextState> state;
    Slot slots[3];
    std::deque<int> released;
    HANDLE mapping=nullptr;void* pixels=nullptr;
    HANDLE job=nullptr;
    std::atomic<int> copies{0};
    std::atomic<bool> probe{false};std::atomic<int> alpha{-1};
    std::atomic<int> error{0};
    ~Session(){if(job)CloseHandle(job);if(pixels)UnmapViewOfFile(pixels);if(mapping)CloseHandle(mapping);}
};
static std::mutex RegistryGate;
static std::map<int,std::shared_ptr<Session>> Sessions;
static std::vector<std::shared_ptr<Session>> Retired;
static int Next=0;
static std::atomic<int> CreateError{0};
API int __cdecl UW_CreateError(){return CreateError.load();}
// Unity Mono does not implement WindowsIdentity.Owner used by CurrentUserOnly.
// Create the equivalent owner-only, remote-rejecting pipe in Win32 instead.
API HANDLE __cdecl UW_CreatePipe(const wchar_t* name){
    HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return INVALID_HANDLE_VALUE;
    DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<BYTE> user(bytes);
    if(!GetTokenInformation(token,TokenUser,user.data(),bytes,&bytes)){CloseHandle(token);return INVALID_HANDLE_VALUE;}CloseHandle(token);
    LPWSTR sid=nullptr;if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid,&sid))return INVALID_HANDLE_VALUE;
    std::wstring sddl=L"D:P(A;;GA;;;"+std::wstring(sid)+L")";LocalFree(sid);PSECURITY_DESCRIPTOR descriptor=nullptr;
    if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&descriptor,nullptr))return INVALID_HANDLE_VALUE;
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES),descriptor,FALSE};
    HANDLE pipe=CreateNamedPipeW(name,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,0,&security);
    LocalFree(descriptor);return pipe;
}
static std::shared_ptr<Session> Find(int id){std::lock_guard<std::mutex> lock(RegistryGate);auto i=Sessions.find(id);return i==Sessions.end()?nullptr:i->second;}
static int Save(std::shared_ptr<Session> s){std::lock_guard<std::mutex> lock(RegistryGate);int id=++Next;Sessions.emplace(id,s);return id;}
static bool SizeOK(int w,int h){return w>0&&h>0&&uint64_t(w)*uint64_t(h)<=uint64_t(INT_MAX)/4;}
API int __cdecl UW_CreateGPU12(void* texture){
    if(!texture)return 0;auto s=std::make_shared<Session>();s->d12=std::make_unique<D12Surface>();
    auto desc=static_cast<ID3D12Resource*>(texture)->GetDesc();if(!SizeOK(int(desc.Width),int(desc.Height)))return 0;
    HANDLE handles[3]{};HRESULT hr=s->d12->Create(static_cast<ID3D12Resource*>(texture),handles);
    for(int i=0;i<3;i++)s->slots[i].handle=handles[i];if(FAILED(hr)){CreateError=hr;return 0;}
    s->width=s->d12->width;s->height=s->d12->height;int id=Save(s);
    UnityD3D12PluginEventConfig config{kUnityD3D12GraphicsQueueAccess_Allow,kUnityD3D12EventConfigFlag_FlushCommandBuffers|kUnityD3D12EventConfigFlag_SyncWorkerThreads,false};
    UnityD12->ConfigureEvent(id,&config);return id;
}
API int __cdecl UW_CreateGPU(void* texture){
    if(!texture)return 0;
    auto s=std::make_shared<Session>();s->output=static_cast<ID3D11Texture2D*>(texture);
    D3D11_TEXTURE2D_DESC d{};s->output->GetDesc(&d);
    CreateError=0;
    if(!SizeOK(d.Width,d.Height)||d.SampleDesc.Count!=1||d.MipLevels!=1){CreateError=int(d.Format);return 0;}
    s->width=d.Width;s->height=d.Height;s->output->GetDevice(&s->device);s->device->GetImmediateContext(&s->context);
    d.Format=DXGI_FORMAT_R8G8B8A8_UNORM;d.Usage=D3D11_USAGE_DEFAULT;d.CPUAccessFlags=0;d.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS;
    d.MiscFlags=0;ComPtr<ID3D11Texture2D> external;
    HRESULT outputResult=s->device->CreateTexture2D(&d,nullptr,&external);if(FAILED(outputResult)){CreateError=outputResult;return 0;}
    s->output=external;
    HRESULT viewResult=s->device->CreateShaderResourceView(s->output.Get(),nullptr,&s->view);if(FAILED(viewResult)){CreateError=viewResult;return 0;}
    if(FAILED(s->device->CreateUnorderedAccessView(s->output.Get(),nullptr,&s->uav)))return 0;
    const char* shader="Texture2D<float4> source:register(t0);RWTexture2D<float4> target:register(u0);[numthreads(8,8,1)]void main(uint3 p:SV_DispatchThreadID){uint w,h;source.GetDimensions(w,h);if(p.x<w&&p.y<h)target[p.xy]=source.Load(int3(p.xy,0));}";
    ComPtr<ID3DBlob> code,errors;
    if(FAILED(D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors))||FAILED(s->device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&s->convert)))return 0;
    ComPtr<ID3D11Device1> device1;D3D_FEATURE_LEVEL level=s->device->GetFeatureLevel(),chosen;
    if(FAILED(s->device.As(&device1))||FAILED(s->context.As(&s->context1))||FAILED(device1->CreateDeviceContextState(0,&level,1,D3D11_SDK_VERSION,__uuidof(ID3D11Device),&chosen,&s->state)))return 0;
    d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    d.MiscFlags=D3D11_RESOURCE_MISC_SHARED_NTHANDLE|D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    for(auto& slot:s->slots){
        HRESULT hr=s->device->CreateTexture2D(&d,nullptr,&slot.texture);if(FAILED(hr)){CreateError=hr;return 0;}
        ComPtr<IDXGIResource1> resource;
        if(FAILED(hr=slot.texture.As(&resource))||FAILED(hr=resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,nullptr,&slot.handle))||FAILED(hr=slot.texture.As(&slot.mutex))){CreateError=hr;return 0;}
        if(FAILED(s->device->CreateShaderResourceView(slot.texture.Get(),nullptr,&slot.view)))return 0;
        D3D11_QUERY_DESC q{D3D11_QUERY_EVENT,0};if(FAILED(s->device->CreateQuery(&q,&slot.done)))return 0;
    }
    return Save(s);
}
API int __cdecl UW_CreateCPU(int width,int height){
    if(!SizeOK(width,height))return 0;auto s=std::make_shared<Session>();s->width=width;s->height=height;
    const uint64_t bytes=CpuStride(width,height)*3;
    s->mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,DWORD(bytes>>32),DWORD(bytes),nullptr);
    if(!s->mapping)return 0;s->pixels=MapViewOfFile(s->mapping,FILE_MAP_ALL_ACCESS,0,0,size_t(bytes));
    return s->pixels?Save(s):0;
}
API void* __cdecl UW_Output(int id){auto s=Find(id);return s?(s->d12?static_cast<void*>(s->d12->output.Get()):static_cast<void*>(s->output.Get())):nullptr;}
API int __cdecl UW_Duplicate(int id,int processId,int slot,uint64_t* result){
    auto s=Find(id);if(!s||!result||slot<0||slot>2)return 0;
    HANDLE child=OpenProcess(PROCESS_DUP_HANDLE,FALSE,processId);if(!child)return 0;
    HANDLE out=nullptr;BOOL ok=DuplicateHandle(GetCurrentProcess(),s->pixels?s->mapping:s->slots[slot].handle,child,&out,0,FALSE,DUPLICATE_SAME_ACCESS);CloseHandle(child);
    *result=reinterpret_cast<uint64_t>(out);return ok?1:0;
}
API int __cdecl UW_TrackProcess(int id,int processId){
    auto s=Find(id);if(!s)return 0;s->job=CreateJobObjectW(nullptr,nullptr);if(!s->job)return 0;
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if(!SetInformationJobObject(s->job,JobObjectExtendedLimitInformation,&limits,sizeof(limits)))return 0;
    HANDLE process=OpenProcess(PROCESS_SET_QUOTA|PROCESS_TERMINATE,FALSE,processId);if(!process)return 0;
    BOOL ok=AssignProcessToJobObject(s->job,process);CloseHandle(process);return ok?1:0;
}
API int __cdecl UW_Adapter(int id,uint32_t* high,uint32_t* low){
    auto s=Find(id);if(!s||!high||!low)return 0;
    if(s->d12){auto luid=s->d12->device->GetAdapterLuid();*high=uint32_t(luid.HighPart);*low=luid.LowPart;return 1;}
    if(!s->device)return 0;
    ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> a;DXGI_ADAPTER_DESC d{};
    if(FAILED(s->device.As(&dxgi))||FAILED(dxgi->GetAdapter(&a))||FAILED(a->GetDesc(&d)))return 0;
    *high=uint32_t(d.AdapterLuid.HighPart);*low=d.AdapterLuid.LowPart;return 1;
}
API void __cdecl UW_Frame(int id,int slot,uint64_t sequence){
    auto s=Find(id);if(!s||slot<0||slot>2||s->pixels)return;
    std::lock_guard<std::mutex> lock(s->gate);auto& q=s->slots[slot];if(q.retiring||q.ready)return;q.ready=true;q.sequence=sequence;
}
API int __cdecl UW_Release(int id){
    auto s=Find(id);if(!s)return -1;std::lock_guard<std::mutex> lock(s->gate);
    if(s->released.empty())return -1;int slot=s->released.front();s->released.pop_front();return slot;
}
// Unity render-thread callback, also directly exercised by the native GPU test.
API void __cdecl UW_Render(int id){
    {std::lock_guard<std::mutex> lock(RegistryGate);Retired.erase(std::remove_if(Retired.begin(),Retired.end(),[](const auto& p){return p->d12->Complete();}),Retired.end());}
    auto s=Find(id);if(!s)return;
    std::unique_lock<std::mutex> lock(s->gate,std::try_to_lock);if(!lock.owns_lock())return;
    if(s->d12){
        auto& d=*s->d12;int newest=-1;
        for(int i=0;i<3;i++){auto& q=s->slots[i];if(q.retiring&&d.fence&&d.fence->GetCompletedValue()>=d.submitted[i]){q.retiring=false;s->released.push_back(i);}
            if(q.ready&&(newest<0||q.sequence>s->slots[newest].sequence))newest=i;}
        for(int i=0;i<3;i++){auto& q=s->slots[i];if(!q.ready)continue;q.ready=false;
            if(i!=newest){s->released.push_back(i);continue;}
            if(FAILED(d.Submit(i))){s->error=3;return;}q.retiring=true;++s->copies;}
        if(s->probe.exchange(false))s->alpha=-2; // D3D12 output is read back by Unity's validation API.
        return;
    }
    if(!s->context)return;
    for(int i=0;i<3;++i){auto& q=s->slots[i];if(q.retiring){
        HRESULT hr=s->context->GetData(q.done.Get(),nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH);
        if(hr==S_OK){q.mutex->ReleaseSync(0);q.retiring=false;s->released.push_back(i);}
        else if(FAILED(hr))s->error=1;
    }}
    int newest=-1;for(int i=0;i<3;++i)if(s->slots[i].ready&&(newest<0||s->slots[i].sequence>s->slots[newest].sequence))newest=i;
    for(int i=0;i<3;++i){auto& q=s->slots[i];if(!q.ready)continue;
        HRESULT acquired=q.mutex->AcquireSync(1,0);if(acquired!=S_OK){if(acquired!=WAIT_TIMEOUT)s->error=2;continue;}
        q.ready=false;
        if(i!=newest){q.mutex->ReleaseSync(0);s->released.push_back(i);continue;}
        // Decode BGRA through the typed SRV and write an RGBA texture Unity can import.
        // Context-state swapping preserves Unity's shader/resource bindings.
        ComPtr<ID3DDeviceContextState> previous;s->context1->SwapDeviceContextState(s->state.Get(),&previous);
        ID3D11ShaderResourceView* input=q.view.Get();ID3D11UnorderedAccessView* output=s->uav.Get();
        s->context->CSSetShader(s->convert.Get(),nullptr,0);s->context->CSSetShaderResources(0,1,&input);s->context->CSSetUnorderedAccessViews(0,1,&output,nullptr);
        s->context->Dispatch((s->width+7)/8,(s->height+7)/8,1);
        s->context1->SwapDeviceContextState(previous.Get(),nullptr);
        s->context->End(q.done.Get());q.retiring=true;
        ++s->copies;
        // Submit, never wait for completion. A later render callback polls the query.
        s->context->Flush();
    }
    if(s->probe.exchange(false)){
        D3D11_TEXTURE2D_DESC d{};s->output->GetDesc(&d);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;int count=-2;
        if(SUCCEEDED(s->device->CreateTexture2D(&d,nullptr,&staging))){s->context->CopyResource(staging.Get(),s->output.Get());D3D11_MAPPED_SUBRESOURCE map{};
            if(SUCCEEDED(s->context->Map(staging.Get(),0,D3D11_MAP_READ,0,&map))){count=0;for(int y=0;y<s->height;y++)for(int x=0;x<s->width;x++)if(static_cast<unsigned char*>(map.pData)[size_t(y)*map.RowPitch+x*4+3]>240)++count;s->context->Unmap(staging.Get(),0);}}
        s->alpha=count;
    }
}
API void __cdecl UW_RequestProbe(int id){auto s=Find(id);if(s)s->probe=true;}
API int __cdecl UW_ProbeAlpha(int id){auto s=Find(id);return s?s->alpha.load():-2;}
API void* __cdecl UW_GetRenderEvent(){return reinterpret_cast<void*>(&UW_Render);}
API int __cdecl UW_Error(int id){auto s=Find(id);return s?s->error.load():-1;}
API int __cdecl UW_Copies(int id){auto s=Find(id);return s?s->copies.load():0;}
API int __cdecl UW_ReadCPU(int id,void* destination,int capacity){
    auto s=Find(id);if(!s||!s->pixels||!destination||capacity<s->width*s->height*4)return 0;
    CpuFrame* newest=nullptr;CpuFrame* owned[3]{};
    for(int i=0;i<3;++i){auto q=CpuSlot(s->pixels,s->width,s->height,i);if(InterlockedCompareExchange(&q->state,3,2)==2){owned[i]=q;if(!newest||q->sequence>newest->sequence)newest=q;}}
    if(newest)memcpy(destination,newest+1,size_t(s->width)*s->height*4);
    for(auto q:owned)if(q)InterlockedExchange(&q->state,0);
    return newest?1:0;
}
API void __cdecl UW_Close(int id){std::lock_guard<std::mutex> lock(RegistryGate);auto it=Sessions.find(id);if(it!=Sessions.end()){if(it->second->d12&&!it->second->d12->Complete())Retired.push_back(it->second);Sessions.erase(it);}}
