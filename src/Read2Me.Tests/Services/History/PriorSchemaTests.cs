using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Read2Me.Data;
using Read2Me.Services.History;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// The old-schema helpers take a project back to the migration before the latest, which holds
/// only while the latest leaves every state table alone (ADR 0016, "Snapshots").
/// </summary>
public class PriorSchemaTests
{
    private static readonly string[] NotState = ["AudioReviews", "StepCheckpoints"];

    [Fact]
    public void ThePrior_IsTheMigrationBeforeTheLatest()
    {
        var prior = PriorSchema.PriorOf(
        [
            new("20260101000000_First", [new CreateTableOperation { Name = "Characters" }]),
            new("20260201000000_Second", [new AddColumnOperation { Table = "Characters", Name = "Age" }]),
            new("20260301000000_Third", [new CreateTableOperation { Name = "StepCheckpoints" }]),
        ], NotState);

        Assert.Equal("20260201000000_Second", prior);
    }

    [Fact]
    public void ALatestMigrationThatTouchesAStateTable_FailsWithWhatToDo()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => PriorSchema.PriorOf(
        [
            new("20260101000000_First", [new CreateTableOperation { Name = "StepCheckpoints" }]),
            new("20260201000000_Second", [new AddColumnOperation { Table = "Characters", Name = "Age" }]),
        ], NotState));

        Assert.Contains("20260201000000_Second", failure.Message);
        Assert.Contains("Characters", failure.Message);
        Assert.Contains(nameof(PriorSchema), failure.Message);
    }

    [Fact]
    public void ALatestMigrationThatCreatesAStateTable_HasAPrior_SinceNoChunkOnThePriorSchemaHoldsItsRows()
    {
        var prior = PriorSchema.PriorOf(
        [
            new("20260101000000_First", [new CreateTableOperation { Name = "Characters" }]),
            new("20260201000000_Second",
            [
                new CreateTableOperation { Name = "Pronunciations" },
                new CreateIndexOperation { Table = "Pronunciations", Name = "IX_Pronunciations_WrittenForm" },
            ]),
        ], NotState);

        Assert.Equal("20260101000000_First", prior);
    }

    [Fact]
    public void ALatestMigrationWithRawSql_FailsToo_SinceWhatItTouchesIsUnknown()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => PriorSchema.PriorOf(
        [
            new("20260101000000_First", [new CreateTableOperation { Name = "Characters" }]),
            new("20260201000000_Second", [new SqlOperation { Sql = "UPDATE Characters SET Name = ''" }]),
        ], NotState));

        Assert.Contains("20260201000000_Second", failure.Message);
    }

    [Fact]
    public void TheProjectsOwnMigrations_HaveAPrior()
    {
        Assert.NotEmpty(PriorSchema.MigrationId);
    }

    [Fact]
    public void TheTablesItTreatsAsNotState_AreTheOnesHistoryNeverRecords()
    {
        using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite("Data Source=:memory:").Options);

        Assert.Equal(ProjectHistory.NotStateTables(db).Order(), PriorSchema.NotStateTables.Order());
    }
}
