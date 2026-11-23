#pragma once

#include <chrono>
#include <condition_variable>
#include <dcomp.h>
#include <thread>

// Only the worker uses the immediate context and the DirectComposition objects.
// The pipe reader publishes slot IDs without waiting for the GPU or Unity.
class CompositionPresenter {
    static constexpr int SlotCount=3;
    struct Surface {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> mutex;
        HANDLE handle=nullptr;
        ~Surface(){if(handle)CloseHandle(handle);}
    };
    struct Pool {
        int width=0,height=0;
        Surface slots[SlotCount];
    };
    struct Frame {int slot;uint64_t sequence;};

    HWND window;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIFactory2> factory;
    ComPtr<IDCompositionDevice> composition;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual> visual;
    ComPtr<IDCompositionScaleTransform> scale;
    ComPtr<IDXGISwapChain1> swapChain;
    ComPtr<ID3D11Query> copyDone;
    LUID adapter{};
    SIZE clientSize{};
    bool attached=false;

    std::mutex gate;
    std::condition_variable wake;
    std::thread worker;
    std::shared_ptr<Pool> pool;
    std::vector<Frame> frames;
    bool stopping=false,visible=true,refresh=false;

    static bool IsPlayerWindow(HWND hwnd){
        DWORD pid=0;
        wchar_t name[64]{};
        GetWindowThreadProcessId(hwnd,&pid);
        return pid==GetCurrentProcessId()&&GetClassNameW(hwnd,name,64)&&wcscmp(name,L"UnityWndClass")==0;
    }

    HRESULT Initialize(){
        HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
        ComPtr<IDXGIDevice1> dxgi;
        ComPtr<IDXGIAdapter> dxgiAdapter;
        DXGI_ADAPTER_DESC desc{};
        if(FAILED(hr)||FAILED(hr=device.As(&dxgi))||FAILED(hr=dxgi->GetAdapter(&dxgiAdapter))||
            FAILED(hr=dxgiAdapter->GetDesc(&desc))||FAILED(hr=dxgiAdapter->GetParent(IID_PPV_ARGS(&factory))))return hr;
        adapter=desc.AdapterLuid;
        if(FAILED(hr=dxgi->SetMaximumFrameLatency(1))||
            FAILED(hr=DCompositionCreateDevice(dxgi.Get(),IID_PPV_ARGS(&composition)))||
            FAILED(hr=composition->CreateTargetForHwnd(window,TRUE,&target))||
            FAILED(hr=composition->CreateVisual(&visual))||
            FAILED(hr=composition->CreateScaleTransform(&scale))||
            FAILED(hr=visual->SetTransform(scale.Get()))||
            FAILED(hr=target->SetRoot(visual.Get())))return hr;
        D3D11_QUERY_DESC query{D3D11_QUERY_EVENT,0};
        return device->CreateQuery(&query,&copyDone);
    }

    HRESULT Configure(const Pool& next){
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width=next.width;
        desc.Height=next.height;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;
        desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount=2;
        desc.Scaling=DXGI_SCALING_STRETCH;
        desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode=DXGI_ALPHA_MODE_PREMULTIPLIED;
        swapChain.Reset();
        attached=false;
        clientSize={};
        return factory->CreateSwapChainForComposition(device.Get(),&desc,nullptr,&swapChain);
    }

    HRESULT Display(const Pool& current,bool show,bool force){
        RECT rect{};
        if(!GetClientRect(window,&rect))return HRESULT_FROM_WIN32(GetLastError());
        LONG width=std::max<LONG>(1,rect.right-rect.left);
        LONG height=std::max<LONG>(1,rect.bottom-rect.top);
        HRESULT hr=S_OK;
        const bool resized=width!=clientSize.cx||height!=clientSize.cy;
        if(resized){
            if(FAILED(hr=scale->SetScaleX(float(width)/current.width))||
                FAILED(hr=scale->SetScaleY(float(height)/current.height)))return hr;
            clientSize={width,height};
        }
        if(attached!=show||force){
            if(FAILED(hr=visual->SetContent(show?swapChain.Get():nullptr)))return hr;
            attached=show;
            return composition->Commit();
        }
        return resized?composition->Commit():S_OK;
    }

    HRESULT Consume(const Surface& slot,bool draw){
        HRESULT hr=slot.mutex->AcquireSync(1,100);
        if(hr!=S_OK)return FAILED(hr)?hr:DXGI_ERROR_DEVICE_HUNG;
        if(draw){
            ComPtr<ID3D11Texture2D> buffer;
            if(FAILED(hr=swapChain->GetBuffer(0,IID_PPV_ARGS(&buffer))))return hr;
            context->CopyResource(buffer.Get(),slot.texture.Get());
            context->End(copyDone.Get());
            context->Flush();
            const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(1);
            while((hr=context->GetData(copyDone.Get(),nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH))==S_FALSE){
                // The producer may reuse this slot only after the GPU read completes.
                if(std::chrono::steady_clock::now()>=deadline)return DXGI_ERROR_DEVICE_HUNG;
                SwitchToThread();
            }
            if(FAILED(hr))return hr;
        }
        return slot.mutex->ReleaseSync(0);
    }

