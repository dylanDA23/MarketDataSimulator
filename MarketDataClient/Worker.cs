// MarketDataClient/Worker.cs
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketDataClient
{
    // Minimal placeholder worker to ensure BackgroundService/ILogger types exist for build.
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        public Worker(ILogger<Worker> logger) => _logger = logger;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // No-op placeholder
            _logger.LogDebug("Worker placeholder running.");
            return Task.CompletedTask;
        }
    }
}
