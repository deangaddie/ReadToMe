using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Read2Me.Services.History;

/// <summary>
/// A SQLite session (https://www.sqlite.org/sessionintro.html) recording the changes made on one
/// open connection, for the tables attached to it. The shipped e_sqlite3 exports the session
/// extension but SQLitePCLRaw does not bind it (ADR 0016), so this is the one place that calls the
/// native symbols; nothing else in the app touches them.
/// <para>
/// The session records only while the connection stays open — a closed connection is a different
/// native handle — so the caller keeps it open from <see cref="Create"/> to <see cref="Dispose"/>.
/// </para>
/// </summary>
internal sealed class SqliteSession : IDisposable
{
    private const int SqliteOk = 0;
    private IntPtr _session;

    private SqliteSession(IntPtr session) => _session = session;

    /// <summary>Starts recording changes to the main database of an open connection.</summary>
    public static SqliteSession Create(SqliteConnection connection)
    {
        var db = connection.Handle?.DangerousGetHandle()
            ?? throw new InvalidOperationException("A session needs an open connection.");
        Check(NativeMethods.sqlite3session_create(db, "main", out var session), "sqlite3session_create");
        return new SqliteSession(session);
    }

    /// <summary>Records changes to one table. A table with no PRIMARY KEY is silently not recorded.</summary>
    public void Attach(string table) =>
        Check(NativeMethods.sqlite3session_attach(Live, table), "sqlite3session_attach");

    /// <summary>
    /// The patchset of everything recorded so far: inserted rows, the primary keys of deleted rows,
    /// and the new values of updated columns. Empty when nothing recorded changed.
    /// </summary>
    public byte[] Patchset()
    {
        Check(NativeMethods.sqlite3session_patchset(Live, out var size, out var buffer), "sqlite3session_patchset");
        try
        {
            var bytes = new byte[size];
            if (size > 0)
                Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.sqlite3_free(buffer);
        }
    }

    public void Dispose()
    {
        if (_session == IntPtr.Zero)
            return;
        NativeMethods.sqlite3session_delete(_session);
        _session = IntPtr.Zero;
    }

    private IntPtr Live => _session != IntPtr.Zero ? _session : throw new ObjectDisposedException(nameof(SqliteSession));

    private static void Check(int rc, string call)
    {
        if (rc != SqliteOk)
            throw new InvalidOperationException($"{call} failed with SQLite error {rc}.");
    }

    private static class NativeMethods
    {
        private const string Library = "e_sqlite3";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3session_create(
            IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, out IntPtr session);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3session_attach(
            IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string table);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3session_patchset(IntPtr session, out int size, out IntPtr buffer);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3session_delete(IntPtr session);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr buffer);
    }
}