    HRESULT PresentFrames(){
        std::shared_ptr<Pool> current;
        bool initialized=false;
        for(;;){
            std::shared_ptr<Pool> next;
            std::vector<Frame> incoming;
            bool show,reattach;
            {
                std::unique_lock<std::mutex> lock(gate);
                wake.wait_for(lock,std::chrono::milliseconds(50),[&]{
                    return stopping||pool!=current||!frames.empty()||refresh;
                });
                if(stopping)return S_OK;
                next=pool;
                incoming.swap(frames);
                show=visible;
                reattach=std::exchange(refresh,false);
            }
            if(!IsWindow(window))return E_HANDLE;
            if(!next)continue;
            HRESULT hr;
            if(next!=current){
                if(current&&FAILED(hr=Display(*current,false,false)))return hr;
                if(FAILED(hr=Configure(*next)))return hr;
                current=std::move(next);
                initialized=false;
            }
            show=show&&!IsIconic(window);
            const auto newest=std::max_element(incoming.begin(),incoming.end(),[](const Frame& a,const Frame& b){
                return a.sequence<b.sequence;
            });
            for(auto it=incoming.begin();it!=incoming.end();++it){
                if(it!=newest&&FAILED(hr=Consume(current->slots[it->slot],false)))return hr;
            }
            if(newest!=incoming.end()){
                if(FAILED(hr=Consume(current->slots[newest->slot],show)))return hr;
                if(show){
                    hr=swapChain->Present(1,0);
                    if(FAILED(hr))return hr;
                    if(hr==S_OK){initialized=true;++presented;}
                }
            }
            if(FAILED(hr=Display(*current,show&&initialized,reattach)))return hr;
            active=attached;
        }
    }

    void Run(){
        const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        HRESULT hr=com;
        if(SUCCEEDED(com))hr=PresentFrames();
        active=false;
        if(FAILED(hr))error=hr;
        visual->SetContent(nullptr);
        target->SetRoot(nullptr);
        composition->Commit();
        if(SUCCEEDED(com))CoUninitialize();
    }

public:
    std::atomic<HRESULT> error{S_OK};
    std::atomic<int> presented{0};
    std::atomic<bool> active{false};

    explicit CompositionPresenter(HWND hwnd):window(hwnd){}
    ~CompositionPresenter(){
        {
            std::lock_guard<std::mutex> lock(gate);
            stopping=true;
        }
        wake.notify_one();
        if(worker.joinable())worker.join();
    }

    static HWND FindPlayerWindow(){
        HWND hwnd=GetActiveWindow();
        if(IsPlayerWindow(hwnd))return hwnd;
        struct Search {
            HWND visible=nullptr,hidden=nullptr;
            bool multipleVisible=false,multipleHidden=false;
        } search;
        EnumWindows([](HWND candidate,LPARAM state)->BOOL{
            if(!IsPlayerWindow(candidate))return TRUE;
            auto& search=*reinterpret_cast<Search*>(state);
            if(IsWindowVisible(candidate)){
                search.multipleVisible=search.visible!=nullptr;
                search.visible=candidate;
                if(search.multipleVisible)return FALSE;
            }else{
                search.multipleHidden=search.hidden!=nullptr;
                search.hidden=candidate;
            }
            return TRUE;
        },reinterpret_cast<LPARAM>(&search));
        if(search.visible)return search.multipleVisible?nullptr:search.visible;
        return search.multipleHidden?nullptr:search.hidden;
    }

    HRESULT Start(int width,int height){
        HRESULT hr=Initialize();
        if(FAILED(hr)||FAILED(hr=Resize(width,height)))return hr;
        try{worker=std::thread([this]{Run();});}
        catch(const std::system_error&){return E_FAIL;}
        return S_OK;
    }

    LUID Adapter() const{return adapter;}

    HRESULT Resize(int width,int height){
        auto replacement=std::make_shared<Pool>();
        replacement->width=width;
        replacement->height=height;
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width=width;
        desc.Height=height;
        desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags=D3D11_RESOURCE_MISC_SHARED_NTHANDLE|D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        for(auto& slot:replacement->slots){
            ComPtr<IDXGIResource1> resource;
            HRESULT hr;
            if(FAILED(hr=device->CreateTexture2D(&desc,nullptr,&slot.texture))||
                FAILED(hr=slot.texture.As(&slot.mutex))||FAILED(hr=slot.texture.As(&resource))||
                FAILED(hr=resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ|DXGI_SHARED_RESOURCE_WRITE,
                    nullptr,&slot.handle)))return hr;
        }
        {
            std::lock_guard<std::mutex> lock(gate);
            pool=std::move(replacement);
            frames.clear();
        }
        wake.notify_one();
        return S_OK;
    }

    BOOL Duplicate(HANDLE child,int slot,HANDLE* result){
        std::lock_guard<std::mutex> lock(gate);
        return DuplicateHandle(GetCurrentProcess(),pool->slots[slot].handle,child,result,0,FALSE,DUPLICATE_SAME_ACCESS);
    }

    void Submit(int slot,uint64_t sequence){
        {
            std::lock_guard<std::mutex> lock(gate);
            frames.push_back({slot,sequence});
        }
        wake.notify_one();
    }

    void SetVisible(bool show){
        {
            std::lock_guard<std::mutex> lock(gate);
            visible=show;
            refresh=true;
        }
        wake.notify_one();
    }
};
