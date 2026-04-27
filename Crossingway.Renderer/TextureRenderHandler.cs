using Crossingway.Common;
using Crossingway.Common.Ipc;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using System.Collections.Concurrent;
using Range = CefSharp.Structs.Range;
using Size = System.Drawing.Size;

namespace Crossingway.Renderer;

internal unsafe class TextureRenderHandler : IRenderHandler
{
	// CEF buffers are 32-bit BGRA
	private const byte _bytesPerPixel = 4;

	// TODO: replace with lockless implementation
	private readonly object _renderLock = new();

	// TODO: remove me
	private byte[] _alphaLookupBuffer = Array.Empty<byte>();
	private int _alphaLookupBufferHeight;
	private int _alphaLookupBufferWidth;

	private Cursor _cursor;

	// Transparent background click-through state
	private bool _cursorOnBackground;

	private ConcurrentBag<IntPtr> _obsoleteTextures = [];

	private Rect _popupRect;
	private ID3D11Texture2D* _popupTexture;
	private bool _popupVisible;
	private ID3D11Texture2D* _sharedTexture;

	private IntPtr _sharedTextureHandle = IntPtr.Zero;
	private ID3D11Texture2D* _viewTexture;

	// Shared memory mode (fallback when D3D11 MISC_SHARED not supported)
	private readonly bool _useSharedMemory;
	private CrossingwaySharedBuffer? _sharedBuffer;
	private string? _sharedBufferName;
	private int _sharedWidth;
	private int _sharedHeight;
	// 記錄 buffer 的實際容量（byte），用於 OnPaint 時的 bounds check
	private int _sharedBufferCapacity;
	private byte[] _popupCpuBuffer = Array.Empty<byte>();
	private int _popupCpuWidth;
	private int _popupCpuHeight;

	public TextureRenderHandler(Size size, Guid overlayGuid, bool useSharedMemory)
	{
		_useSharedMemory = useSharedMemory;
		if (useSharedMemory)
		{
			// 計算 DPI 對齊後的實體像素大小（CEF 實際渲染尺寸）
			// GetViewRect 使用 Ceiling(w/scale)，CEF 實際渲染為 Ceiling(w/scale)*scale
			// 若 buffer 只分配 w*h，會導致溢位，因此分配 DPI 對齊後的大小
			var (physW, physH) = ComputePhysicalSize(size);
			_sharedWidth = physW;
			_sharedHeight = physH;
			_sharedBufferCapacity = physW * physH * _bytesPerPixel;
			// 每次使用唯一名稱，避免插件端仍持有舊 MMF 句柄時，CreateNew 丟出 "File already exists"
			_sharedBufferName = $"cw_{overlayGuid:N}_{Guid.NewGuid():N}";
			_sharedBuffer = new CrossingwaySharedBuffer(_sharedBufferName, _sharedBufferCapacity);
		}
		else
		{
			_sharedTexture = BuildViewTexture(size, true);
			_viewTexture = BuildViewTexture(size, false);
		}
	}

	public IntPtr SharedTextureHandle
	{
		get
		{
			if (_useSharedMemory) return IntPtr.Zero;

			if (_sharedTextureHandle == IntPtr.Zero)
			{
				IDXGIResource* resource;
				Guid resourceGuid = typeof(IDXGIResource).GUID;
				HRESULT hr = ((IUnknown*)_sharedTexture)->QueryInterface(&resourceGuid, (void**)&resource);
				if (hr.SUCCEEDED)
				{
					HANDLE sharedHandle;
					resource->GetSharedHandle(&sharedHandle);
					_sharedTextureHandle = (IntPtr)sharedHandle.Value;
					resource->Release();
				}
			}

			return _sharedTextureHandle;
		}
	}

	public string? SharedMemoryName => _useSharedMemory ? _sharedBufferName : null;
	public int SharedMemoryWidth => _sharedWidth;
	public int SharedMemoryHeight => _sharedHeight;

	public event EventHandler<Cursor>? CursorChanged;

	public void Dispose()
	{
		if (_useSharedMemory)
		{
			_sharedBuffer?.Dispose();
			_sharedBuffer = null;
		}
		else
		{
			_sharedTexture->Release();
			_viewTexture->Release();
			if (_popupTexture != null)
			{
				_popupTexture->Release();
			}

			foreach (IntPtr texturePtr in _obsoleteTextures)
			{
				((ID3D11Texture2D*)texturePtr)->Release();
			}
		}
	}

