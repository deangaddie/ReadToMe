using FractionalIndexing;

namespace Read2Me.Core.Utils;

public static class OrderHelper
{
    public static string GetNextOrder(string? currentMax)
    {
        return OrderKeyGenerator.GenerateKeyBetween(currentMax, null);
    }

    public static string GetBefore(string? first)
    {
        return OrderKeyGenerator.GenerateKeyBetween(null, first);
    }

    public static string GetBetween(string? a, string? b)
    {
        return OrderKeyGenerator.GenerateKeyBetween(a, b);
    }
}
