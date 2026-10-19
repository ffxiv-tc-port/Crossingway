using Crossingway.Common;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Crossingway;

internal unsafe class SharedTextureHandler : IDisposable
{
	private readonly ID3D11ShaderResourceView* _view;
	private readonly ID3D11Texture2D* _texture;
	private readonly Vector2 _size;
	private readonly nint _textureId;

	// Shared memory mode fields
	private readonly bool _isSharedMemMode;
	private readonly CrossingwaySharedBuffer? _sharedBuffer;
	private readonly int _pixelBufferSize;
	private readonly byte[] _pixelDataBuffer; // Pre-allocated to avoid per-frame GC pressure

	public SharedTextureHandler(IntPtr handle)
	{
		_pixelDataBuffer = Array.Empty<byte>();
		ID3D11Device* device = DxHandler.Device;
		if (device == null)
		{
			throw new Exception("Device is null");
		}

		// Open the shared resource
		Guid texture2DGuid = typeof(ID3D11Texture2D).GUID;
		void* texturePtr;
		HRESULT hr = device->OpenSharedResource((HANDLE)handle, &texture2DGuid, &texturePtr);
		if (hr.FAILED)
		{
			throw new Exception($"Could not open shared resource: {hr}");
		}

		_texture = (ID3D11Texture2D*)texturePtr;

		// Get the texture description
		D3D11_TEXTURE2D_DESC texDesc;
		_texture->GetDesc(&texDesc);

		// Create the shader resource view
		D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = new()
		{
			Format = texDesc.Format, ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D, Texture2D = new D3D11_TEX2D_SRV {MostDetailedMip = 0, MipLevels = texDesc.MipLevels}
		};

		ID3D11ShaderResourceView* view;
		hr = device->CreateShaderResourceView((ID3D11Resource*)_texture, &srvDesc, &view);
		if (hr.FAILED)
		{
			_texture->Release();
			throw new Exception($"Could not create shader resource view: {hr}");
		}

		_view = view;
		_size = new Vector2(texDesc.Width, texDesc.Height);
		_textureId = (nint)_view;
	}

	public SharedTextureHandler(string sharedMemName, int width, int height)
	{
		_isSharedMemMode = true;
		_size = new Vector2(width, height);
		_pixelBufferSize = width * height * 4;
		_pixelDataBuffer = new byte[_pixelBufferSize];

		ID3D11Device* device = DxHandler.Device;
		if (device == null)
		{
			throw new Exception("Device is null");
		}

		// Open the renderer's shared memory buffer
		_sharedBuffer = new CrossingwaySharedBuffer(sharedMemName);

		// Create a local (non-shared) D3D11 texture to upload pixels into
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
			BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
			CPUAccessFlags = 0,
			MiscFlags = 0
		};

		ID3D11Texture2D* texture;
		HRESULT hr = device->CreateTexture2D(&desc, null, &texture);
		if (hr.FAILED)
		{
			_sharedBuffer.Dispose();
			throw new Exception($"Could not create local texture for shared memory: {hr}");
		}

		_texture = texture;

		D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = new()
		{
			Format = desc.Format,
			ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
			Texture2D = new D3D11_TEX2D_SRV { MostDetailedMip = 0, MipLevels = 1 }
		};

		ID3D11ShaderResourceView* view;
		hr = device->CreateShaderResourceView((ID3D11Resource*)_texture, &srvDesc, &view);
		if (hr.FAILED)
		{
			_texture->Release();
			_sharedBuffer.Dispose();
			throw new Exception($"Could not create SRV for shared memory texture: {hr}");
		}

		_view = view;
		_textureId = (nint)_view;
	}

	public void Dispose()
	{
		_view->Release();
		_texture->Release();
		_sharedBuffer?.Dispose();
	}

	public void Render()
	{
		if (_isSharedMemMode && _sharedBuffer != null)
		{
			// Read pixels from shared memory and upload to local texture
			_sharedBuffer.ReadPixels(_pixelDataBuffer);

			ID3D11DeviceContext* ctx;
			DxHandler.Device->GetImmediateContext(&ctx);
			fixed (byte* src = _pixelDataBuffer)
			{
				ctx->UpdateSubresource(
					(ID3D11Resource*)_texture, 0, null,
					src, (uint)((int)_size.X * 4), (uint)_pixelBufferSize);
			}
			ctx->Release();
		}

		ImGui.Image(new ImTextureID(_textureId), _size);
	}
}