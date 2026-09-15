using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Pages;

public sealed class IndexModel(PortfolioStore store) : PageModel
{
    public PortfolioState Portfolio { get; private set; } = null!;
    [TempData] public string? Notice { get; set; }

    public void OnGet() => Portfolio = store.Snapshot();

    public IActionResult OnPostControl(string command, string? strategy)
    {
        return Change(() => store.Mutate(state =>
        {
            if (strategy is null)
            {
                switch (command)
                {
                    case "halt":
                        state.Halted = true;

                        break;
                    case "resume":
                        state.Halted = false;

                        break;
                    default:
                    {
                        if (command != "flatten")
                        {
                            throw new ArgumentException("Unknown control.");
                        }

                        break;
                    }
                }

                foreach (StrategyAccount account in state.Strategies.Values)
                {
                    if (command == "flatten")
                    {
                        account.Flatten = true;
                        account.Paused = true;
                    }

                    PortfolioEngine.Record(state, account, "Control", "", 0m, 0m, command);
                }
            }
            else
            {
                StrategyAccount account = state.Strategies[strategy];

                if (account.TrialCompleted)
                {
                    throw new ArgumentException("Completed trials are retained as read-only results.");
                }

                switch (command)
                {
                    case "pause":
                        account.Paused = true;

                        break;
                    case "resume":
                        account.Paused = false;
                        account.Flatten = false;

                        break;
                    case "flatten":
                        account.Flatten = true;
                        account.Paused = true;

                        break;
                    default:
                        throw new ArgumentException("Unknown control.");
                }

                PortfolioEngine.Record(state, account, "Control", "", 0m, 0m, command);
            }

            store.Engine.Tick(state, state.Now);
        }));
    }

    public IActionResult OnPostTransfer(string source, string target, decimal amount) =>
        Change(() => store.Mutate(state => store.Engine.TransferBudget(state, source, target, amount)));

    public IActionResult OnPostConversion(string allocation, decimal sourceAmount, decimal receivedAmount, string reference) => Change(() =>
        store.Mutate(state => store.Engine.RecordConversion(state, allocation, sourceAmount, receivedAmount, reference)));

    public IActionResult OnPostPerpetualSettings(string strategy, int fast, int slow, decimal band, decimal adx, decimal atrStop)
    {
        return Change(() => store.Mutate(state =>
        {
            StrategyAccount account = state.Strategies[strategy];

            if (account.TrialCompleted || account.Settings.Asset != "USDT" || account.Positions.Count != 0 || account.Orders.Count != 0)
            {
                throw new ArgumentException("Close perpetual positions before changing rules; completed trials are read-only.");
            }

            StrategySettings updated = account.Settings with
            {
                Version = account.Settings.Version + 1,
                Trend = account.Settings.Trend with { FastPeriod = fast, SlowPeriod = slow, EntryBandBasisPoints = band, MinimumTrendStrength = adx, StopAtrMultiple = atrStop }
            };

            updated.Validate();
            account.Settings = updated;
            account.BlockedDirection = 0;
            PortfolioEngine.Record(state, account, "Configuration", "", 0m, 0m, System.Text.Json.JsonSerializer.Serialize(updated));
        }));
    }

    public IActionResult OnPostSplit(string strategy, decimal retain, decimal reserve, Dictionary<string, decimal> allocations)
    {
        return Change(() => store.Mutate(state =>
        {
            Dictionary<string, decimal> split = new() { ["retain"] = retain / 100m, ["reserve"] = reserve / 100m };

            foreach (KeyValuePair<string, decimal> allocation in allocations.Where(allocation => allocation.Value != 0m))
            {
                split.Add(allocation.Key, allocation.Value / 100m);
            }

            store.Engine.ConfigureSplit(state, strategy, split);
        }));
    }

    public IActionResult OnPostSettings(string strategy, decimal takeProfit, decimal partial, decimal trail, decimal stop, int holdSeconds, decimal tradeSize)
    {
        return Change(() => store.Mutate(state =>
        {
            StrategyAccount account = state.Strategies[strategy];

            if (account.TrialCompleted || account.Positions.Count != 0 || account.Orders.Count != 0)
            {
                throw new ArgumentException("Close positions before changing rules; completed trials are read-only.");
            }

            StrategySettings updated = account.Settings with
            {
                Version = account.Settings.Version + 1, TakeProfitFraction = takeProfit / 100m, PartialFraction = partial / 100m, TrailFraction = trail / 100m,
                StopFraction = stop / 100m, HoldSeconds = holdSeconds, TradeSizeSol = tradeSize
            };

            updated.Validate();
            account.Settings = updated;
            account.EnteredMarkets.Clear();
            PortfolioEngine.Record(state, account, "Configuration", "", 0m, 0m, System.Text.Json.JsonSerializer.Serialize(updated));
        }));
    }

    private IActionResult Change(Action action)
    {
        if (!ModelState.IsValid)
        {
            Notice = "Invalid form values.";

            return RedirectToPage();
        }

        try
        {
            action();
            Notice = "Saved.";
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            Notice = exception.Message;
        }

        return RedirectToPage();
    }
}