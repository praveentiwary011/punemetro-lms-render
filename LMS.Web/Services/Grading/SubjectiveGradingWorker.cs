using LMS.Web.Data;

namespace LMS.Web.Services.Grading;

/// <summary>Background grader (§AIG-05 CPU mode, and the outage-retry path in either mode).
/// Polls for attempts with ungraded subjective answers and grades them, so an Ollama outage
/// self-heals and CPU-mode trainees are graded without blocking the submit request (§AIG-09).</summary>
public class SubjectiveGradingWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SubjectiveGradingWorker> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public SubjectiveGradingWorker(IServiceProvider services, ILogger<SubjectiveGradingWorker> log)
    { _services = services; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var grading = scope.ServiceProvider.GetRequiredService<GradingService>();
                var ids = await grading.PendingAttemptIdsAsync(20, stoppingToken);
                if (ids.Count > 0)   // only probe/contact Ollama when there is work to grade
                {
                    var ollama = scope.ServiceProvider.GetRequiredService<IOllamaClient>();
                    if (await ollama.IsAvailableAsync(stoppingToken))
                        foreach (var id in ids)
                            await grading.GradeAttemptPendingAsync(id, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Subjective grading worker pass failed."); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
