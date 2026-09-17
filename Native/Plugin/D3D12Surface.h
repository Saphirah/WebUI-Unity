#pragma once
#include <d3d12.h>
#include "../UnityAPI/IUnityGraphics.h"
#include "../UnityAPI/IUnityGraphicsD3D12.h"
static IUnityGraphicsD3D12v7* UnityD12=nullptr;
struct D12Surface {
    ComPtr<ID3D12Device> device;
    ComPtr<ID3D12Resource> output,inputs[3];
    ComPtr<ID3D12CommandAllocator> allocators[3];
    ComPtr<ID3D12GraphicsCommandList> commands[3];
    ComPtr<ID3D12DescriptorHeap> descriptors;
    ComPtr<ID3D12RootSignature> root;
    ComPtr<ID3D12PipelineState> pipeline;
    ComPtr<ID3D12Fence> fence;
    UINT64 submitted[3]{};
    UINT stride=0;int width=0,height=0;
    bool Complete() const {for(auto value:submitted)if(value&&(!fence||fence->GetCompletedValue()<value))return false;return true;}
    HRESULT Create(ID3D12Resource* bootstrap,HANDLE* handles){
        if(!UnityD12)return E_NOINTERFACE;
        HRESULT hr=bootstrap->GetDevice(IID_PPV_ARGS(&device));if(FAILED(hr))return hr;
        auto desc=bootstrap->GetDesc();width=int(desc.Width);height=int(desc.Height);
        desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.Flags=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        D3D12_HEAP_PROPERTIES heap{};heap.Type=D3D12_HEAP_TYPE_DEFAULT;
        if(FAILED(hr=device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&desc,D3D12_RESOURCE_STATE_COMMON,nullptr,IID_PPV_ARGS(&output))))return hr;
        D3D12_DESCRIPTOR_HEAP_DESC hd{};hd.Type=D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;hd.NumDescriptors=4;hd.Flags=D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        if(FAILED(hr=device->CreateDescriptorHeap(&hd,IID_PPV_ARGS(&descriptors))))return hr;
        stride=device->GetDescriptorHandleIncrementSize(hd.Type);auto cpu=descriptors->GetCPUDescriptorHandleForHeapStart();
        desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.Flags=D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS;
        for(int i=0;i<3;i++){
            if(FAILED(hr=device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_SHARED,&desc,D3D12_RESOURCE_STATE_COMMON,nullptr,IID_PPV_ARGS(&inputs[i]))))return hr;
            if(FAILED(hr=device->CreateSharedHandle(inputs[i].Get(),nullptr,GENERIC_ALL,nullptr,&handles[i])))return hr;
            D3D12_SHADER_RESOURCE_VIEW_DESC srv{};srv.Format=desc.Format;srv.ViewDimension=D3D12_SRV_DIMENSION_TEXTURE2D;srv.Shader4ComponentMapping=D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;srv.Texture2D.MipLevels=1;
            device->CreateShaderResourceView(inputs[i].Get(),&srv,cpu);cpu.ptr+=stride;
            if(FAILED(hr=device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&allocators[i]))))return hr;
            if(FAILED(hr=device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,allocators[i].Get(),nullptr,IID_PPV_ARGS(&commands[i]))))return hr;
            commands[i]->Close();
        }
        D3D12_UNORDERED_ACCESS_VIEW_DESC uav{};uav.Format=DXGI_FORMAT_R8G8B8A8_UNORM;uav.ViewDimension=D3D12_UAV_DIMENSION_TEXTURE2D;device->CreateUnorderedAccessView(output.Get(),nullptr,&uav,cpu);
        D3D12_DESCRIPTOR_RANGE ranges[2]{};D3D12_ROOT_PARAMETER parameters[2]{};
        for(int i=0;i<2;i++){ranges[i].RangeType=i?D3D12_DESCRIPTOR_RANGE_TYPE_UAV:D3D12_DESCRIPTOR_RANGE_TYPE_SRV;ranges[i].NumDescriptors=1;
            parameters[i].ParameterType=D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;parameters[i].DescriptorTable.NumDescriptorRanges=1;parameters[i].DescriptorTable.pDescriptorRanges=&ranges[i];}
        D3D12_ROOT_SIGNATURE_DESC rd{};rd.NumParameters=2;rd.pParameters=parameters;
        ComPtr<ID3DBlob> signature,errors,code;
        if(FAILED(hr=D3D12SerializeRootSignature(&rd,D3D_ROOT_SIGNATURE_VERSION_1,&signature,&errors)))return hr;
        if(FAILED(hr=device->CreateRootSignature(0,signature->GetBufferPointer(),signature->GetBufferSize(),IID_PPV_ARGS(&root))))return hr;
        const char* shader="Texture2D<float4> source:register(t0);RWTexture2D<float4> target:register(u0);[numthreads(8,8,1)]void main(uint3 p:SV_DispatchThreadID){uint w,h;source.GetDimensions(w,h);if(p.x<w&&p.y<h)target[p.xy]=source.Load(int3(p.xy,0));}";
        if(FAILED(hr=D3DCompile(shader,strlen(shader),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&errors)))return hr;
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd{};pd.pRootSignature=root.Get();pd.CS={code->GetBufferPointer(),code->GetBufferSize()};
        return device->CreateComputePipelineState(&pd,IID_PPV_ARGS(&pipeline));
    }
    HRESULT Submit(int slot){
        if(!UnityD12)return E_FAIL;fence=UnityD12->GetFrameFence();
        auto command=commands[slot].Get();HRESULT hr=allocators[slot]->Reset();if(FAILED(hr))return hr;
        if(FAILED(hr=command->Reset(allocators[slot].Get(),pipeline.Get())))return hr;
        D3D12_RESOURCE_BARRIER barrier{};barrier.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition={inputs[slot].Get(),D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,D3D12_RESOURCE_STATE_COMMON,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE};command->ResourceBarrier(1,&barrier);
        command->SetComputeRootSignature(root.Get());ID3D12DescriptorHeap* heaps[]={descriptors.Get()};command->SetDescriptorHeaps(1,heaps);
        auto gpu=descriptors->GetGPUDescriptorHandleForHeapStart();gpu.ptr+=UINT64(slot)*stride;command->SetComputeRootDescriptorTable(0,gpu);
        gpu=descriptors->GetGPUDescriptorHandleForHeapStart();gpu.ptr+=UINT64(3)*stride;command->SetComputeRootDescriptorTable(1,gpu);
        command->Dispatch((width+7)/8,(height+7)/8,1);
        std::swap(barrier.Transition.StateBefore,barrier.Transition.StateAfter);command->ResourceBarrier(1,&barrier);
        barrier.Transition={output.Get(),D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE};command->ResourceBarrier(1,&barrier);
        if(FAILED(hr=command->Close()))return hr;
        UnityGraphicsD3D12ResourceState state{output.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE};
        submitted[slot]=UnityD12->ExecuteCommandList(command,1,&state);return S_OK;
    }
};
API void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces){UnityD12=interfaces->Get<IUnityGraphicsD3D12v7>();}
API void UNITY_INTERFACE_API UnityPluginUnload(){UnityD12=nullptr;}
