using SharedMemory;

namespace Crossingway.Common;

/// <summary>
/// Thin wrapper around SharedArray&lt;byte&gt; that exposes bulk read/write for pixel data.
/// Used as a fallback IPC transport when D3D11 shared textures are unavailable (e.g. DXMT).
/// </summary>
public class CrossingwaySharedBuffer : SharedArray<byte>
{
public CrossingwaySharedBuffer(string name, int size) : base(name, size) { }
public CrossingwaySharedBuffer(string name) : base(name) { }

public void WritePixels(byte[] data)
{
WriteArray(data, 0, data.Length, 0);
}

public void ReadPixels(byte[] data)
{
ReadArray(data, 0, data.Length, 0);
}
}
