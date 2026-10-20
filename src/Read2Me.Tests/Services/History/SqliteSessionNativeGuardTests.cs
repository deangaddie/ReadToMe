using Microsoft.Data.Sqlite;
using Read2Me.Services.History;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// History records each step as a patchset from the SQLite session extension, which the shipped
/// e_sqlite3 exports but SQLitePCLRaw does not bind (ADR 0016). A bundle upgrade built without
/// <c>SQLITE_ENABLE_SESSION</c> or <c>SQLITE_ENABLE_PREUPDATE_HOOK</c> fails here instead of
/// silently recording nothing.
/// </summary>
public class SqliteSessionNativeGuardTests
{
    [Fact]
    public void ASessionOnTheShippedSqlite_RecordsAPatchsetOfAWrite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE Things (Id INTEGER PRIMARY KEY, Name TEXT)");

        using var session = SqliteSession.Create(connection);
        session.Attach("Things");
        Assert.Empty(session.Patchset());

        Execute(connection, "INSERT INTO Things (Id, Name) VALUES (1, 'one')");

        Assert.NotEmpty(session.Patchset());
    }

    [Fact]
    public void ASessionIgnoresTablesItWasNotAttachedTo()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE Things (Id INTEGER PRIMARY KEY, Name TEXT)");
        Execute(connection, "CREATE TABLE Results (Id INTEGER PRIMARY KEY, Score REAL)");

        using var session = SqliteSession.Create(connection);
        session.Attach("Things");
        Execute(connection, "INSERT INTO Results (Id, Score) VALUES (1, 0.5)");

        Assert.Empty(session.Patchset());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
