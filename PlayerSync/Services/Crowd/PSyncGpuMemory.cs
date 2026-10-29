using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace MareSynchronos.Services;

/// <summary>
/// 1.15.5.1217: Auto memory budget source. Samples DXGI IDXGIAdapter3::QueryVideoMemoryInfo (LOCAL and NON_LOCAL)
/// every few seconds on its own background thread (never the framework thread). All interop is guarded: any failure,
/// a shared-memory-only GPU, or Wine (Mac/Linux, incl. Apple Silicon unified memory) makes Auto unavailable and the
/// budget falls back to the manual MB cap.
/// </summary>
public sealed class PSyncGpuMemory : IDisposable
{
    private const long MiB = 1024L * 1024L;
    private const long MinDedicatedBytes = 1024L * MiB; // below this the GPU is treated as integrated/shared-only
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");

    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _wanted;
    private volatile bool _disposed;
    private nint _adapter3;
    private string _unavailable = "starting";
    private Sample? _last;
    private long _prevNonLocalUsage = -1;
    private bool _nonLocalRising;

    private readonly record struct Sample(long LocalBudget, long LocalUsage, long NonLocalBudget, long NonLocalUsage, DateTimeOffset At);

    public static bool IsWine { get; } = DetectWine();

    public PSyncGpuMemory(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Read by the UI: last LOCAL/NON_LOCAL numbers, or why Auto is not available.</summary>
    public static string StatusText { get; private set; } = "Auto not started";

    public void SetWanted(bool wanted)
    {
        _wanted = wanted;
        if (!wanted || _thread != null || _disposed) return;
        if (IsWine)
        {
            _unavailable = "Wine / macOS: unified or shared memory, Manual cap used";
            StatusText = _unavailable;
            return;
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "PSync GPU memory", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>
    /// Budget for synced players in bytes = what they use now + room left under the LOCAL budget minus headroom.
    /// Tightened by 10% when NON_LOCAL usage is rising while LOCAL is near its budget (spilling to system RAM).
    /// </summary>
    public bool TryGetCap(long syncedAppliedBytes, VierAIConfig cfg, out long cap, out string why)
    {
        cap = -1;
        Sample? s;
        bool rising;
        lock (_lock)
        {
            s = _last;
            why = _unavailable;
            rising = _nonLocalRising;
        }
        if (s == null || DateTimeOffset.UtcNow - s.Value.At > TimeSpan.FromSeconds(15)) return false;
        var v = s.Value;
        long headroom = Math.Max((long)(v.LocalBudget * Math.Clamp(cfg.MemoryBudgetHeadroomPercent, 0, 50) / 100.0), Math.Max(0, cfg.MemoryBudgetHeadroomMinMiB) * MiB);
        long room = v.LocalBudget - headroom - v.LocalUsage;
        long c = syncedAppliedBytes + room;
        if (rising && v.LocalUsage > v.LocalBudget - headroom) c = (long)(c * 0.9);
        cap = Math.Max(0, c);
        why = string.Empty;
        return true;
    }

    private void Loop()
    {
        try
        {
            while (!_disposed)
            {
                if (_wanted)
                {
                    try { SampleOnce(); }
                    catch (Exception ex)
                    {
                        lock (_lock) { _last = null; _unavailable = "GPU memory query failed"; }
                        StatusText = _unavailable;
                        _logger.LogDebug(ex, "PSync: DXGI memory query failed");
                    }
                }
                Thread.Sleep(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PSync: GPU memory thread stopped");
        }
        finally
        {
            ReleaseAdapter();
        }
    }

    private unsafe void SampleOnce()
    {
        if (_adapter3 == nint.Zero && !TryOpenAdapter()) return;
        DXGI_QUERY_VIDEO_MEMORY_INFO local, nonLocal;
        var vtbl = *(nint**)_adapter3;
        var query = (delegate* unmanaged[Stdcall]<nint, uint, int, DXGI_QUERY_VIDEO_MEMORY_INFO*, int>)vtbl[14];
        int hr1 = query(_adapter3, 0, 0, &local);
        int hr2 = query(_adapter3, 0, 1, &nonLocal);
        if (hr1 < 0 || local.Budget == 0)
        {
            lock (_lock) { _last = null; _unavailable = $"GPU memory query returned 0x{hr1:X8}"; }
            StatusText = _unavailable;
            ReleaseAdapter();
            return;
        }
        var sample = new Sample((long)local.Budget, (long)local.CurrentUsage, hr2 < 0 ? 0 : (long)nonLocal.Budget, hr2 < 0 ? 0 : (long)nonLocal.CurrentUsage, DateTimeOffset.UtcNow);
        lock (_lock)
        {
            _nonLocalRising = _prevNonLocalUsage >= 0 && sample.NonLocalUsage > _prevNonLocalUsage + 64 * MiB;
            _prevNonLocalUsage = sample.NonLocalUsage;
            _last = sample;
            _unavailable = string.Empty;
        }
        StatusText = $"GPU local {sample.LocalUsage / MiB} / {sample.LocalBudget / MiB} MB, shared {sample.NonLocalUsage / MiB} / {sample.NonLocalBudget / MiB} MB";
    }

    /// <summary>Picks the hardware adapter with the most dedicated VRAM; refuses shared-only (integrated) GPUs.</summary>
    private unsafe bool TryOpenAdapter()
    {
        nint factory = nint.Zero;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(&iid, &factory) < 0 || factory == nint.Zero)
            {
                SetUnavailable("DXGI factory unavailable");
                return false;
            }
            var fvt = *(nint**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)fvt[12];
            nint best = nint.Zero;
            ulong bestDedicated = 0;
            for (uint i = 0; i < 16; i++)
            {
                nint adapter = nint.Zero;
                if (enumAdapters1(factory, i, &adapter) < 0 || adapter == nint.Zero) break;
                var avt = *(nint**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<nint, DXGI_ADAPTER_DESC1*, int>)avt[10];
                DXGI_ADAPTER_DESC1 desc;
                bool keep = false;
                if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & 2u) == 0 && (ulong)desc.DedicatedVideoMemory > bestDedicated)
                {
                    keep = true;
                    bestDedicated = (ulong)desc.DedicatedVideoMemory;
                }
                if (keep)
                {
                    if (best != nint.Zero) Marshal.Release(best);
                    best = adapter;
                }
                else
                {
                    Marshal.Release(adapter);
                }
            }
            if (best == nint.Zero)
            {
                SetUnavailable("no hardware GPU found");
                return false;
            }
            if (bestDedicated < (ulong)MinDedicatedBytes)
            {
                Marshal.Release(best);
                SetUnavailable("integrated / shared-memory GPU");
                return false;
            }
            var iid3 = IID_IDXGIAdapter3;
            int hr = Marshal.QueryInterface(best, in iid3, out var a3);
            Marshal.Release(best);
            if (hr < 0 || a3 == nint.Zero)
            {
                SetUnavailable("DXGI 1.4 not supported");
                return false;
            }
            _adapter3 = a3;
            return true;
        }
        finally
        {
            if (factory != nint.Zero) Marshal.Release(factory);
        }
    }

    private void SetUnavailable(string why)
    {
        lock (_lock) { _last = null; _unavailable = why; }
        StatusText = why;
    }

    private void ReleaseAdapter()
    {
        var a = Interlocked.Exchange(ref _adapter3, nint.Zero);
        if (a != nint.Zero)
        {
            try { Marshal.Release(a); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static bool DetectWine()
    {
        try
        {
            var ntdll = NativeLibrary.Load("ntdll.dll");
            if (NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _)) return true;
        }
        catch
        {
            // ignore
        }
        try { return Dalamud.Utility.Util.IsWine(); }
        catch { return false; }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern unsafe int CreateDXGIFactory1(Guid* riid, nint* ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
