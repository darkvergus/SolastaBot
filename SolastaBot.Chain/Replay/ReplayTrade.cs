namespace SolastaBot.Chain.Replay;

public sealed record ReplayTrade(string Mint, string Arm, decimal AdmissionProbability, ReplayOutcome Outcome, decimal? EnteredAt, decimal? ExitSignalAt,
    decimal? ExitedAt, decimal EntryCostSol, decimal? ProceedsSol, decimal? NetPnlSol, decimal? StressPnlSol)
{
    public decimal? NetReturn => EntryCostSol > 0m ? NetPnlSol / EntryCostSol : null;
}
