using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolastaBot.Data.Integrity;

public sealed record IntegrityReport(int Count, DateTime? First, DateTime? Last, int MissingCount, IReadOnlyList<DateTime> MissingSample, IReadOnlyList<DateTime> Duplicates,
    IReadOnlyList<DateTime> OutOfOrder, IReadOnlyList<DateTime> Malformed)
{
    public bool IsClean => MissingCount == 0 && Duplicates.Count == 0 && OutOfOrder.Count == 0 && Malformed.Count == 0;

    public string Describe()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{Count} bars");

        if (First is not null && Last is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $" from {First:yyyy-MM-dd HH:mm} to {Last:yyyy-MM-dd HH:mm}");
        }

        if (IsClean)
        {
            return text.Append(", no gaps or duplicates.").ToString();
        }

        text.Append(':');
        Append(text, "missing", MissingCount);
        Append(text, "duplicated", Duplicates.Count);
        Append(text, "out of order", OutOfOrder.Count);
        Append(text, "malformed", Malformed.Count);

        if (MissingSample.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" First gap at {MissingSample[0]:yyyy-MM-dd HH:mm}.");
        }

        return text.ToString();

        static void Append(StringBuilder text, string label, int count)
        {
            if (count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" {count} {label};");
            }
        }
    }
}