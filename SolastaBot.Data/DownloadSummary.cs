using System;
using System.Collections.Generic;
using SolastaBot.Data.Integrity;

namespace SolastaBot.Data;

public sealed record DownloadSummary(string Symbol, string Interval, DateOnly From, DateOnly To, int MonthsPulled, int MonthsSkipped, IReadOnlyList<DateOnly> MonthsUnavailable,
    int BarsStored, int FundingStored, IntegrityReport Integrity);