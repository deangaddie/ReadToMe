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

    /// <summary>
    /// Applies a <see cref="Patchset"/> to the main database of an open connection, in order. Any
    /// conflict — a row a change expects that is not there, or a constraint it breaks — aborts the
    /// apply and throws: a patchset replayed onto the state it was recorded against has none.
    /// </summary>
    public static void Apply(SqliteConnection connection, byte[] patchset)
    {
        var db = connection.Handle?.DangerousGetHandle()
            ?? throw new InvalidOperationException("Applying a patchset needs an open connection.");
        NativeMethods.ConflictHandler abort = (_, _, _) => SqliteChangesetAbort;
        var rc = NativeMethods.sqlite3changeset_apply(db, patchset.Length, patchset, IntPtr.Zero, abort, IntPtr.Zero);
        GC.KeepAlive(abort);
        Check(rc, "sqlite3changeset_apply");
    }

    /// <summary>
    /// Records into this session the changes that turn <paramref name="table"/> in the attached
    /// database <paramref name="fromSchema"/> into the same table in main, its rows compared by
    /// primary key: what <see cref="Patchset"/> then answers is the difference. Both tables must
    /// have the same columns and primary key.
    /// </summary>
    public void Diff(string fromSchema, string table)
    {
        var rc = NativeMethods.sqlite3session_diff(Live, fromSchema, table, out var error);
        try
        {
            if (rc != SqliteOk)
                throw new InvalidOperationException(
                    $"sqlite3session_diff of {table} failed with SQLite error {rc}: {Marshal.PtrToStringUTF8(error)}");
        }
        finally
        {
            NativeMethods.sqlite3_free(error);
        }
    }

    /// <summary>
    /// Applies a <see cref="Diff"/> patchset onto a database with foreign keys on, inside the
    /// caller's transaction. A row a change expects that is already gone was removed by the cascade
    /// of a delete earlier in the same patchset, which the patchset removes too: it is skipped. Any
    /// other conflict aborts the apply and throws.
    /// </summary>
    public static void ApplyOverCascades(SqliteConnection connection, byte[] patchset)
    {
        var db = connection.Handle?.DangerousGetHandle()
            ?? throw new InvalidOperationException("Applying a patchset needs an open connection.");
        NativeMethods.ConflictHandler handler = (_, conflict, _) =>
            conflict == SqliteChangesetNotFound ? SqliteChangesetOmit : SqliteChangesetAbort;
        var rc = NativeMethods.sqlite3changeset_apply(db, patchset.Length, patchset, IntPtr.Zero, handler, IntPtr.Zero);
        GC.KeepAlive(handler);
        Check(rc, "sqlite3changeset_apply");
    }

    private const int SqliteChangesetNotFound = 2;
    private const int SqliteChangesetOmit = 0;
    private const int SqliteChangesetAbort = 2;

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
            // The low byte is the primary result code (SQLITE_FULL, SQLITE_BUSY, …) callers classify by.
            throw new SqliteException($"{call} failed with SQLite error {rc}.", rc & 0xFF, rc);
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
        public static extern int sqlite3session_diff(
            IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string fromDb,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string table, out IntPtr error);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3session_delete(IntPtr session);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ConflictHandler(IntPtr context, int conflict, IntPtr iterator);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3changeset_apply(
            IntPtr db, int size, byte[] changeset, IntPtr filter, ConflictHandler conflict, IntPtr context);
    }
}
