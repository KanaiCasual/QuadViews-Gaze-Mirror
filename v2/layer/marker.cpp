#include "pch.h"

#include "../core/log.h"
#include "marker.h"

using Microsoft::WRL::ComPtr;

namespace gaze_mirror::layer {

    namespace {
        const char MarkerShader[] = R"(
cbuffer C : register(b0) { float2 center; float aspect; float radius; float4 color; };
struct V { float4 p : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID) {
    V o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.p = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 ps(V i) : SV_Target {
    float d = length((i.uv - center) * float2(aspect, 1));
    float ring = 1 - smoothstep(0.0008, 0.0016, abs(d - radius));
    float cross = max(1 - smoothstep(0.0006, 0.0012, min(abs(i.uv.x - center.x) * aspect, abs(i.uv.y - center.y))), 0) * step(d, radius * 0.6);
    float a = max(ring, cross) * 0.9;
    return float4(color.rgb, a);
}
)";
    }

    bool Marker::start(ID3D11Device* device) {
        stop();
        _device = device;
        _device->GetImmediateContext(&_immediate);
        if (FAILED(_device->CreateDeferredContext(0, &_deferred))) return false;
        ComPtr<ID3DBlob> vertexCode, pixelCode, messages;
        if (FAILED(D3DCompile(MarkerShader, sizeof(MarkerShader) - 1, nullptr, nullptr, nullptr, "vs", "vs_5_0", 0, 0, &vertexCode, &messages)) ||
            FAILED(D3DCompile(MarkerShader, sizeof(MarkerShader) - 1, nullptr, nullptr, nullptr, "ps", "ps_5_0", 0, 0, &pixelCode, &messages))) {
            Log("marker: shader did not compile: %s", messages ? static_cast<const char*>(messages->GetBufferPointer()) : "?");
            return false;
        }
        D3D11_BUFFER_DESC buffer{};
        buffer.ByteWidth = sizeof(Constants);
        buffer.Usage = D3D11_USAGE_DEFAULT;
        buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        D3D11_BLEND_DESC blend{};
        blend.RenderTarget[0].BlendEnable = TRUE;
        blend.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
        blend.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        blend.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
        blend.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ZERO;
        blend.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ONE;
        blend.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
        blend.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_RED | D3D11_COLOR_WRITE_ENABLE_GREEN | D3D11_COLOR_WRITE_ENABLE_BLUE;
        D3D11_RASTERIZER_DESC rasterizer{};
        rasterizer.FillMode = D3D11_FILL_SOLID;
        rasterizer.CullMode = D3D11_CULL_NONE;
        rasterizer.DepthClipEnable = TRUE;
        D3D11_DEPTH_STENCIL_DESC depth{};
        const bool made =
            SUCCEEDED(_device->CreateVertexShader(vertexCode->GetBufferPointer(), vertexCode->GetBufferSize(), nullptr, &_vertexShader)) &&
            SUCCEEDED(_device->CreatePixelShader(pixelCode->GetBufferPointer(), pixelCode->GetBufferSize(), nullptr, &_pixelShader)) &&
            SUCCEEDED(_device->CreateBuffer(&buffer, nullptr, &_constants)) && SUCCEEDED(_device->CreateBlendState(&blend, &_blend)) &&
            SUCCEEDED(_device->CreateRasterizerState(&rasterizer, &_rasterizer)) && SUCCEEDED(_device->CreateDepthStencilState(&depth, &_depth));
        if (!made) stop();
        return made;
    }

    void Marker::stop() {
        _targets.clear();
        _vertexShader.Reset();
        _pixelShader.Reset();
        _constants.Reset();
        _blend.Reset();
        _rasterizer.Reset();
        _depth.Reset();
        _deferred.Reset();
        _immediate.Reset();
        _device.Reset();
    }

    void Marker::draw(const SourceImage& image, const float ndc[2], float radius, const float color[3]) {
        if (!_device || !image.texture) return;
        const auto key = std::make_pair(image.texture, image.arraySlice);
        auto found = _targets.find(key);
        if (found == _targets.end()) {
            D3D11_TEXTURE2D_DESC desc;
            image.texture->GetDesc(&desc);
            ComPtr<ID3D11RenderTargetView> target;
            if (desc.BindFlags & D3D11_BIND_RENDER_TARGET) {
                D3D11_RENDER_TARGET_VIEW_DESC viewDesc{};
                viewDesc.Format = image.viewFormat;
                viewDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2DARRAY;
                viewDesc.Texture2DArray.FirstArraySlice = image.arraySlice;
                viewDesc.Texture2DArray.ArraySize = 1;
                if (FAILED(_device->CreateRenderTargetView(image.texture, &viewDesc, &target))) {
                    viewDesc.Format = desc.Format;
                    _device->CreateRenderTargetView(image.texture, &viewDesc, &target);
                }
            }
            if (!target) LogFewTimes(_logProblem, 3, "marker: the headset image cannot be drawn into (bind flags 0x%x)", desc.BindFlags);
            found = _targets.emplace(key, target).first;
        }
        if (!found->second) return;

        Constants constants{};
        constants.center[0] = (ndc[0] + 1.f) * 0.5f;
        constants.center[1] = (1.f - ndc[1]) * 0.5f;
        constants.aspect = float(image.width) / float(image.height);
        constants.radius = radius;
        for (int i = 0; i < 3; i++) constants.color[i] = image.linearLight ? powf(color[i], 2.2f) : color[i];
        constants.color[3] = 1.f;
        const D3D11_VIEWPORT viewport{float(image.x), float(image.y), float(image.width), float(image.height), 0, 1};
        _deferred->UpdateSubresource(_constants.Get(), 0, nullptr, &constants, 0, 0);
        _deferred->OMSetRenderTargets(1, found->second.GetAddressOf(), nullptr);
        _deferred->OMSetBlendState(_blend.Get(), nullptr, 0xFFFFFFFF);
        _deferred->OMSetDepthStencilState(_depth.Get(), 0);
        _deferred->RSSetState(_rasterizer.Get());
        _deferred->RSSetViewports(1, &viewport);
        _deferred->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        _deferred->IASetInputLayout(nullptr);
        _deferred->VSSetShader(_vertexShader.Get(), nullptr, 0);
        _deferred->PSSetShader(_pixelShader.Get(), nullptr, 0);
        _deferred->PSSetConstantBuffers(0, 1, _constants.GetAddressOf());
        _deferred->Draw(3, 0);
        ComPtr<ID3D11CommandList> commands;
        if (SUCCEEDED(_deferred->FinishCommandList(FALSE, &commands))) _immediate->ExecuteCommandList(commands.Get(), TRUE);
    }

} // namespace gaze_mirror::layer
