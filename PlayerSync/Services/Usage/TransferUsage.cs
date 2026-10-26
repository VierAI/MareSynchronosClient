using System.Text.Json;

namespace MareSynchronos.Services.Usage;

/// <summary>One finished (or the current) play session in the usage history.</summary>
public sealed class UsageSession
{
    public DateTimeOffset StartUtc { get; set; }
    public double DurationSeconds { get; set; }
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
}

/// <summary>Fork: download/upload totals for one calendar month (local time, key "yyyy-MM").</summary>
public sealed class UsageMonth
{
    public string Month { get; set; } = string.Empty;
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
}

/// <summary>Fork: transfer-usage.json layout since the monthly totals (older files are a bare session list and are migrated on load).</summary>
public sealed class UsageFile
{
    public int Version { get; set; } = 2;
    public List<UsageSession> Sessions { get; set; } = new();
    public List<UsageMonth> Months { get; set; } = new();
}

/// <summary>
/// Fork (1.15.5.1211): counts PlayerSync file transfer bytes for this session: totals and a per-minute history (last
/// <see cref="Minutes"/> minutes). Each transfer adds what it actually moved to the minute it finished or was cancelled. Past sessions (start, length, down/up) and per-month totals (last <see cref="MaxMonths"/>) are kept in
/// transfer-usage.json in the plugin config folder, at most <see cref="MaxSessions"/>.
/// Counted: file downloads (bytes read from the server, compressed, before decompression) and file uploads. Not counted:
/// the server connection itself (API/websocket messages) and HTTP headers.
/// Thread-safe and allocation-free; Add* is called from download/upload threads. Nothing per player is kept.
/// </summary>
public static class TransferUsage
{
    public const int Minutes = 240;
    public const int MaxSessions = 100;
    public const int MaxMonths = 24;
    private const string FileName = "transfer-usage.json";

    private static readonly object Lock = new();
    private static readonly long[] DownPerMinute = new long[Minutes];
    private static readonly long[] UpPerMinute = new long[Minutes];
    private static readonly List<UsageSession> History = new();
    private static readonly SortedDictionary<string, UsageMonth> Months = new(StringComparer.Ordinal);
    private static DateTimeOffset _start = DateTimeOffset.UtcNow;
    private static long _minute;
    private static long _down;
    private static long _up;
    private static string _path = string.Empty;
    private static long _version;

    /// <summary>Changes whenever anything was counted; the UI rebuilds its cached arrays only when this moves.</summary>
    public static long Version => Interlocked.Read(ref _version);
    public static DateTimeOffset SessionStart { get { lock (Lock) return _start; } }
    public static long SessionDownloaded => Interlocked.Read(ref _down);
    public static long SessionUploaded => Interlocked.Read(ref _up);

