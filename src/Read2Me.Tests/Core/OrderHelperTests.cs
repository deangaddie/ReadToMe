using Read2Me.Core.Utils;
using Xunit;

namespace Read2Me.Tests.Core;

public class OrderHelperTests
{
    [Fact]
    public void GetNextOrder_ReturnsHigherKey()
    {
        var key1 = OrderHelper.GetNextOrder(null);
        var key2 = OrderHelper.GetNextOrder(key1);
        
        Assert.True(string.Compare(key2, key1, StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void GetBefore_ReturnsLowerKey()
    {
        var first = OrderHelper.GetNextOrder(null);
        var before = OrderHelper.GetBefore(first);
        
        Assert.True(string.Compare(before, first, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public void GetBetween_ReturnsIntermediateKey()
    {
        var a = OrderHelper.GetNextOrder(null);
        var b = OrderHelper.GetNextOrder(a);
        var between = OrderHelper.GetBetween(a, b);
        
        Assert.True(string.Compare(between, a, StringComparison.Ordinal) > 0);
        Assert.True(string.Compare(between, b, StringComparison.Ordinal) < 0);
    }

    [Fact]
    public void GetBetween_WithNulls_ReturnsInitialKey()
    {
        var key = OrderHelper.GetBetween(null, null);
        Assert.NotNull(key);
    }
}