	public Rect GetViewRect()
	{
		// There's a very small chance that OnPaint's cleanup will delete the current _sharedTexture midway through this function -
		// Try a few times just in case before failing out with an obviously-wrong value
		// hi adam
		// TODO: proper threading model instead of shitty hacks
		for (int i = 0; i < 5; i++)
		{
			try { return GetViewRectInternal(); }
			catch (NullReferenceException) { }
		}

		return new Rect(0, 0, 1, 1);
	}

	public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
	{
		// TODO: use this instead of manual texture copying
		throw new NotImplementedException();
	}

	public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
	{
		lock (_renderLock)
		{
			if (_useSharedMemory)
			{
				OnPaintSharedMemory(type, buffer, width, height);
				return;
			}

			ID3D11Texture2D* targetTexture = type switch
			{
				PaintElementType.View => _viewTexture,
				PaintElementType.Popup => _popupTexture,
				_ => throw new Exception($"Unknown paint type {type}")
			};

			if (targetTexture == null)
			{
				throw new Exception($"Target texture is null for paint type {type}");
			}

			// keep buffer to make alpha checks later on.
			// TODO: make this a back and front buffer to atomic swap them
			if (type == PaintElementType.View)
			{
				// check if lookup buffer is big enough
				int requiredBufferSize = width * height * _bytesPerPixel;
				_alphaLookupBufferWidth = width;
				_alphaLookupBufferHeight = height;
				if (_alphaLookupBuffer.Length < requiredBufferSize)
				{
					_alphaLookupBuffer = new byte[width * height * _bytesPerPixel];
				}

				fixed (void* dstBuffer = _alphaLookupBuffer)
				{
					Buffer.MemoryCopy(buffer.ToPointer(), dstBuffer, _alphaLookupBuffer.Length, requiredBufferSize);
				}
			}

			// Calculate offset multipliers for the current buffer
			int rowPitch = width * _bytesPerPixel;
			int depthPitch = rowPitch * height;

			// Build the destination region for the dirty rect that we'll draw to
			D3D11_TEXTURE2D_DESC texDesc;
			targetTexture->GetDesc(&texDesc);

			IntPtr sourceRegionPtr = buffer + (dirtyRect.X * _bytesPerPixel) + (dirtyRect.Y * rowPitch);
			D3D11_BOX destinationBox = new()
			{
				top = (uint)Math.Min(dirtyRect.Y, (int)texDesc.Height),
				bottom = (uint)Math.Min(dirtyRect.Y + dirtyRect.Height, (int)texDesc.Height),
				left = (uint)Math.Min(dirtyRect.X, (int)texDesc.Width),
				right = (uint)Math.Min(dirtyRect.X + dirtyRect.Width, (int)texDesc.Width),
				front = 0,
				back = 1
			};

			// Draw to the target
			ID3D11DeviceContext* context;
			DxHandler.Device->GetImmediateContext(&context);

			context->UpdateSubresource(
				(ID3D11Resource*)targetTexture,
				0,
				&destinationBox,
				sourceRegionPtr.ToPointer(),
				(uint)rowPitch,
				(uint)depthPitch);

			// composite final picture
			// draw view layer, first
			context->CopySubresourceRegion(
				(ID3D11Resource*)_sharedTexture,
				0,
				0,
				0,
				0,
				(ID3D11Resource*)_viewTexture,
				0,
				null);

			// draw popup layer if required
			if (_popupVisible && _popupTexture != null)
			{
				Point popupPos = DpiScaling.ScaleScreenPoint(_popupRect.X, _popupRect.Y);
				context->CopySubresourceRegion(
					(ID3D11Resource*)_sharedTexture,
					0,
					(uint)popupPos.X,
					(uint)popupPos.Y,
					0,
					(ID3D11Resource*)_popupTexture,
					0,
					null);
			}

			context->Flush();
			context->Release();

			// Rendering is complete, clean up any obsolete textures
			ConcurrentBag<IntPtr> textures = _obsoleteTextures;
			_obsoleteTextures = new ConcurrentBag<IntPtr>();
			foreach (IntPtr texPtr in textures)
			{
				((ID3D11Texture2D*)texPtr)->Release();
			}
		}
	}

	public void OnPopupShow(bool show)
	{
		_popupVisible = show;
	}

