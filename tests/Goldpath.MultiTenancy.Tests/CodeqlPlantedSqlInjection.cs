using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goldpath.Tests;

// PLANTED for #339's proof: SQL built from a request string. CodeQL must flag this; the next
// commit removes it.
internal static class CodeqlPlantedSqlInjection
{
    public static void Map(WebApplication app) => app.MapGet("/planted", async (HttpContext http) =>
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM customers WHERE name = '" + http.Request.Query["name"] + "'";
        return (string?)await command.ExecuteScalarAsync();
    });
}
