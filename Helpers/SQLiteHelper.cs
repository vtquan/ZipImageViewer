﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static ZipImageViewer.TableHelper;

namespace ZipImageViewer
{
    internal static class SQLiteHelper
    {
        internal static readonly Dictionary<Table, TableInfo> Tables =
            new Dictionary<Table, TableInfo>() {
                { Table.Thumbs,          new TableInfo(Table.Thumbs) },
                { Table.MappedPasswords, new TableInfo(Table.MappedPasswords) },
            };

        /// <summary>
        /// Returns an array of objects containing the return value from each Func.
        /// Errors will be ignored and the next callback will be executed;
        /// </summary>
        internal static object[] Execute(Table t, params Func<TableInfo, SQLiteConnection, object>[] callbackFuncs) {
            SQLiteConnection con = null;
            var affected = new object[callbackFuncs.Length];
            var table = Tables[t];

            Monitor.Enter(table.Lock);
            try {
                con = new SQLiteConnection($"Data Source={table.FullPath};Version=3;");
                con.Open();
                for (int i = 0; i < callbackFuncs.Length; i++) {
                    try { affected[i] = callbackFuncs[i].Invoke(table, con); }
                    catch { }
                }
            }
            finally {
                if (con != null) {
                    con.Close();
                    con.Dispose();
                }
                Monitor.Exit(table.Lock);
            }
            return affected;
        }

        internal static int AddToThumbDB(ImageSource source, string path, System.Drawing.Size decodeSize) {
            if (!(source is BitmapSource bs)) throw new NotSupportedException();

            object[] affected = null;
            byte[] png;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using (var ms = new MemoryStream()) {
                enc.Save(ms);
                png = ms.ToArray();
            }
            if (png.Length == 0) return 0;

            affected = Execute(Table.Thumbs, (table, con) => {
                using (var cmd = new SQLiteCommand(con)) {
                    //remove existing
                    cmd.CommandText = $@"delete from {table.Name} where {Column.VirtualPath} = @path";
                    cmd.Parameters.Add(new SQLiteParameter("@path", DbType.String) { Value = path });
                    cmd.ExecuteNonQuery();
                    //insert new
                    cmd.CommandText = $@"insert into {table.Name}
({Column.VirtualPath}, {Column.DecodeWidth}, {Column.DecodeHeight}, {Column.ThumbData}) values
(@path, {decodeSize.Width}, {decodeSize.Height}, @png)";
                    cmd.Parameters.Add(new SQLiteParameter("@png", DbType.Binary) { Value = png });
                    return cmd.ExecuteNonQuery();
                }
            });

            return (int)affected[0];
        }

        /// <summary>
        /// Returns null if thumb either does not exist in DB or has different size.
        /// </summary>
        internal static BitmapSource GetFromThumbDB(string path, System.Drawing.Size decodeSize) {
            var png = Execute(Table.Thumbs, (table, con) => {
                byte[] pngByte = null;
                using (var cmd = new SQLiteCommand(con)) {
                    cmd.CommandText =
$@"select {Column.ThumbData} from {table.Name} where
{Column.VirtualPath} = @path and
{Column.DecodeWidth} = {decodeSize.Width} and
{Column.DecodeHeight} = {decodeSize.Height}";
                    cmd.Parameters.Add(new SQLiteParameter("@path", DbType.String) { Value = path });
                    using (var reader = cmd.ExecuteReader()) {
                        while (reader.Read()) {
                            pngByte = (byte[])reader["thumbData"];
                            break;
                        }
                    }
                }
                return pngByte;
            });

            if (png.Length == 0 || png[0] == null || ((byte[])png[0]).Length == 0) return null;
            using (var ms = new MemoryStream((byte[])png[0])) {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
    }
}
