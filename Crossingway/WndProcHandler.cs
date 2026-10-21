using System.Runtime.InteropServices;

namespace Crossingway;

// Contract: Install/Shutdown run on the thread that owns the window (comctl32 subclassing is per-thread).
// Subclassing instead of SetWindowLongPtr: removal works whatever got hooked above us later, because the
// chain link is comctl32's own proc and removing only drops our entry from its list.
// The callback delegate stays reachable until RemoveWindowSubclass confirms removal; when it can't be
// confirmed it is pinned for the process lifetime (leaks this assembly rather than leaving a dead callback).
internal static class WndProcHandler
{
	public delegate (bool, long) WndProcMessageDelegate(WindowsMessage msg, ulong wParam, long lParam);

	public enum Result
	{
		Ok,
		AlreadyInstalled,
		NotInstalled,
		NoWindow,
		WrongThread,
		ApiUnavailable,
		Failed
	}

	private const nuint _subclassId = 0x43574E44;
	private const uint _wmNcDestroy = 0x0082;

	private static NativeMethods.SubclassProc? _subclassProc;
	private static IntPtr _subclassProcPtr;
	private static GCHandle _pin;
	private static IntPtr _hWnd;
	private static bool _installed;
	private static volatile bool _detached;
	private static int _callbackFailureLogged;

	public static event WndProcMessageDelegate? WndProcMessage;

	public static Result Install(IntPtr hWnd)
	{
		if (_installed) { return Result.AlreadyInstalled; }

		if (hWnd == IntPtr.Zero) { return Result.NoWindow; }

		if (!NativeMethods.IsWindowOwnedByCurrentThread(hWnd)) { return Result.WrongThread; }

		NativeMethods.SubclassProc proc = SubclassDetour;
		IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(proc);
		bool ok;
		try { ok = NativeMethods.SetWindowSubclass(hWnd, procPtr, _subclassId, 0); }
		catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return Result.ApiUnavailable; }
		finally { GC.KeepAlive(proc); }

		if (!ok) { return Result.Failed; }

		_hWnd = hWnd;
		_subclassProc = proc;
		_subclassProcPtr = procPtr;
		_detached = false;
		_installed = true;
		return Result.Ok;
	}

	public static Result Shutdown()
	{
		if (!_installed)
		{
			if (!_pin.IsAllocated) { _subclassProc = null; }
			return Result.NotInstalled;
		}

		_detached = true;

		if (!NativeMethods.IsWindowOwnedByCurrentThread(_hWnd))
		{
			PinForever();
			return Result.WrongThread;
		}

		bool ok;
		try { ok = NativeMethods.RemoveWindowSubclass(_hWnd, _subclassProcPtr, _subclassId); }
		catch (Exception) { ok = false; }

		if (!ok)
		{
			PinForever();
			return Result.Failed;
		}

		_installed = false;
		_subclassProc = null;
		return Result.Ok;
	}

	private static void PinForever()
	{
		// Never freed: comctl32 may still hold our callback, so this assembly must not be collected.
		if (!_pin.IsAllocated && _subclassProc is not null) { _pin = GCHandle.Alloc(_subclassProc); }
	}

	private static long SubclassDetour(IntPtr hWnd, uint msg, ulong wParam, long lParam, nuint idSubclass, nuint refData)
	{
		// Contract: no managed exception may leave this method (it would unwind through the game's native
		// message loop and end the process). A failing handler only loses that one message; it still goes on
		// down the chain via DefSubclassProc, which is called exactly once per message.
		// Ignore things not targeting the current window handle
		if (!_detached && hWnd == _hWnd)
		{
			try
			{
				(bool, long)? resp = WndProcMessage?.Invoke((WindowsMessage)msg, wParam, lParam);

				// Item1 is a bool, where true == capture event. If false, we're falling through default handling.
				if (resp is { Item1: true })
				{
					return resp.Value.Item2;
				}
			}
			catch (Exception e) { ReportCallbackFailure(e); }
		}

		long result = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);

		// Documented requirement: a subclass must be gone before the window is destroyed.
		if (msg == _wmNcDestroy && _installed && hWnd == _hWnd)
		{
			try
			{
				if (NativeMethods.RemoveWindowSubclass(hWnd, _subclassProcPtr, _subclassId)) { _installed = false; }
			}
			catch (Exception e) { ReportCallbackFailure(e); }
		}

		return result;
	}

	private static void ReportCallbackFailure(Exception e)
	{
		if (Interlocked.Exchange(ref _callbackFailureLogged, 1) != 0) { return; }

		// Logging must not throw out of the window procedure either.
		try { Services.PluginLog.Error(e, "Window message handler failed; this and later failing messages fall through to the game (logged once)."); }
		catch { }
	}
}
