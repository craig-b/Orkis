using Orkis.Agents;
using Orkis.Client;

namespace Orkis.Daemon.Tests;

/// <summary>
/// Drives schedules through the daemon over a real socket, including a firing: an
/// every-second cron produces a run whose origin points back at the schedule.
/// </summary>
public sealed class ScheduleTests(DaemonFixture fixture) : IClassFixture<DaemonFixture>, IDisposable
{
    private readonly OrkisClient _client = new(fixture.SocketPath);

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CreateListAndDelete()
    {
        var created = await _client.CreateScheduleAsync(
            new CreateScheduleRequest
            {
                Name = "nightly",
                Cron = "0 3 * * *",
                Prompt = "review",
            }
        );
        Assert.Equal("Fresh", created.Continuity);

        Assert.Contains(await _client.ListSchedulesAsync(), s => s.Id == created.Id);

        Assert.True(await _client.DeleteScheduleAsync(created.Id));
        Assert.DoesNotContain(await _client.ListSchedulesAsync(), s => s.Id == created.Id);
        Assert.False(await _client.DeleteScheduleAsync(created.Id));
    }

    [Fact]
    public async Task InvalidCronAndUnknownContinuityAreRejected()
    {
        var badCron = await Assert.ThrowsAsync<OrkisApiException>(() =>
            _client.CreateScheduleAsync(
                new CreateScheduleRequest
                {
                    Name = "x",
                    Cron = "not a cron",
                    Prompt = "p",
                }
            )
        );
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badCron.StatusCode);

        var badContinuity = await Assert.ThrowsAsync<OrkisApiException>(() =>
            _client.CreateScheduleAsync(
                new CreateScheduleRequest
                {
                    Name = "x",
                    Cron = "0 3 * * *",
                    Prompt = "p",
                    Continuity = "nonsense",
                }
            )
        );
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, badContinuity.StatusCode);
    }

    [Fact]
    public async Task AnEverySecondScheduleFiresARunAttributedToIt()
    {
        var schedule = await _client.CreateScheduleAsync(
            new CreateScheduleRequest
            {
                Name = "tick",
                Cron = "* * * * * *", // every second (seconds field)
                Prompt = "Run the greeting command.",
                SupervisorKey = "yolo",
            }
        );
        try
        {
            var origin = $"schedule:{schedule.Id}";
            var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            RunResponse? fired = null;
            while (DateTime.UtcNow < stopAt && fired is null)
            {
                fired = (await _client.ListRunsAsync()).FirstOrDefault(r => r.Origin == origin);
                if (fired is null)
                {
                    await Task.Delay(500);
                }
            }

            Assert.NotNull(fired);
            Assert.Equal(origin, fired.Origin);

            // The schedule records its firing.
            var refreshed = (await _client.ListSchedulesAsync()).Single(s => s.Id == schedule.Id);
            Assert.NotNull(refreshed.LastFiredAt);
            Assert.NotNull(refreshed.LastRunId);
        }
        finally
        {
            // Stop it firing more runs into the shared fixture.
            await _client.DeleteScheduleAsync(schedule.Id);
        }
    }
}
