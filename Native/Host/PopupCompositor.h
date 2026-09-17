#pragma once
#include <d3dcompiler.h>
// GPU-only popup composition, allocated only while a native popup is visible.
struct PopupCompositor {
    Microsoft::WRL::ComPtr<ID3D11Texture2D> base,popup,output;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixel;
    Microsoft::WRL::ComPtr<ID3D11Buffer> constants;
    bool visible=false;CefRect rect;
    void Reset(){base.Reset();popup.Reset();output.Reset();}
    HRESULT Keep(ID3D11Device* device,ID3D11DeviceContext* context,ID3D11Texture2D* source,Microsoft::WRL::ComPtr<ID3D11Texture2D>& target){
        D3D11_TEXTURE2D_DESC d{},old{};source->GetDesc(&d);if(target)target->GetDesc(&old);
        if(!target||old.Width!=d.Width||old.Height!=d.Height){target.Reset();d.MiscFlags=0;d.Usage=D3D11_USAGE_DEFAULT;d.CPUAccessFlags=0;d.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;HRESULT hr=device->CreateTexture2D(&d,nullptr,&target);if(FAILED(hr))return hr;}
        context->CopyResource(target.Get(),source);return S_OK;
    }
    ID3D11Texture2D* Compose(ID3D11Device* device,ID3D11DeviceContext* context){
        if(!base)return nullptr;if(!popup)return base.Get();
        if(!vertex){
            const char* code="Texture2D<float4> b:register(t0);Texture2D<float4> p:register(t1);cbuffer C:register(b0){int4 r;}float4 vs(uint i:SV_VertexID):SV_POSITION{return float4(i==2?3:-1,i==1?3:-1,0,1);}float4 ps(float4 v:SV_POSITION):SV_Target{int2 x=int2(v.xy);float4 c=b.Load(int3(x,0));int2 q=x-r.xy;if(all(q>=0)&&all(q<r.zw)){float4 a=p.Load(int3(q,0));c=a+c*(1-a.a);}return c;}";
            Microsoft::WRL::ComPtr<ID3DBlob> vs,ps,error;
            if(FAILED(D3DCompile(code,strlen(code),nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vs,&error))||FAILED(D3DCompile(code,strlen(code),nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&ps,&error)))return nullptr;
            if(FAILED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex))||FAILED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)))return nullptr;
            D3D11_BUFFER_DESC bd{};bd.ByteWidth=16;bd.Usage=D3D11_USAGE_DEFAULT;bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;if(FAILED(device->CreateBuffer(&bd,nullptr,&constants)))return nullptr;
        }
        D3D11_TEXTURE2D_DESC d{},old{};base->GetDesc(&d);if(output)output->GetDesc(&old);
        if(!output||d.Width!=old.Width||d.Height!=old.Height){output.Reset();if(FAILED(device->CreateTexture2D(&d,nullptr,&output)))return nullptr;}
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> rtv;Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> b,p;
        if(FAILED(device->CreateRenderTargetView(output.Get(),nullptr,&rtv))||FAILED(device->CreateShaderResourceView(base.Get(),nullptr,&b))||FAILED(device->CreateShaderResourceView(popup.Get(),nullptr,&p)))return nullptr;
        int bounds[4]={rect.x,rect.y,rect.width,rect.height};context->UpdateSubresource(constants.Get(),0,nullptr,bounds,0,0);
        D3D11_VIEWPORT vp{0,0,float(d.Width),float(d.Height),0,1};context->RSSetViewports(1,&vp);
        ID3D11RenderTargetView* target=rtv.Get();context->OMSetRenderTargets(1,&target,nullptr);context->OMSetBlendState(nullptr,nullptr,~0u);context->OMSetDepthStencilState(nullptr,0);
        context->IASetInputLayout(nullptr);context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(vertex.Get(),nullptr,0);context->PSSetShader(pixel.Get(),nullptr,0);ID3D11Buffer* cb=constants.Get();context->PSSetConstantBuffers(0,1,&cb);
        ID3D11ShaderResourceView* inputs[2]={b.Get(),p.Get()};context->PSSetShaderResources(0,2,inputs);context->Draw(3,0);
        inputs[0]=inputs[1]=nullptr;context->PSSetShaderResources(0,2,inputs);context->OMSetRenderTargets(0,nullptr,nullptr);return output.Get();
    }
};
