using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lagerkraft.Shared.Jobs;

public abstract class PeriodicJob : BackgroundService
{
    private static readonly Meter Meter = new("Lagerkraft.Jobs");
    private readonly Counter<long> _runs = Meter.CreateCounter<long>("lagerkraft.job.runs");
    private readonly Counter<long> _failures = Meter.CreateCounter<long>("lagerkraft.job.failures");
    private readonly IClock _clock;
    private readonly ILogger _logger;

    protected PeriodicJob(IClock clock, ILogger logger)
    {
        _clock = clock;
        _logger = logger;
    }

    public DateTimeOffset? LastRanAt { get; private set; }

    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceSafeAsync(stoppingToken);
            try
            {
                await Task.Delay(NextDelay(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal Task TickForTests(CancellationToken cancellationToken) => RunOnceSafeAsync(cancellationToken);

    private TimeSpan NextDelay()
    {
        var ms = Interval.TotalMilliseconds;
        var jitterMs = ms <= 0 ? 0 : Random.Shared.Next(0, Math.Max(1, (int)(ms * 0.1)));
        return Interval + TimeSpan.FromMilliseconds(jitterMs);
    }

    private async Task RunOnceSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunOnceAsync(cancellationToken);
            LastRanAt = _clock.UtcNow;
            _runs.Add(1);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures.Add(1);
            _logger.LogError(ex, "Periodic job {Job} failed", GetType().Name);
        }
    }
}
