using System;
using DesktopNetBlocker.Core.Common;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Policy;
using DesktopNetBlocker.Service.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("=================================================");
        Console.WriteLine("Desktop Net Blocker - Network Enforcement Service");
        Console.WriteLine("=================================================");

        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = "DesktopNetBlockerService";
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((hostContext, services) =>
            {
                string dbPath = ServicePaths.GetDatabasePath();
                services.AddSingleton(new BlockerDbContext(dbPath));
                services.AddSingleton<ITimeProvider, DefaultTimeProvider>();
                services.AddSingleton<IAuthorizationStateMachine, AuthorizationStateMachine>();
                services.AddSingleton<NetworkEnforcementCoordinator>();
                services.AddHostedService<BlockerWorker>();
            });

        var app = builder.Build();
        app.Run();
    }
}
