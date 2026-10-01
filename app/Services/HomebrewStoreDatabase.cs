using Microsoft.Data.Sqlite;
using System.Text.Json;
using PkgRelay.Models;
namespace PkgRelay.Services;

internal static class HomebrewStoreDatabase
{
    internal static HomebrewItem[] Read(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM homebrews LIMIT 20000";
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, string>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
            row["source_name"] = "PKG-Zone";
            rows.Add(row);
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        var items = HomebrewCatalog.Parse(json.RootElement, new Uri("https://api.pkg-zone.com/"));
        if (items.Length == 0) throw new InvalidDataException("The database has no usable PS4 package links.");
        return items;
    }
}
