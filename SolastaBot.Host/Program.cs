using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain;
using SolastaBot.Exchange.Chain.Solana.Interfaces;
using SolastaBot.Host.Chain;

namespace SolastaBot.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args is ["--help"])
        {
            Console.WriteLine("Solasta trading worker. Usage: --settings <json-file> --state <directory>");
            Console.WriteLine("Modes: Paper, Simulate, Devnet. Mainnet broadcasting is disabled.");
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            Dictionary<string, string> arguments = new(StringComparer.Ordinal);
            if (args.Length != 4)
            {
                throw new ArgumentException("Expected --settings <json-file> --state <directory>.");
            }

            for (int index = 0; index < args.Length; index += 2)
            {
                if (args[index] is not ("--settings" or "--state") || !arguments.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Unknown or duplicate option.");
                }
            }

            JsonSerializerOptions jsonOptions = new()
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            };

            string configuration = await File.ReadAllTextAsync(arguments["--settings"]);
            using JsonDocument document = JsonDocument.Parse(configuration);
            if (document.RootElement.TryGetProperty("Mode", out JsonElement mode) && mode.GetString() is "Simulate" or "Devnet")
            {
                using CancellationTokenSource shutdown = new();
                ConsoleCancelEventHandler handler = (sender, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
                Console.CancelKeyPress += handler;
                using System.Runtime.InteropServices.PosixSignalRegistration? terminate = OperatingSystem.IsWindows() ? null :
                    System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
                try
                {
                    return await ConnectedTradingWorker.RunAsync(arguments["--settings"], arguments["--state"], shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return 0;
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }
            PaperTradingOptions options = JsonSerializer.Deserialize<PaperTradingOptions>(configuration, jsonOptions) ?? throw new ArgumentException("Settings must be a JSON object.");
            options.Validate();
            string stateDirectory = Path.GetFullPath(arguments["--state"]);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options))));
            HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(new PaperWorkerSettings(stateDirectory, hash, options));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
            builder.Services.AddSingleton<ILaunchMarketFeed>(services => new PumpLaunchMarketFeed(services.GetRequiredService<HttpClient>(), services.GetRequiredService<TimeProvider>(), options.RequestsPerSecond));
            builder.Services.AddSerilog(logger => logger.WriteTo.Console());
            builder.Services.AddHostedService<LaunchTradingWorker>();
            using IHost host = builder.Build();
            await host.RunAsync();
            return Environment.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine($"Cannot start trader: {exception.Message}");
            return 1;
        }
    }
}
