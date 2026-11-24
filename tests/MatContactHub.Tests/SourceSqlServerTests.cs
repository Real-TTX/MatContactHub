using MatContactHub.Services.Sources;
using Microsoft.Data.SqlClient;

namespace MatContactHub.Tests;

/// <summary>A test that needs Microsoft SQL Server: it runs when <c>MATCONTACTHUB_TEST_MSSQL</c> holds a connection string, else it is skipped.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SourceSqlServerTests.Server))
        {
            Skip = "MATCONTACTHUB_TEST_MSSQL is not set (a connection string of a SQL Server).";
        }
    }
}

/// <summary>The SQL Server connector against a real server: the rows of a query, the types, duplicate column names, the refused statements, the errors.</summary>
public sealed class SourceSqlServerTests
{
    public static string? Server => Environment.GetEnvironmentVariable("MATCONTACTHUB_TEST_MSSQL");

    private static SqlServerConnector Connector(string query, string? connection = null)
        => new(new SqlServerSettings { Query = query, CommandTimeoutSeconds = 30, TrustServerCertificate = true }, new SourceSecrets { ConnectionString = connection ?? Server });

    [SqlFact]
    public async Task The_rows_of_a_select_become_records()
    {
        const string query = """
            SELECT Id, FirstName, LastName, Born, Amount, Active, Note, Id AS id
            FROM (VALUES
              (1, N'Ada', N'Lovelace', CAST('1815-12-10' AS date), CAST(12.50 AS decimal(9,2)), CAST(1 AS bit), NULL),
              (2, N'Jürgen', N'Groß', CAST('1990-01-02' AS date), CAST(0 AS decimal(9,2)), CAST(0 AS bit), N'x;y')
            ) AS t(Id, FirstName, LastName, Born, Amount, Active, Note)
            ORDER BY 1
            """;
        var records = await SourceTestKit.ReadAllAsync(Connector(query));
        Assert.Equal(2, records.Count);
        Assert.Equal(("1", "Ada", "1815-12-10", "12.50", "true"), (records[0]["Id"], records[0]["firstname"], records[0]["Born"], records[0]["Amount"], records[0]["Active"]));
        Assert.Null(records[0]["Note"]);
        Assert.Equal(("Jürgen", "Groß", "x;y"), (records[1]["FirstName"], records[1]["LastName"], records[1]["Note"]));
        Assert.Equal("1", records[0]["Id_2"]);
    }

    [SqlFact]
    public async Task A_common_table_expression_works()
    {
        var records = await SourceTestKit.ReadAllAsync(Connector("WITH n AS (SELECT 1 AS k UNION ALL SELECT 2) SELECT k FROM n"));
        Assert.Equal(["1", "2"], records.Select(r => r["k"]));
    }

    /// <summary>
    /// The query runs in a transaction of its own (the one that is rolled back) on a connection named for the app. A rollback cannot be proven from here: the
    /// connector lets no statement through that would change something.
    /// </summary>
    [SqlFact]
    public async Task The_query_runs_in_a_transaction_of_the_app()
    {
        var record = Assert.Single(await SourceTestKit.ReadAllAsync(Connector("SELECT @@TRANCOUNT AS TranCount, APP_NAME() AS App")));
        Assert.Equal(("1", "MatContactHub"), (record["TranCount"], record["App"]));
    }

    [SqlFact]
    public async Task Leaving_a_large_result_early_does_not_read_it_to_its_end()
    {
        // billions of rows: read to the end, this takes far longer than the limit below
        var connector = Connector("SELECT a.object_id AS A, b.object_id AS B FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in connector.ReadAsync(CancellationToken.None))
        {
            break;
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The early stop took {watch.Elapsed}.");
    }

    [SqlFact]
    public async Task A_statement_that_changes_something_is_refused_before_it_reaches_the_server()
    {
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector("SELECT 1; CREATE TABLE x (a int)")));
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(Connector("SELECT 1 AS a INTO #copy")));
    }

    [SqlFact]
    public async Task A_wrong_query_is_an_error_of_the_server()
        => await Assert.ThrowsAsync<SqlException>(() => SourceTestKit.ReadAllAsync(Connector("SELECT * FROM dbo.DoesNotExist")));

    [SqlFact]
    public async Task A_wrong_password_is_an_error()
    {
        var wrong = new SqlConnectionStringBuilder(Server) { Password = "wrong-password-1", ConnectTimeout = 5 }.ConnectionString;
        await Assert.ThrowsAsync<SqlException>(() => SourceTestKit.ReadAllAsync(Connector("SELECT 1 AS a", wrong)));
    }

    [Fact]
    public async Task Without_a_connection_string_the_error_is_worded()
    {
        var connector = new SqlServerConnector(new SqlServerSettings { Query = "SELECT 1" }, new SourceSecrets());
        await Assert.ThrowsAsync<SourceException>(() => SourceTestKit.ReadAllAsync(connector));
    }
}
