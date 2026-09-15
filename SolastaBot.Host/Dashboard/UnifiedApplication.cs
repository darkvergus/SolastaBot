using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using SolastaBot.Exchange.Chain;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Host.Markets;
using SolastaBot.Host.Portfolio;
using SolastaBot.Host.Research;

namespace SolastaBot.Host.Dashboard;

public static class UnifiedApplication
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length != 4)
            {
                throw new ArgumentException("Usage: serve --settings <json-file> --state <directory>");
            }

            Dictionary<string, string> arguments = [];
            for (int index = 0; index < args.Length; index += 2)
            {
                if (args[index] is not ("--settings" or "--state") || !arguments.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException("Unknown or duplicate option.");
                }
            }
            JsonSerializerOptions json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            UnifiedSettings settings = JsonSerializer.Deserialize<UnifiedSettings>(await File.ReadAllTextAsync(arguments["--settings"]), json) ?? throw new ArgumentException("Settings required.");
            settings.Validate();
            string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(webRoot))
            {
                using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "SolastaBot.Host.staticwebassets.runtime.json"), cancellationToken));
                webRoot = manifest.RootElement.GetProperty("ContentRoots")[0].GetString()!;
            }
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(UnifiedApplication).Assembly.GetName().Name, WebRootPath = webRoot });
            builder.WebHost.UseUrls(settings.ListenUrl);
            builder.Services.AddRazorPages();
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new(Path.Combine(Path.GetFullPath(arguments["--state"]), "dashboard-keys")));
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton(_ => new PortfolioStore(arguments["--state"], settings));
            builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
            builder.Services.AddSingleton(Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true }));
            builder.Services.AddSingleton(services => new PumpLaunchMarketFeed(services.GetRequiredService<HttpClient>(), TimeProvider.System, 0.8m));
            builder.Services.AddSingleton(services => new ReadOnlyRateLimitedRpc(new SolanaRpc(services.GetRequiredService<HttpClient>(), new(settings.SolanaRpcUrl), false)));
            builder.Services.AddHostedService<PortfolioClockWorker>();
            builder.Services.AddHostedService<SolanaPaperWorker>();
            builder.Services.AddHostedService<SolanaDiscoveryWorker>();
            builder.Services.AddHostedService<BinancePaperWorker>();
            builder.Services.AddHostedService<ResearchWorker>();
            await using WebApplication application = builder.Build();
            application.Use(async (context, next) =>
            {
                if (context.Connection.RemoteIpAddress is not null && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress) || context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.CacheControl = "no-store";
                await next();
            });
            application.UseStaticFiles();
            application.MapRazorPages();
            application.MapGet("/health", (PortfolioStore store) => Results.Json(new { mode = "Paper", revision = store.Snapshot().Revision, networkEnabled = settings.EnableNetwork }));
            await application.StartAsync(cancellationToken);
            await application.WaitForShutdownAsync(cancellationToken);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine($"Cannot start unified service: {exception.Message}");
            return 1;
        }
    }
}
