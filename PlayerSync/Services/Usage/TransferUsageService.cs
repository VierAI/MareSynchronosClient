using Dalamud.Plugin;
using Microsoft.Extensions.Hosting;

namespace MareSynchronos.Services.Usage;

/// <summary>Loads the usage history at start, saves it every 5 minutes and on unload.</summary>
public sealed class TransferUsageService : IHostedService, IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private Timer? _timer;

    public TransferUsageService(IDalamudPluginInterface pi) => _pi = pi;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TransferUsage.Init(_pi.ConfigDirectory.FullName);
        _timer = new Timer(_ => TransferUsage.Save(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        _timer = null;
        TransferUsage.Save();
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
