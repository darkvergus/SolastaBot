using SolastaBot.Chain.Execution;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class ConnectedSession(ConnectedStateStore store, IConnectedOrderRouter router, ConnectedOptions options, ConnectedState initial)
{
    public ConnectedState State { get; private set; } = initial;

    public async Task SaveAsync(ConnectedState state, string kind, CancellationToken cancellationToken)
    {
        State = await store.SaveAsync(state, kind, cancellationToken);
    }

    public async Task ReconcileAsync(Func<TradingControl> control, CancellationToken cancellationToken)
    {
        foreach (ExecutionOrder order in State.Orders.Where(order => order.Pending).ToArray())
        {
            ExecutionReceipt? receipt = await router.ReceiptAsync(order, cancellationToken);
            if (receipt is not null)
            {
                await ApplyAsync(order, receipt, cancellationToken);
                continue;
            }
            if (await router.ExpiredAsync(order, cancellationToken))
            {
                WalletSnapshot wallet = await router.WalletAsync(cancellationToken);
                if (Matches(wallet, State) && wallet.Lamports == State.WalletLamports)
                {
                    await SetOrderAsync(order with { Status = OrderStatus.Expired, Detail = "Finalized block height passed expiry; history has no signature and holdings are unchanged." }, cancellationToken);
                }
                else
                {
                    await SetOrderAsync(order with { Status = OrderStatus.Unresolved, Detail = "Expired signature with unexpected wallet changes; replacement remains blocked." }, cancellationToken);
                }

                continue;
            }
            TradingControl latest = control();
            if (order.Intent.Side == OrderSide.Buy && (latest.HaltEntries || latest.Flatten || State.DailyHalt))
            {
                continue;
            }

            try
            {
                await router.SubmitAsync(order, () => order.Intent.Side == OrderSide.Sell || !State.DailyHalt && !control().HaltEntries && !control().Flatten, cancellationToken);
                await SetOrderAsync(order with { Status = OrderStatus.Submitted, Detail = "Awaiting finalized transaction receipt." }, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await SetOrderAsync(order with { Status = OrderStatus.Unresolved, Detail = "Submission outcome unknown; the persisted signature must be resolved." }, cancellationToken);
                throw;
            }
        }
        if (State.Orders.Any(order => order.Pending))
        {
            return;
        }

        WalletSnapshot snapshot = await router.WalletAsync(cancellationToken);
        bool initialFunding = State.Orders.Count == 0 && State.Positions.Count == 0 && State.NetWalletChangeLamports == 0;
        string? error = State.ExecutionFault ?? (!Matches(snapshot, State) ? "Wallet token holdings differ from confirmed positions." : State.HasReconciled && !initialFunding && snapshot.Lamports != State.WalletLamports
            ? "Wallet SOL changed outside the journal. New entries are blocked." : null);
        if (!State.HasReconciled || error != State.ReconciliationError || initialFunding && snapshot.Lamports != State.WalletLamports)
        {
            await SaveAsync(State with { HasReconciled = true, WalletLamports = !State.HasReconciled || initialFunding ? snapshot.Lamports : State.WalletLamports, ReconciliationError = error }, "Reconciliation", cancellationToken);
        }
    }

    public async Task ExecuteAsync(OrderIntent intent, Func<TradingControl> control, CancellationToken cancellationToken)
    {
        if (State.Orders.Any(order => order.Pending))
        {
            throw new InvalidOperationException("Resolve the pending transaction before creating another.");
        }

        if (State.Orders.Any(order => order.Intent.Id == intent.Id))
        {
            throw new InvalidOperationException("Order ID has already been recorded.");
        }

        await ReconcileAsync(control, cancellationToken);
        if (!State.HasReconciled || State.ReconciliationError is not null)
        {
            throw new InvalidOperationException(State.ReconciliationError ?? "Wallet has not been reconciled.");
        }

        ConnectedState observed = ConnectedRiskPolicy.Observe(State, intent.RequestedAt, options.Strategy);
        if (observed != State)
        {
            await SaveAsync(observed, "RiskObservation", cancellationToken);
        }

        CheckRisk(intent, control());
        ExecutionOrder prepared = await router.PrepareAsync(intent, cancellationToken);
        if (prepared.Intent != intent || prepared.Status != OrderStatus.Signed)
        {
            throw new InvalidOperationException("Router returned a different order.");
        }

        CheckReserve(prepared);
        string simulation = await router.SimulateAsync(prepared, cancellationToken);
        CheckRisk(intent, control());
        ExecutionOrder persisted = options.Mode == "Simulate" ? prepared with { Status = OrderStatus.Simulated, Detail = simulation, Transaction = string.Empty } : prepared;
        await SaveAsync(State with { Orders = [.. State.Orders, persisted] }, options.Mode == "Simulate" ? "Simulated" : "SignedBeforeSubmission", cancellationToken);
        if (options.Mode == "Devnet")
        {
            await ReconcileAsync(control, cancellationToken);
        }
    }

    private void CheckRisk(OrderIntent intent, TradingControl control)
    {
        if (intent.Amount == 0)
        {
            throw new InvalidOperationException("Order amount must be positive.");
        }

        if (intent.Side == OrderSide.Buy)
        {
            if (control.HaltEntries || control.Flatten || State.DailyHalt)
            {
                throw new InvalidOperationException("Entries are halted.");
            }

            if (State.Positions.Count >= options.Strategy.MaxPositions || State.Positions.Any(position => position.Mint == intent.Mint))
            {
                throw new InvalidOperationException("Position limit or duplicate mint.");
            }

            if (State.Positions.Any(position => intent.RequestedAt - position.MarkedAt > options.Strategy.Execution.MaxObservationGapSeconds))
            {
                throw new InvalidOperationException("Existing holdings have stale quotes.");
            }

            if (intent.Amount > options.Strategy.Execution.TradeSizeSol * 1_000_000_000m)
            {
                throw new InvalidOperationException("Buy exceeds the configured trade size.");
            }

            decimal cost = intent.Amount / 1_000_000_000m + options.FeeReserveLamports / 1_000_000_000m;
            if (cost > Math.Max(0m, State.EquitySol(options.Strategy.Capital.ChainCapitalSol)) * options.Strategy.MaxPositionFraction)
            {
                throw new InvalidOperationException("Position allocation exceeded.");
            }
        }
        else
        {
            ConfirmedPosition? position = State.Positions.SingleOrDefault(position => position.Mint == intent.Mint);
            if (position is null || intent.Amount > position.Tokens)
            {
                throw new InvalidOperationException("Sell exceeds confirmed holdings.");
            }
        }
    }

    private void CheckReserve(ExecutionOrder order)
    {
        ulong expected = checked(options.FeeReserveLamports + (order.Intent.Side == OrderSide.Buy ? order.Intent.Amount : 0));
        if (order.ReservedLamports != expected || expected > State.WalletLamports)
        {
            throw new InvalidOperationException("Insufficient wallet SOL for the order and fee reserve.");
        }

        decimal allocatedCash = options.Strategy.Capital.ChainCapitalSol + State.NetWalletChangeLamports / 1_000_000_000m;
        if (expected / 1_000_000_000m > allocatedCash)
        {
            throw new InvalidOperationException("Order cannot use the other sleeve's allocation.");
        }
    }

    private async Task ApplyAsync(ExecutionOrder order, ExecutionReceipt receipt, CancellationToken cancellationToken)
    {
        List<ConfirmedPosition> positions = [.. State.Positions];
        string? mismatch = null;
        if (receipt.Failed)
        {
            if (receipt.TokenChange != 0)
            {
                mismatch = "Failed transaction reports changed tokens.";
            }
        }
        else if (order.Intent.Side == OrderSide.Buy)
        {
            if (receipt.TokenChange <= 0 || (ulong)receipt.TokenChange < order.MinimumOutput || receipt.WalletChangeLamports >= 0)
            {
                mismatch = "Buy receipt violates output or debit bounds.";
            }
            else
            {
                positions.Add(new(order.Intent.Mint, (ulong)receipt.TokenChange, -receipt.WalletChangeLamports / 1_000_000_000m, order.Intent.RequestedAt, 0m, order.Intent.RequestedAt));
            }
        }
        else
        {
            ConfirmedPosition? position = positions.SingleOrDefault(position => position.Mint == order.Intent.Mint);
            if (position is null || receipt.TokenChange >= 0 || -receipt.TokenChange > (decimal)order.Intent.Amount || -receipt.TokenChange > (decimal)position.Tokens)
            {
                mismatch = "Sell receipt does not match holdings.";
            }
            else
            {
                positions.Remove(position);
                ulong remaining = checked(position.Tokens - (ulong)-receipt.TokenChange);
                if (remaining > 0)
                {
                    positions.Add(position with { Tokens = remaining, CostSol = position.CostSol * remaining / position.Tokens, MarkSol = 0m });
                }
            }
        }
        if (receipt.WalletChangeLamports < -(decimal)order.ReservedLamports || receipt.FeeLamports > options.FeeReserveLamports)
        {
            mismatch = "Confirmed transaction exceeded the reserved debit or network fee.";
        }

        WalletSnapshot wallet = await router.WalletAsync(cancellationToken, receipt.Slot);
        ConnectedState next = State with
        {
            Positions = positions,
            NetWalletChangeLamports = checked(State.NetWalletChangeLamports + receipt.WalletChangeLamports),
            WalletLamports = wallet.Lamports,
            HasReconciled = true,
            Orders = [.. State.Orders.Select(record => record.Intent.Id == order.Intent.Id ? record with
            {
                Status = receipt.Failed ? OrderStatus.Failed : OrderStatus.Confirmed,
                Detail = $"Finalized slot={receipt.Slot}; fee={receipt.FeeLamports}; wallet change={receipt.WalletChangeLamports}; token change={receipt.TokenChange}.",
                Transaction = string.Empty,
                Receipt = receipt
            } : record)]
        };
        if (wallet.Lamports != checked((decimal)State.WalletLamports + receipt.WalletChangeLamports))
        {
            mismatch ??= "Wallet balance contains an unexplained external change.";
        }

        if (!Matches(wallet, next))
        {
            mismatch ??= "Confirmed receipt and wallet token holdings disagree.";
        }

        await SaveAsync(next with { ReconciliationError = mismatch, ExecutionFault = mismatch }, receipt.Failed ? "TransactionFailed" : "TransactionConfirmed", cancellationToken);
    }

    public async Task AcknowledgeFaultAsync(CancellationToken cancellationToken)
    {
        if (State.Orders.Any(order => order.Pending))
        {
            throw new InvalidOperationException("Pending signatures must be resolved before acknowledging a fault.");
        }

        WalletSnapshot wallet = await router.WalletAsync(cancellationToken);
        if (!Matches(wallet, State) || wallet.Lamports != State.WalletLamports)
        {
            throw new InvalidOperationException("Wallet and journal still disagree; acknowledgement cannot change holdings or balances.");
        }

        await SaveAsync(State with { ExecutionFault = null, ReconciliationError = null }, "OperatorAcknowledgedFault", cancellationToken);
    }

    private Task SetOrderAsync(ExecutionOrder order, CancellationToken cancellationToken) => SaveAsync(State with
    {
        Orders = [.. State.Orders.Select(existing => existing.Intent.Id == order.Intent.Id ? order : existing)]
    }, order.Status.ToString(), cancellationToken);

    private static bool Matches(WalletSnapshot wallet, ConnectedState state) => wallet.Tokens.Count == state.Positions.Count &&
        state.Positions.All(position => wallet.Tokens.TryGetValue(position.Mint, out ulong balance) && balance == position.Tokens);
}
