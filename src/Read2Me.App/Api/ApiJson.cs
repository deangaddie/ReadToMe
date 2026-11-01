using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.AspNetCore.Http;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The HTTP wire form (#166): every enum is its C# member name, PascalCase, in both directions, and
    /// numbers are JSON numbers only. A request that sends an enum as an integer, or a number as a string,
    /// fails to bind. The agent API (<c>ConfigureHttpJsonOptions</c>), the live hub and
    /// <see cref="BookCommandJson"/> all take their options from here, so no type or property carries an
    /// enum converter of its own.
    /// </summary>
    public static class ApiJson
    {
        public static void Configure(JsonSerializerOptions options)
        {
            options.NumberHandling = JsonNumberHandling.Strict;
            options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        }

        /// <summary>
        /// An enum in a route segment or query value: exactly a member name, case-sensitive. Numbers,
        /// combined flags and other casings are refused, the same as in a body.
        /// </summary>
        public static bool TryParseName<TEnum>(string? value, out TEnum result)
            where TEnum : struct, Enum
        {
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (!string.Equals(candidate.ToString(), value, StringComparison.Ordinal)) continue;
                result = candidate;
                return true;
            }
            result = default;
            return false;
        }

        /// <summary>
        /// A body read by hand, with the same strictness a bound parameter gets: JSON that breaks the wire
        /// form (an enum as a number, a number as a string) comes back as an error to answer with a 400,
        /// rather than an exception that would be a 500. Both null: no body.
        /// </summary>
        public static async Task<(T? Body, string? Error)> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct = default)
        {
            try
            {
                return (await request.ReadFromJsonAsync<T>(ct), null);
            }
            catch (JsonException ex)
            {
                return (default, $"Invalid body: {ex.Message}");
            }
        }
    }
}
