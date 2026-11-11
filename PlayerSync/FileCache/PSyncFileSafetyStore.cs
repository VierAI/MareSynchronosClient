using MareSynchronos.PlayerData.Pairs;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace PlayerSync.FileCache;

/// <summary>
/// 1.15.5.1415 (VierAI): crash-safety records kept in the file cache SQLite DB (tables from migration PSyncFileSafety).
/// - PSyncBadFiles: hashes known to be bad (failed validation or quarantined after a crash); skipped on apply, survive restarts.
/// - PSyncFileApplications: game path + last applied time per file hash, to trace what was loading at a crash.
/// - PSyncApplyMarkers: written right before a pair's mod files are handed to Penumbra, removed once the redraw settled.
///   A marker from an earlier session means the game died mid-apply: that pair's .mdl hashes are quarantined.
/// Everything here is best effort: a DB error is logged and never blocks syncing.
/// 1.15.5.1701 (audit M1): the blocked list is mirrored to PSyncBadFiles.json next to the DB. A DB restore (FileCache.GOOD.bak, up to an hour
/// old) or reset rolls the table back or wipes it; on start the mirror re-applies a later "Clear blocked files" and re-adds lost entries.
/// </summary>
public sealed class PSyncFileSafetyStore : IHostedService
{
    public static PSyncFileSafetyStore? Instance { get; private set; }

    private static readonly ConcurrentDictionary<string, byte> s_bad = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly ILogger<PSyncFileSafetyStore> _logger;
    private readonly IDatabaseProvider<FileCacheDbContext> _db;
    private readonly PairManager _pairManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareMediator _mediator;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _mirrorLock = new(1, 1);
    private string? _mirrorPath;
    private long _clearedTicks;

    private sealed record MirrorEntry(string Hash, string? Reason, long AddedTicks, string? SourcePairUid);

    private sealed class Mirror
    {
        public long ClearedTicks { get; set; }
        public List<MirrorEntry> Bad { get; set; } = [];
    }

    public PSyncFileSafetyStore(ILogger<PSyncFileSafetyStore> logger, IDatabaseProvider<FileCacheDbContext> db, PairManager pairManager,
        DalamudUtilService dalamudUtil, MareMediator mediator)
    {
        _logger = logger;
        _db = db;
        _pairManager = pairManager;
        _dalamudUtil = dalamudUtil;
        _mediator = mediator;
        Instance = this;
    }

