// QuadViews Gaze Mirror 2.0 - core: the one draw per frame.
//
// Samples the eye image where it is (a view of it, no copy), cropped and scaled, and composites the ring in the same
// pixel shader - all eight looks of 1.x, with "screen" or "normal" blending done in the shader. The result lands in a
// shared slot texture. Everything is recorded on a deferred context and played back with "restore state", so the
// game's (or the helper's) own Direct3D state is never disturbed.
#pragma once

#include "framing.h"
#include "publisher.h"
#include "ring.h"

namespace gaze_mirror {

    struct SourceImage {
        ID3D11Texture2D* texture = nullptr;
        uint32_t arraySlice = 0;
        DXGI_FORMAT viewFormat = DXGI_FORMAT_UNKNOWN; // The format to read it as (a swapchain's; the texture may be typeless).
        int32_t x = 0, y = 0;                         // The eye image inside the texture.
        int32_t width = 0, height = 0;
        bool linearLight = false;                     // sRGB / float formats: sampled values are linear.
    };

    class Renderer {
      public:
        ~Renderer();

        bool start(ID3D11Device* device);
        void stop();

        // Draws one frame: source -> crop -> scale -> ring -> slot. ring may be null (nothing drawn).
        bool render(Publisher& publisher, const SourceImage& source, const CropRect& crop, uint32_t outWidth, uint32_t outHeight,
                    const RingConstants* ring, int eye);

        // A small box-filtered copy of the WHOLE eye image, read back to memory (BGRA, top row first) - the settings
        // app's crop tool. Slow (a GPU round trip): only on request.
        bool snapshot(const SourceImage& source, uint32_t maxSide, std::vector<uint8_t>& pixels, uint32_t& width, uint32_t& height);

        // These textures are going away: let go of every view of them.
        void forget(const std::vector<ID3D11Texture2D*>& textures);
        void forgetAll();

      private:
        struct Constants {
            float sourceRect[4]; // u0, v0, du, dv in the source texture.
            float output[4];     // width / height; source is linear light; ring on; unused.
            float bounds[4];     // The output uv rectangle the ring can touch.
            RingConstants ring;
        };
        ID3D11ShaderResourceView* viewOf(const SourceImage& source, const CropRect& crop, float rect[4]);
        bool createPipeline();

        Microsoft::WRL::ComPtr<ID3D11Device> _device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> _immediate;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> _deferred;
        Microsoft::WRL::ComPtr<ID3D11VertexShader> _vertexShader;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> _pixelShader;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> _snapshotShader;
        Microsoft::WRL::ComPtr<ID3D11SamplerState> _sampler;
        Microsoft::WRL::ComPtr<ID3D11Buffer> _constants;
        Microsoft::WRL::ComPtr<ID3D11RasterizerState> _rasterizer;
        Microsoft::WRL::ComPtr<ID3D11BlendState> _blend;
        Microsoft::WRL::ComPtr<ID3D11DepthStencilState> _depth;
        std::map<std::pair<ID3D11Texture2D*, uint32_t>, Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>> _views;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> _copy;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> _copyView;
        uint32_t _copyWidth = 0, _copyHeight = 0;
        DXGI_FORMAT _copyFormat = DXGI_FORMAT_UNKNOWN;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> _snapshotTarget, _snapshotStaging;
        Microsoft::WRL::ComPtr<ID3D11RenderTargetView> _snapshotView;
        int _logProblem = 0;
        int _logRender = 0;
    };

} // namespace gaze_mirror
