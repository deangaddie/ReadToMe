using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

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
        /// A body read by hand, with the same strictness a bound parameter gets. JSON that breaks the
        /// wire form (an enum as a number, a number as a string) is a 400 "Invalid body", not an
        /// exception that would be a 500. No body is a 400 with <paramref name="missing"/>.
        /// </summary>
        public static async Task<BodyRead<T>> ReadBodyAsync<T>(HttpRequest request, string missing, CancellationToken ct = default)
            where T : class
        {
            try
            {
                return await request.ReadFromJsonAsync<T>(ct) is { } body
                    ? new BodyRead<T>(body, null)
                    : new BodyRead<T>(null, BadRequest(missing));
            }
            catch (JsonException ex)
            {
                return new BodyRead<T>(null, BadRequest($"Invalid body: {ex.Message}"));
            }
        }

        private static ProblemHttpResult BadRequest(string detail) =>
            TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>What <see cref="ApiJson.ReadBodyAsync{T}"/> read: the body, or the 400 to answer instead.</summary>
    public readonly record struct BodyRead<T>(T? Body, ProblemHttpResult? Problem) where T : class
    {
        /// <summary>True with the 400 to answer; false with the body.</summary>
        public bool Failed([NotNullWhen(false)] out T? body, [NotNullWhen(true)] out ProblemHttpResult? problem)
        {
            body = Body;
            problem = Problem;
            return Problem is not null;
        }
    }
}