    public static bool IsKnownBad(string hash) => s_bad.ContainsKey(hash);
    public int BadCount => s_bad.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // FileCacheManager (registered before this service) has already run the migrations in its StartAsync.
        _ = Task.Run(() => StartupAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        // Clean unload: this session's markers are not crashes.
        try
        {
            await ExecAsync("DELETE FROM PSyncApplyMarkers WHERE SessionId = $s;", c => c.Parameters.AddWithValue("$s", _sessionId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FileSafety] could not clear apply markers on stop");
        }
        if (Instance == this) Instance = null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var context = _db.CreateDbContext();
        var connection = new SqliteConnection(context.Database.GetConnectionString());
        await context.DisposeAsync().ConfigureAwait(false);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private async Task ExecAsync(string sql, Action<SqliteCommand>? bind, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task StartupAsync(CancellationToken ct)
    {
        var pending = new List<(string Uid, int Count)>();
        try
        {
            _mirrorPath = GetMirrorPath();
            await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
            {
                // 0) 1.15.5.1701: re-apply the mirrored blocked list (see the class summary)
                var mirror = await ReadMirrorAsync(ct).ConfigureAwait(false);
                if (mirror != null)
                {
                    Interlocked.Exchange(ref _clearedTicks, mirror.ClearedTicks);
                    await using var tx0 = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                    var removed = 0;
                    if (mirror.ClearedTicks > 0)
                    {
                        await using var delete = connection.CreateCommand();
                        delete.Transaction = tx0;
                        delete.CommandText = "DELETE FROM PSyncBadFiles WHERE AddedTicks <= $c;";
                        delete.Parameters.AddWithValue("$c", mirror.ClearedTicks);
                        removed = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    var readded = 0;
                    foreach (var entry in mirror.Bad)
                    {
                        if (string.IsNullOrEmpty(entry.Hash) || entry.AddedTicks <= mirror.ClearedTicks) continue;
                        await using var insert = connection.CreateCommand();
                        insert.Transaction = tx0;
                        insert.CommandText = "INSERT OR IGNORE INTO PSyncBadFiles (Hash, Reason, AddedTicks, SourcePairUid) VALUES ($h, $r, $t, $u);";
                        insert.Parameters.AddWithValue("$h", entry.Hash.ToUpperInvariant());
                        insert.Parameters.AddWithValue("$r", entry.Reason ?? string.Empty);
                        insert.Parameters.AddWithValue("$t", entry.AddedTicks);
                        insert.Parameters.AddWithValue("$u", (object?)entry.SourcePairUid ?? DBNull.Value);
                        readded += await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await tx0.CommitAsync(ct).ConfigureAwait(false);
                    if (removed > 0 || readded > 0)
                    {
                        _logger.LogInformation("[FileSafety] blocked list mirror: removed {removed} entry(s) cleared earlier, re-added {readded} missing entry(s)", removed, readded);
                    }
                }

                // 1) quarantine hashes from apply markers left by an earlier session (the game died mid-apply)
                var markers = new List<(string Id, string Uid, string[] Hashes)>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT ApplicationId, PairUid, MdlHashes FROM PSyncApplyMarkers WHERE SessionId <> $s;";
                    command.Parameters.AddWithValue("$s", _sessionId);
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        markers.Add((reader.GetString(0), reader.GetString(1),
                            reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                    }
                }

                if (markers.Count > 0)
                {
                    await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                    foreach (var marker in markers)
                    {
                        foreach (var hash in marker.Hashes)
                        {
                            await using var insert = connection.CreateCommand();
                            insert.Transaction = tx;
                            insert.CommandText = "INSERT OR IGNORE INTO PSyncBadFiles (Hash, Reason, AddedTicks, SourcePairUid) VALUES ($h, $r, $t, $u);";
                            insert.Parameters.AddWithValue("$h", hash.ToUpperInvariant());
                            insert.Parameters.AddWithValue("$r", "crash quarantine: the game closed while this file was being applied");
                            insert.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
                            insert.Parameters.AddWithValue("$u", marker.Uid);
                            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                        _logger.LogWarning("[FileSafety] game closed while applying files for {uid}; quarantined {count} model file(s): {hashes}",
                            marker.Uid, marker.Hashes.Length, string.Join(", ", marker.Hashes));
                        var idx = pending.FindIndex(p => string.Equals(p.Uid, marker.Uid, StringComparison.Ordinal));
                        if (idx < 0) pending.Add((marker.Uid, marker.Hashes.Length));
                        else pending[idx] = (marker.Uid, pending[idx].Count + marker.Hashes.Length);
                    }
                    await using (var delete = connection.CreateCommand())
                    {
                        delete.Transaction = tx;
                        delete.CommandText = "DELETE FROM PSyncApplyMarkers WHERE SessionId <> $s;";
                        delete.Parameters.AddWithValue("$s", _sessionId);
                        await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }

                // 2) load all known-bad hashes
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT Hash FROM PSyncBadFiles;";
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        s_bad.TryAdd(reader.GetString(0), 0);
                    }
                }
            }
            await WriteMirrorAsync(ct).ConfigureAwait(false);
            MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.ForkSeedVerifiedBad(s_bad.Keys);
            _logger.LogInformation("[FileSafety] loaded {count} known-bad file hash(es)", s_bad.Count);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileSafety] startup failed; crash quarantine is inactive this session");
            return;
        }

        if (pending.Count > 0)
        {
            await NotifyAsync(pending, ct).ConfigureAwait(false);
        }
    }

    // Chat warning per quarantined pair: a clickable player link once the pair is visible, else the UID after a while.
    private async Task NotifyAsync(List<(string Uid, int Count)> pending, CancellationToken ct)
    {
        DateTime? loggedInSince = null;
        var deadline = DateTime.UtcNow.AddMinutes(30);
        try
        {
            while (pending.Count > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!_dalamudUtil.IsLoggedIn) { loggedInSince = null; continue; }
                loggedInSince ??= DateTime.UtcNow;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var (uid, count) = pending[i];
                    var pair = _pairManager.GetPairByUID(uid);
                    var tail = $" had files loading when the game last closed unexpectedly. {count} of their model file(s) are now blocked. If this was a false alarm, use Settings > Sync > Filtering > Clear blocked files.";
                    if (pair != null && pair.Address != nint.Zero && !string.IsNullOrEmpty(pair.PlayerName))
                    {
                        _mediator.Publish(new PSyncChatNoticeMessage($"{pair.PlayerName} ({pair.UserData.AliasOrUID})" + tail, pair));
                        pending.RemoveAt(i);
                    }
                    else if (DateTime.UtcNow - loggedInSince.Value > TimeSpan.FromMinutes(2))
                    {
                        _mediator.Publish(new PSyncChatNoticeMessage((pair?.UserData.AliasOrUID ?? uid) + tail));
                        pending.RemoveAt(i);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // unloading
        }
    }

    public void RecordBad(string hash, string reason, string? pairUid)
    {
        if (string.IsNullOrEmpty(hash) || !s_bad.TryAdd(hash, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ExecAsync("INSERT OR REPLACE INTO PSyncBadFiles (Hash, Reason, AddedTicks, SourcePairUid) VALUES ($h, $r, $t, $u);", c =>
                {
                    c.Parameters.AddWithValue("$h", hash.ToUpperInvariant());
                    c.Parameters.AddWithValue("$r", reason.Length > 500 ? reason[..500] : reason);
                    c.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
                    c.Parameters.AddWithValue("$u", (object?)pairUid ?? DBNull.Value);
                }, _cts.Token).ConfigureAwait(false);
                await WriteMirrorAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[FileSafety] could not store bad file {hash}", hash);
            }
        });
    }

    /// <summary>Writes the apply marker. Awaited before the Penumbra hand-off so it is on disk if the game dies.</summary>
    public async Task<string?> BeginApplyAsync(string pairUid, IReadOnlyCollection<string> mdlHashes, CancellationToken ct)
    {
        if (mdlHashes.Count == 0) return null;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await ExecAsync("INSERT INTO PSyncApplyMarkers (ApplicationId, SessionId, PairUid, MdlHashes, StartedTicks) VALUES ($i, $s, $u, $h, $t);", c =>
            {
                c.Parameters.AddWithValue("$i", id);
                c.Parameters.AddWithValue("$s", _sessionId);
                c.Parameters.AddWithValue("$u", pairUid);
                c.Parameters.AddWithValue("$h", string.Join(",", mdlHashes));
                c.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            }, timeout.Token).ConfigureAwait(false);
            return id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FileSafety] could not write apply marker for {uid}", pairUid);
            return null;
        }
    }

    /// <summary>Removes the marker a little after the apply finished, so the redraw has settled first.</summary>
    public void EndApply(string? markerId)
    {
        if (markerId == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false);
                await ExecAsync("DELETE FROM PSyncApplyMarkers WHERE ApplicationId = $i;", c => c.Parameters.AddWithValue("$i", markerId), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[FileSafety] could not clear apply marker");
            }
            catch (OperationCanceledException)
            {
                // unloading; StopAsync clears this session's markers
            }
        });
    }

