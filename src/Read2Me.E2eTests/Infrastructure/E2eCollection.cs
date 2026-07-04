namespace Read2Me.E2eTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class E2eCollection : ICollectionFixture<E2eAppFixture>
{
    public const string Name = "e2e";
}
