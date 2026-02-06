using Microsoft.AspNetCore.SignalR;
using SharpAI.Api.Hubs;
using SharpAI.Core;

namespace SharpAI.Api.Services
{
    public sealed class LogBroadcastService : IHostedService
    {
        private readonly IHubContext<LogHub> hubContext;

        public LogBroadcastService(IHubContext<LogHub> hubContext)
        {
            this.hubContext = hubContext;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StaticLogger.LogAdded += OnLogAdded;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StaticLogger.LogAdded -= OnLogAdded;
            return Task.CompletedTask;
        }

        private void OnLogAdded(string logEntry)
        {
            _ = this.hubContext.Clients.All.SendAsync("LogAdded", logEntry);
        }
    }
}
