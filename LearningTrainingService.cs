using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Elektra.Configuration;

namespace Elektra.Learning;

public sealed class LearningTrainingService(
    LocalLearningService learning,
    IOptions<AssistantOptions> options,
    ILogger<LearningTrainingService> logger) : BackgroundService
{
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(Math.Clamp(options.Value.LearningTrainingCheckMinutes, 5, 24 * 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkInterval);
        do
        {
            try
            {
                await learning.TrainIfReadyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled local learning evaluation failed; the currently active intent model was kept.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