    public void RecordApplied(IReadOnlyCollection<(string Hash, string GamePath)> files)
    {
        if (files.Count == 0) return;
        var now = DateTime.UtcNow.Ticks;
        _ = Task.Run(async () =>
        {
            try
            {
                await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    await using var connection = await OpenAsync(_cts.Token).ConfigureAwait(false);
                    await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(_cts.Token).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = "INSERT INTO PSyncFileApplications (Hash, GamePath, LastAppliedTicks) VALUES ($h, $g, $t) ON CONFLICT(Hash, GamePath) DO UPDATE SET LastAppliedTicks = excluded.LastAppliedTicks;";
                    var h = command.Parameters.Add("$h", SqliteType.Text);
                    var g = command.Parameters.Add("$g", SqliteType.Text);
                    command.Parameters.AddWithValue("$t", now);
                    foreach (var (hash, gamePath) in files)
                    {
                        h.Value = hash.ToUpperInvariant();
                        g.Value = gamePath;
                        await command.ExecuteNonQueryAsync(_cts.Token).ConfigureAwait(false);
                    }
                    await tx.CommitAsync(_cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[FileSafety] could not record applied files");
            }
            catch (OperationCanceledException)
            {
                // unloading
            }
        });
    }

    /// <summary>Settings button: forget all known-bad files (undo false positives). Files are checked again on next apply.</summary>
    public async Task<int> ClearBadAsync()
    {
        var count = s_bad.Count;
        s_bad.Clear();
        MareSynchronos.PlayerData.Pairs.PairDataApplicationHandler.ForkClearVerifiedBad();
        Interlocked.Exchange(ref _clearedTicks, DateTime.UtcNow.Ticks); // 1701: also undoes older entries a DB restore brings back
        try
        {
            await ExecAsync("DELETE FROM PSyncBadFiles;", null, _cts.Token).ConfigureAwait(false);
            await WriteMirrorAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileSafety] could not clear the bad file list");
        }
        _logger.LogInformation("[FileSafety] cleared {count} known-bad file hash(es)", count);
        return count;
    }

    private string? GetMirrorPath()
    {
        try
        {
            using var context = _db.CreateDbContext();
            var dataSource = new SqliteConnectionStringBuilder(context.Database.GetConnectionString()).DataSource;
            var folder = string.IsNullOrEmpty(dataSource) ? null : Path.GetDirectoryName(Path.GetFullPath(dataSource));
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "PSyncBadFiles.json");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FileSafety] no blocked list mirror path");
            return null;
        }
    }

    private async Task<Mirror?> ReadMirrorAsync(CancellationToken ct)
    {
        var path = _mirrorPath;
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Mirror>(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[FileSafety] could not read the blocked list mirror {path}", path);
            return null;
        }
    }

    // Rewrites the mirror from the table. The blocked list is small and changes rarely (a bad file, a crash quarantine, the clear button).
    private async Task WriteMirrorAsync(CancellationToken ct)
    {
        var path = _mirrorPath;
        if (path == null) return;
        try
        {
            await _mirrorLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var mirror = new Mirror { ClearedTicks = Interlocked.Read(ref _clearedTicks) };
            await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Hash, Reason, AddedTicks, SourcePairUid FROM PSyncBadFiles;";
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    mirror.Bad.Add(new MirrorEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(mirror), ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            // unloading
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FileSafety] could not write the blocked list mirror {path}", path);
        }
        finally
        {
            _mirrorLock.Release();
        }
    }
}