	public void OnPopupSize(Rect rect)
	{
		_popupRect = DpiScaling.ScaleScreenRect(rect);

		// Shared memory mode: popup compositing is handled by the CPU path (CpuCompositeToSharedBuffer).
		// No D3D11 popup texture is needed; just recording _popupRect is sufficient.
		if (_useSharedMemory) return;

		// I'm really not sure if this happens. If it does, frequently - will probably need 2x shared textures and some jazz.
		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		if (_popupRect.Width > texDesc.Width || _popupRect.Height > texDesc.Height)
		{
			Console.Error.WriteLine(
				$"Trying to build popup layer ({_popupRect.Width}x{_popupRect.Height}) larger than primary surface ({texDesc.Width}x{texDesc.Height}).");
		}

		// Get a reference to the old _sharedTexture, we'll make sure to assign a new _sharedTexture before disposing the old one.
		ID3D11Texture2D* oldTexture = _popupTexture;

		// Build a _sharedTexture for the new sized popup
		_popupTexture = BuildViewTexture(new Size(_popupRect.Width, _popupRect.Height), false);

		if (oldTexture != null)
		{
			oldTexture->Release();
		}
	}

	public ScreenInfo? GetScreenInfo()
	{
		return new ScreenInfo {DeviceScaleFactor = DpiScaling.GetDeviceScale()};
	}

	public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
	{
		screenX = viewX;
		screenY = viewY;

		return false;
	}

	public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
	{
	}

