namespace SolastaBot.Host.Portfolio;

public sealed record PortfolioOrder(string Market, decimal RequestedAt, decimal ExecuteAfter, decimal ExpiresAt, decimal Reserved, int Direction = 1, decimal StopDistance = 0m);
