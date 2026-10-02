using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mailserver.Core.Data;

internal static class SqliteExtensions
{
    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        connection.Execute(sql, null, parameters);

    public static int Execute(this SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        connection.Scalar(sql, null, parameters);

    public static object? Scalar(this SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) =>
        connection.Query(sql, null, map, parameters);

    public static List<T> Query<T>(this SqliteConnection connection, string sql, SqliteTransaction? transaction, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }

    public static string ToDbTime(this DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset GetDbTime(this SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
