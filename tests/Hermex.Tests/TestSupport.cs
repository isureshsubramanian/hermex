using System.Text;
using Hermex;
using Hermex.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hermex.Tests;

/// <summary>Shared helpers for the test suite.</summary>
internal static class TestSupport
{
    /// <summary>Builds a raw message, normalising every line ending to CRLF as SMTP requires.</summary>
    public static byte[] Raw(string message)
    {
        var normalized = message.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalized);
    }
}

/// <summary>A minimal <see cref="IHostEnvironment"/> for constructing the store in tests.</summary>
internal sealed class StubHostEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Hermex.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Creates a <see cref="SqliteMailStore"/> over a throwaway database file.</summary>
internal sealed class TestStore : IAsyncDisposable
{
    private readonly string _directory;

    private TestStore(SqliteMailStore store, string directory)
    {
        Store = store;
        _directory = directory;
    }

    public SqliteMailStore Store { get; }

    public static async Task<TestStore> CreateAsync(Action<HermexOptions>? configure = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hermex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var options = new HermexOptions { DatabasePath = Path.Combine(directory, "test.db") };
        configure?.Invoke(options);

        var store = new SqliteMailStore(
            options,
            new HermexRuntimeState(),
            new StubHostEnvironment(),
            NullLogger<SqliteMailStore>.Instance);

        await store.InitializeAsync();
        return new TestStore(store, directory);
    }

    public ValueTask DisposeAsync()
    {
        Store.Dispose();
        // SQLite pools connections; clear them so the temp file is no longer locked.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
