namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>Hands out HttpClients backed by the single routing fake handler.</summary>
public sealed class FakeHttpClientFactory(FakeAiRoutingHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
