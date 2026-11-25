using System.Runtime.InteropServices;
using System.Text;

namespace MusicToppin;

internal sealed class SqliteRow
{
    public string[] Text = Array.Empty<string>();
    public byte[] Blob;
}

/// <summary>
/// 只读 SQLite：用 Windows 10/11 自带的 winsqlite3.dll，避免给应用加 SQLite 依赖。
/// 主要用来读各客户端的 cookie 库（网易云 / QQ 音乐都是 Chromium 系）。
/// </summary>
internal static class Sqlite
{
    private static bool? _available;

    public static bool Available
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winsqlite3.dll");
                _available = File.Exists(path);
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>执行查询；blobColumn 指定要把哪一列按 blob 取出来（-1 = 不取）。</summary>
    public static List<SqliteRow> Query(string dbPath, string sql, int blobColumn = -1)
    {
        var rows = new List<SqliteRow>();
        if (!Available || string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return rows;

        IntPtr db;
        if (sqlite3_open_v2(ToUtf8(dbPath), out db, 1, IntPtr.Zero) != 0) return rows;

        try
        {
            IntPtr stmt;
            if (sqlite3_prepare_v2(db, ToUtf8(sql), -1, out stmt, IntPtr.Zero) != 0) return rows;

            try
            {
                while (sqlite3_step(stmt) == 100)
                {
                    var row = new SqliteRow();
                    var count = sqlite3_column_count(stmt);
                    row.Text = new string[count];

                    for (var i = 0; i < count; i++)
                    {
                        var pointer = sqlite3_column_text(stmt, i);
                        if (pointer == IntPtr.Zero)
                        {
                            row.Text[i] = null;
                            continue;
                        }

                        var length = sqlite3_column_bytes(stmt, i);
                        if (length <= 0)
                        {
                            row.Text[i] = "";
                            continue;
                        }

                        var bytes = new byte[length];
                        Marshal.Copy(pointer, bytes, 0, length);
                        row.Text[i] = Encoding.UTF8.GetString(bytes);
                    }

                    if (blobColumn >= 0 && blobColumn < count)
                    {
                        var length = sqlite3_column_bytes(stmt, blobColumn);
                        var pointer = sqlite3_column_blob(stmt, blobColumn);
                        if (length > 0 && pointer != IntPtr.Zero)
                        {
                            row.Blob = new byte[length];
                            Marshal.Copy(pointer, row.Blob, 0, length);
                        }
                    }

                    rows.Add(row);
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        finally
        {
            sqlite3_close(db);
        }

        return rows;
    }

    private static byte[] ToUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var zero = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, zero, 0, bytes.Length);
        return zero;
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
}
