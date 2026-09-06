using Lagerkraft.Shared;
using Lagerkraft.Shared.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lagerkraft.Shared.Tests;

public sealed class PeriodicJobTests
{
    [Fact]
    public async Task RunOnceAsync_Throws_SurvivesAndRunsAgain()
    {
        var job = new FlakyJob();
        job.ThrowNext = true;

        await job.TickForTests(CancellationToken.None);
        job.Runs.ShouldBe(1);

        await job.TickForTests(CancellationToken.None);
        job.Runs.ShouldBe(2);
        job.ThrowNext.ShouldBeFalse();
    }

    private sealed class FlakyJob() : PeriodicJob(new FakeClock(), NullLogger.Instance)
    {
        public int Runs;
        public bool ThrowNext;

        protected override TimeSpan Interval => TimeSpan.Zero;

        protected override Task RunOnceAsync(CancellationToken cancellationToken)
        {
            Runs++;
            if (ThrowNext)
            {
                ThrowNext = false;
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
    }
}