    public static void Init(string configDirectory)
    {
        lock (Lock)
        {
            _path = Path.Combine(configDirectory, FileName);
            _start = DateTimeOffset.UtcNow;
            try
            {
                if (File.Exists(_path))
                {
                    var text = File.ReadAllText(_path);
                    using var doc = JsonDocument.Parse(text);
                    History.Clear();
                    Months.Clear();
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        // Pre-monthly file: a bare list of sessions. Seed the monthly totals from the sessions (credited to the month each started).
                        var list = JsonSerializer.Deserialize<List<UsageSession>>(text);
                        if (list != null)
                        {
                            History.AddRange(list.Where(s => s != null).TakeLast(MaxSessions));
                            foreach (var s in History)
                            {
                                var m = Month(MonthKey(s.StartUtc.ToLocalTime()));
                                m.Downloaded += Math.Max(0, s.Downloaded);
                                m.Uploaded += Math.Max(0, s.Uploaded);
                            }
                        }
                    }
                    else
                    {
                        var file = JsonSerializer.Deserialize<UsageFile>(text);
                        if (file != null)
                        {
                            History.AddRange((file.Sessions ?? new()).Where(s => s != null).TakeLast(MaxSessions));
                            foreach (var m in (file.Months ?? new()).Where(x => x != null && !string.IsNullOrEmpty(x.Month)))
                            {
                                var t = Month(m.Month);
                                t.Downloaded += Math.Max(0, m.Downloaded);
                                t.Uploaded += Math.Max(0, m.Uploaded);
                            }
                        }
                    }
                    TrimMonths();
                }
            }
            catch
            {
                History.Clear(); // a broken file just starts a new history
                Months.Clear();
            }
        }
    }

    private static string MonthKey(DateTimeOffset local) => local.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    private static UsageMonth Month(string key)
    {
        if (!Months.TryGetValue(key, out var m)) Months[key] = m = new UsageMonth { Month = key };
        return m;
    }

    private static void TrimMonths()
    {
        while (Months.Count > MaxMonths) Months.Remove(Months.Keys.First());
    }

    private static void Advance(DateTimeOffset now)
    {
        var minute = (long)Math.Max(0, (now - _start).TotalMinutes);
        if (minute <= _minute) return;
        var steps = Math.Min(minute - _minute, Minutes);
        for (long m = 1; m <= steps; m++)
        {
            var i = (int)((_minute + m) % Minutes);
            DownPerMinute[i] = 0;
            UpPerMinute[i] = 0;
        }
        _minute = minute;
    }

    public static void AddDownload(long bytes)
    {
        if (bytes <= 0) return;
        lock (Lock)
        {
            Advance(DateTimeOffset.UtcNow);
            DownPerMinute[(int)(_minute % Minutes)] += bytes;
            _down += bytes;
            Month(MonthKey(DateTimeOffset.Now)).Downloaded += bytes;
            TrimMonths();
            _version++;
        }
    }

    public static void AddUpload(long bytes)
    {
        if (bytes <= 0) return;
        lock (Lock)
        {
            Advance(DateTimeOffset.UtcNow);
            UpPerMinute[(int)(_minute % Minutes)] += bytes;
            _up += bytes;
            Month(MonthKey(DateTimeOffset.Now)).Uploaded += bytes;
            TrimMonths();
            _version++;
        }
    }

    /// <summary>Copies the per-minute history (oldest first, in MiB) into the given arrays; returns the number of minutes.</summary>
    public static int CopyMinutes(float[] downMiB, float[] upMiB)
    {
        lock (Lock)
        {
            Advance(DateTimeOffset.UtcNow);
            var count = (int)Math.Min(_minute + 1, Math.Min(Minutes, downMiB.Length));
            for (var k = 0; k < count; k++)
            {
                var i = (int)((_minute - count + 1 + k) % Minutes);
                downMiB[k] = DownPerMinute[i] / 1048576f;
                upMiB[k] = UpPerMinute[i] / 1048576f;
            }
            return count;
        }
    }

    /// <summary>Totals for the last <paramref name="count"/> calendar months (oldest first, current month last, local time); missing months are zero.</summary>
    public static void CopyMonths(long[] down, long[] up, DateTime[] month, int count)
    {
        var first = DateTime.Now;
        first = new DateTime(first.Year, first.Month, 1).AddMonths(-(count - 1));
        lock (Lock)
        {
            for (var k = 0; k < count; k++)
            {
                var d = first.AddMonths(k);
                month[k] = d;
                if (Months.TryGetValue(d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), out var m)) { down[k] = m.Downloaded; up[k] = m.Uploaded; }
                else { down[k] = 0; up[k] = 0; }
            }
        }
    }

    /// <summary>Past sessions plus the current one (newest first), written into the reusable list.</summary>
    public static void CopySessions(List<UsageSession> into)
    {
        into.Clear();
        lock (Lock)
        {
            into.Add(Current());
            for (var i = History.Count - 1; i >= 0; i--) into.Add(History[i]);
        }
    }

    private static UsageSession Current() => new()
    {
        StartUtc = _start,
        DurationSeconds = (DateTimeOffset.UtcNow - _start).TotalSeconds,
        Downloaded = _down,
        Uploaded = _up,
    };

    /// <summary>Writes the history with the current session as the newest entry (sessions with no traffic are not kept).</summary>
    public static void Save()
    {
        string json;
        string path;
        lock (Lock)
        {
            if (string.IsNullOrEmpty(_path)) return;
            var list = new List<UsageSession>(History);
            if (_down > 0 || _up > 0) list.Add(Current());
            if (list.Count > MaxSessions) list.RemoveRange(0, list.Count - MaxSessions);
            json = JsonSerializer.Serialize(new UsageFile
            {
                Sessions = list,
                Months = Months.Values.Select(m => new UsageMonth { Month = m.Month, Downloaded = m.Downloaded, Uploaded = m.Uploaded }).ToList(),
            });
            path = _path;
        }
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // best effort; tried again on the next save
        }
    }

    /// <summary>Clears the history and starts a new session from zero.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            History.Clear();
            Months.Clear();
            Array.Clear(DownPerMinute);
            Array.Clear(UpPerMinute);
            _start = DateTimeOffset.UtcNow;
            _minute = 0;
            _down = 0;
            _up = 0;
            _version++;
        }
        Save();
    }
}