	public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds)
	{
	}

	public void OnCursorChange(IntPtr cursorPtr, CursorType type, CursorInfo customCursorInfo)
	{
		_cursor = EncodeCursor(type);

		// If we're on background, don't flag a cursor change
		if (!_cursorOnBackground) { CursorChanged?.Invoke(this, _cursor); }
	}

	public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
	{
		// Returning false to abort drag operations.
		return false;
	}

	public void UpdateDragCursor(DragOperationsMask operation)
	{
	}

	public void Resize(Size size)
	{
		lock (_renderLock)
		{
			if (_useSharedMemory)
			{
				_sharedBuffer?.Dispose();
				// 立即設為 null，避免建立新 buffer 失敗時仍存取已 Dispose 的舊物件
				_sharedBuffer = null;
				var (physW, physH) = ComputePhysicalSize(size);
				_sharedWidth = physW;
				_sharedHeight = physH;
				_sharedBufferCapacity = physW * physH * _bytesPerPixel;
				// 每次 Resize 使用新的唯一名稱，避免與插件端仍持有的舊 MMF 句柄衝突
				_sharedBufferName = $"cw_{Guid.NewGuid():N}";
				_sharedBuffer = new CrossingwaySharedBuffer(_sharedBufferName!, _sharedBufferCapacity);
				return;
			}

			// TODO: make this thread unsafe crap thread safe crap
			ID3D11Texture2D* oldTexture1 = _sharedTexture;
			ID3D11Texture2D* oldTexture2 = _viewTexture;
			_sharedTexture = BuildViewTexture(size, true);
			_viewTexture = BuildViewTexture(size, false);
			_obsoleteTextures.Add((IntPtr)oldTexture1);
			_obsoleteTextures.Add((IntPtr)oldTexture2);

			// Need to clear the cached handle value
			// TODO: Maybe I should just avoid the lazy cache and do it eagerly on _sharedTexture build.
			_sharedTextureHandle = IntPtr.Zero;
		}
	}

	protected byte GetAlphaAt(int x, int y)
	{
		lock (_renderLock)
		{
			// Buffer not yet populated (no OnPaint has fired); treat as opaque
			if (_alphaLookupBuffer.Length == 0) { return 255; }

			int rowPitch = _alphaLookupBufferWidth * _bytesPerPixel;

			// Get the offset for the alpha of the cursor's current position. Bitmap buffer is BGRA, so +3 to get alpha byte
			int cursorAlphaOffset = 0
			                        + (Math.Min(Math.Max(x, 0), _alphaLookupBufferWidth - 1) * _bytesPerPixel)
			                        + (Math.Min(Math.Max(y, 0), _alphaLookupBufferHeight - 1) * rowPitch)
			                        + 3;
			cursorAlphaOffset = cursorAlphaOffset < 0 ? 0 : cursorAlphaOffset;

			if (cursorAlphaOffset < _alphaLookupBuffer.Length)
			{
				return _alphaLookupBuffer[cursorAlphaOffset];
			}

			return 255;
		}
	}

	private void OnPaintSharedMemory(PaintElementType type, IntPtr buffer, int width, int height)
	{
		int bufferSize = width * height * _bytesPerPixel;

		if (type == PaintElementType.View)
		{
			// Update alpha lookup buffer
			_alphaLookupBufferWidth = width;
			_alphaLookupBufferHeight = height;
			if (_alphaLookupBuffer.Length < bufferSize)
				_alphaLookupBuffer = new byte[bufferSize];
			fixed (void* dst = _alphaLookupBuffer)
				Buffer.MemoryCopy(buffer.ToPointer(), dst, _alphaLookupBuffer.Length, bufferSize);

			// Write composited frame to shared buffer
			CpuCompositeToSharedBuffer(buffer, width, height);
		}
		else if (type == PaintElementType.Popup)
		{
			// Store popup CPU buffer for compositing
			if (_popupCpuBuffer.Length < bufferSize)
				_popupCpuBuffer = new byte[bufferSize];
			_popupCpuWidth = width;
			_popupCpuHeight = height;
			fixed (void* dst = _popupCpuBuffer)
				Buffer.MemoryCopy(buffer.ToPointer(), dst, _popupCpuBuffer.Length, bufferSize);

			// Recomposite using saved view buffer
			if (_alphaLookupBuffer.Length > 0)
			{
				fixed (byte* viewPtr = _alphaLookupBuffer)
					CpuCompositeToSharedBuffer((IntPtr)viewPtr, _alphaLookupBufferWidth, _alphaLookupBufferHeight);
			}
		}
	}

	private void CpuCompositeToSharedBuffer(IntPtr viewBuffer, int viewWidth, int viewHeight)
	{
		if (_sharedBuffer == null) return;

		int frameSize = viewWidth * viewHeight * _bytesPerPixel;

		// 防止緩衝區溢位：若 CEF 傳來的 frame 超出 buffer 容量，略過此幀
		// 這在 Resize 後的競態條件下會發生（CEF 尚未採用新尺寸，仍以舊尺寸 OnPaint）
		if (frameSize > _sharedBufferCapacity)
			return;

		// Copy view into shared buffer
		var frameBytes = new byte[frameSize];
		fixed (void* dst = frameBytes)
			Buffer.MemoryCopy(viewBuffer.ToPointer(), dst, frameSize, frameSize);

		// Overlay popup if visible
		if (_popupVisible && _popupCpuWidth > 0 && _popupCpuHeight > 0 && _popupCpuBuffer.Length >= _popupCpuWidth * _popupCpuHeight * _bytesPerPixel)
		{
			// _popupRect is already in physical pixels (set by ScaleScreenRect in OnPopupSize)
			int popupX = _popupRect.X;
			int popupY = _popupRect.Y;
			int viewStride = viewWidth * _bytesPerPixel;
			int popupStride = _popupCpuWidth * _bytesPerPixel;
			for (int row = 0; row < _popupCpuHeight; row++)
			{
				int dstY = popupY + row;
				if (dstY < 0 || dstY >= viewHeight) continue;
				int srcOffset = row * popupStride;
				int dstOffset = dstY * viewStride + popupX * _bytesPerPixel;
				int copyBytes = Math.Min(popupStride, (viewWidth - popupX) * _bytesPerPixel);
				if (copyBytes <= 0) continue;
				Array.Copy(_popupCpuBuffer, srcOffset, frameBytes, dstOffset, copyBytes);
			}
		}

		_sharedBuffer.WritePixels(frameBytes);
	}

	private ID3D11Texture2D* BuildViewTexture(Size size, bool isShared)
	{
		// Build _sharedTexture. Most of these properties are defined to match how CEF exposes the render buffer.
		D3D11_TEXTURE2D_DESC desc = new()
		{
			Width = (uint)size.Width,
			Height = (uint)size.Height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
			SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
			Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
			BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
			CPUAccessFlags = 0,
			MiscFlags = isShared ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED : 0
		};

		ID3D11Texture2D* texture;
		HRESULT hr = DxHandler.Device->CreateTexture2D(&desc, null, &texture);
		if (hr.FAILED)
		{
			throw new Exception($"Failed to create texture: {hr}");
		}

		return texture;
	}

	private Rect GetViewRectInternal()
	{
		if (_useSharedMemory)
		{
			return DpiScaling.ScaleViewRect(new Rect(0, 0, _sharedWidth, _sharedHeight));
		}

		D3D11_TEXTURE2D_DESC texDesc;
		_sharedTexture->GetDesc(&texDesc);
		return DpiScaling.ScaleViewRect(new Rect(0, 0, (int)texDesc.Width, (int)texDesc.Height));
	}

	public void SetMousePosition(int x, int y)
	{
		byte alpha = GetAlphaAt(x, y);

		// We treat 0 alpha as click through - if changed, fire off the event
		bool currentlyOnBackground = alpha == 0;
		if (currentlyOnBackground != _cursorOnBackground)
		{
			_cursorOnBackground = currentlyOnBackground;

			// EDGE CASE: if cursor transitions onto alpha:0 _and_ between two native cursor types, I guess this will be a race cond.
			// Not sure if should have two separate upstreams for them, or try and prevent the race. consider.
			CursorChanged?.Invoke(this, currentlyOnBackground ? Cursor.CrossingwayNoCapture : _cursor);
		}
	}

	private Cursor EncodeCursor(CursorType cursor)
	{
		switch (cursor)
		{
			// CEF calls default "pointer", and pointer "hand".
			case CursorType.Pointer: return Cursor.Default;
			case CursorType.Cross: return Cursor.Crosshair;
			case CursorType.Hand: return Cursor.Pointer;
			case CursorType.IBeam: return Cursor.Text;
			case CursorType.Wait: return Cursor.Wait;
			case CursorType.Help: return Cursor.Help;
			case CursorType.EastResize: return Cursor.EResize;
			case CursorType.NorthResize: return Cursor.NResize;
			case CursorType.NortheastResize: return Cursor.NeResize;
			case CursorType.NorthwestResize: return Cursor.NwResize;
			case CursorType.SouthResize: return Cursor.SResize;
			case CursorType.SoutheastResize: return Cursor.SeResize;
			case CursorType.SouthwestResize: return Cursor.SwResize;
			case CursorType.WestResize: return Cursor.WResize;
			case CursorType.NorthSouthResize: return Cursor.NsResize;
			case CursorType.EastWestResize: return Cursor.EwResize;
			case CursorType.NortheastSouthwestResize: return Cursor.NeswResize;
			case CursorType.NorthwestSoutheastResize: return Cursor.NwseResize;
			case CursorType.ColumnResize: return Cursor.ColResize;
			case CursorType.RowResize: return Cursor.RowResize;

			// There isn't really support for panning right now. Default to all-scroll.
			case CursorType.MiddlePanning:
			case CursorType.EastPanning:
			case CursorType.NorthPanning:
			case CursorType.NortheastPanning:
			case CursorType.NorthwestPanning:
			case CursorType.SouthPanning:
			case CursorType.SoutheastPanning:
			case CursorType.SouthwestPanning:
			case CursorType.WestPanning:
				return Cursor.AllScroll;

			case CursorType.Move: return Cursor.Move;
			case CursorType.VerticalText: return Cursor.VerticalText;
			case CursorType.Cell: return Cursor.Cell;
			case CursorType.ContextMenu: return Cursor.ContextMenu;
			case CursorType.Alias: return Cursor.Alias;
			case CursorType.Progress: return Cursor.Progress;
			case CursorType.NoDrop: return Cursor.NoDrop;
			case CursorType.Copy: return Cursor.Copy;
			case CursorType.None: return Cursor.None;
			case CursorType.NotAllowed: return Cursor.NotAllowed;
			case CursorType.ZoomIn: return Cursor.ZoomIn;
			case CursorType.ZoomOut: return Cursor.ZoomOut;
			case CursorType.Grab: return Cursor.Grab;
			case CursorType.Grabbing: return Cursor.Grabbing;

			// Not handling custom for now
			case CursorType.Custom: return Cursor.Default;
		}

		// Unmapped cursor, log and default
		Console.WriteLine($"Switching to unmapped cursor type {cursor}.");
		return Cursor.Default;
	}

	/// <summary>
	/// 計算 CEF 實際渲染的實體像素尺寸。
	/// GetViewRect 回傳 ceil(w/scale) × ceil(h/scale) 的邏輯像素，
	/// CEF 再乘以 deviceScale 得到實體像素，結果可能大於原始 w × h，
	/// 因此 buffer 必須以此實體像素大小分配，避免緩衝區溢位。
	/// </summary>
	private static (int Width, int Height) ComputePhysicalSize(Size logicalSize)
	{
		float scale = DpiScaling.GetDeviceScale();
		if (scale <= 0) scale = 1.0f;
		int physW = (int)(Math.Ceiling(logicalSize.Width / scale) * scale);
		int physH = (int)(Math.Ceiling(logicalSize.Height / scale) * scale);
		return (Math.Max(1, physW), Math.Max(1, physH));
	}
}
