using Microsoft.Extensions.Options;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Every two minutes: turns delivered orders into 15-day journeys, raises order-progress tasks, sends advance reminders,
/// escalates overdue work and (if an admin enabled it) sends template messages. Safe to run on one server only:
/// set FollowUps:WorkerEnabled=false on any other process that shares the database.
/// </summary>
public sealed class FollowUpWorker(IServiceScopeFactory scopes, IOptions<FollowUpOptions> options, ILogger<FollowUpWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) { log.LogInformation("[FollowUp] Worker disabled by configuration"); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FollowUpService>().RunCycleAsync();
                await scope.ServiceProvider.GetRequiredService<TaskService>().RunCycleAsync();
                await scope.ServiceProvider.GetRequiredService<LeadService>().AssignWaitingAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "[FollowUp] Cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
