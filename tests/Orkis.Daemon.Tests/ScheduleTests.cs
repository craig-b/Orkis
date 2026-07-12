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
    public async Task UpdatePatchesOnlyTheGivenFields()
    {
        var created = await _client.CreateScheduleAsync(
            new CreateScheduleRequest
            {
                Name = "editable",
                Cron = "0 3 * * *",
                Prompt = "review",
                Continuity = "sharedStorage",
            }
        );
        Assert.True(created.Enabled);

        try
        {
            // A one-field patch pauses it and leaves everything else untouched.
            var paused = await _client.UpdateScheduleAsync(created.Id, new UpdateScheduleRequest { Enabled = false });
            Assert.NotNull(paused);
            Assert.False(paused.Enabled);
            Assert.Equal("review", paused.Prompt);
            Assert.Equal("SharedStorage", paused.Continuity);

            // Other fields update in isolation; enabled stays as last set.
            var edited = await _client.UpdateScheduleAsync(
                created.Id,
                new UpdateScheduleRequest { Cron = "0 4 * * *", Prompt = "audit" }
            );
            Assert.NotNull(edited);
            Assert.Equal("0 4 * * *", edited.Cron);
            Assert.Equal("audit", edited.Prompt);
            Assert.False(edited.Enabled);

            // Validation still applies, and the change is not persisted.
            var badCron = await Assert.ThrowsAsync<OrkisApiException>(() =>
                _client.UpdateScheduleAsync(created.Id, new UpdateScheduleRequest { Cron = "nope" })
            );
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badCron.StatusCode);

            // A missing schedule patches to null.
            Assert.Null(
                await _client.UpdateScheduleAsync("does-not-exist", new UpdateScheduleRequest { Enabled = true })
            );
        }
        finally
        {
            await _client.DeleteScheduleAsync(created.Id);
        }
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

    [Fact]
    public async Task HandoffContinuityCapturesTheFiringsClosingNote()
    {
        var store = (Orkis.Scheduling.IScheduleStore)
            fixture.Services.GetService(typeof(Orkis.Scheduling.IScheduleStore))!;

        var schedule = await _client.CreateScheduleAsync(
            new CreateScheduleRequest
            {
                Name = "handoff",
                Cron = "* * * * * *",
                Prompt = "Run the greeting command.",
                SupervisorKey = "yolo",
                Continuity = "sharedStorageWithHandoff",
            }
        );
        try
        {
            // Captured from the run's completion event, not the firing call — so this
            // works whether the run ran straight through or parked and resumed.
            var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            string? handoff = null;
            while (DateTime.UtcNow < stopAt && string.IsNullOrEmpty(handoff))
            {
                handoff = (await store.GetAsync(schedule.Id))?.Handoff;
                if (string.IsNullOrEmpty(handoff))
                {
                    await Task.Delay(500);
                }
            }

            Assert.False(string.IsNullOrEmpty(handoff));
            Assert.Contains("greeting command", handoff);
        }
        finally
        {
            await _client.DeleteScheduleAsync(schedule.Id);
        }
    }
}
