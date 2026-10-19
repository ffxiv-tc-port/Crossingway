using System.Threading;

namespace Crossingway;

/// <summary>
/// While unloading, RunOnFrameworkThread (and RunOnTick with no delay) runs the delegate
/// <b>inline on the calling thread</b>, so the dispatch protects nothing — a background thread
/// would then race the framework thread on the overlay dictionary, DX textures and ImGui state.
/// </summary>
internal static class FrameworkUnloadGuard
{
	private static int _reported;

	/// <summary>
	/// True when unloading and <b>not</b> on the framework thread; the caller must then
	/// drop the work entirely. Always false once on the framework thread, where running
	/// inline is correct.
	/// </summary>
	internal static bool ShouldSkip(string scope)
	{
		var framework = Services.Framework;
		if (!framework.IsFrameworkUnloading || framework.IsInFrameworkUpdateThread)
			return false;

		if (Interlocked.Exchange(ref _reported, 1) == 0)
			Services.PluginLog.Information($"Plugin is unloading; dropping work queued from a background thread ({scope}).");

		return true;
	}
}
