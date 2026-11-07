using System.Text.Json;
using Read2Me.App.Live;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// The hub's messages take the HTTP wire form (ADR 0017, #238): enums as member names and nulls sent,
/// so the generated <c>T | null</c> types hold; a <c>[Flags]</c> facet set travels as a list of single facets.
/// </summary>
public class LiveWireFormTests
{
    private static JsonElement Wire(object message)
    {
        var options = new JsonSerializerOptions();
        LiveServiceCollectionExtensions.ConfigurePayloadJson(options);
        return JsonSerializer.SerializeToElement(message, message.GetType(), options);
    }

    [Fact]
    public void Nulls_are_sent_and_kinds_are_member_names()
    {
        var json = Wire(new WatchdogMessage(WatchdogKind.ServiceHealthy, "llama"));

        Assert.Equal("ServiceHealthy", json.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void Facets_split_into_single_facets()
    {
        var effects = BookMutationEffects.Nothing with { Facets = BookFacets.Structure | BookFacets.Audio };

        Assert.Equal([BookFacets.Structure, BookFacets.Audio], ReceiptEffects.From(effects).Facets);
    }

    [Fact]
    public void All_is_every_facet_and_None_is_none()
    {
        var all = ReceiptEffects.From(BookMutationEffects.Unknown).Facets;

        Assert.DoesNotContain(BookFacets.All, all);
        Assert.DoesNotContain(BookFacets.None, all);
        Assert.Equal(BookFacets.All, all.Aggregate(BookFacets.None, (a, f) => a | f));
        Assert.Empty(ReceiptEffects.From(BookMutationEffects.Nothing).Facets);
    }

    [Fact]
    public void Facets_travel_as_an_array_of_names()
    {
        var effects = BookMutationEffects.Nothing with { Facets = BookFacets.NodeTitle | BookFacets.Voices };

        var facets = Wire(ReceiptEffects.From(effects)).GetProperty("facets");

        Assert.Equal(["Voices", "NodeTitle"], facets.EnumerateArray().Select(f => f.GetString()));
    }
}
