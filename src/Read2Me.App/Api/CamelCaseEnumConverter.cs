using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.App.Api
{
    /// <summary>
    /// A string enum converter that writes and reads camelCase names, for enums whose wire values
    /// are lower-case ids (<c>structure</c>, <c>confirmed</c>). An attribute cannot pass a naming
    /// policy, hence the subclass.
    /// </summary>
    public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase)
        where TEnum : struct, Enum;
}
