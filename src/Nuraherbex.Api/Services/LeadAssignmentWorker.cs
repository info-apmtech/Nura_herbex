using Microsoft.Extensions.Options;
using Nuraherbex.Api.Options;

namespace Nuraherbex.Api.Services;

/// <summary>
/// Every few seconds: switches away staff off, gives waiting leads to whoever is online, and shares open leads out evenly,
/// so someone who comes online starts receiving leads within seconds instead of waiting for the 2-minute follow-up cycle.
/// Set FollowUps:WorkerEnabled=false on any other process that shares the database.
/// </summary>
public sealed class LeadAssignmentWorker(IServiceScopeFactory scopes, IOptions<FollowUpOptions> options, ILogger<LeadAssignmentWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private async Task Step(string name, Func<Task<int>> run)
    {
        try { await run(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.LogError(ex, "[Lead] Assignment step failed: {Step}", name); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var leads = scope.ServiceProvider.GetRequiredService<LeadService>();
                // each step on its own, so one failing never blocks the others
                await Step("release away staff", leads.ReleaseAwayStaffAsync);
                await Step("assign waiting leads", leads.AssignWaitingAsync);
                await Step("share out unopened leads", leads.RebalanceAsync);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "[Lead] Assignment cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
