using System.Reflection;
using System.Text.Json.Serialization;
using Read2Me.App;
using Xunit;

namespace Read2Me.Tests.Architecture;

/// <summary>
/// Enums take one form on the HTTP wire (#166): their member names, set once by the shared
/// <see cref="App.Api.ApiJson"/> options. A converter on a type or a property would give that enum a
/// second form (camelCase, integers) and, on a nullable property, leak <c>null</c> into its schema. So
/// none may remain in the host, and no enum the host can send may carry a converter or renamed members.
/// Stored JSON (the attribution chain's entries) keeps its own converters: it is not the wire.
/// </summary>
public class EnumWireFormTests
{
    private static readonly Assembly Host = typeof(Startup).Assembly;

    private static IEnumerable<Assembly> Read2MeAssemblies() =>
        Host.GetReferencedAssemblies()
            .Where(a => a.Name?.StartsWith("Read2Me.", StringComparison.Ordinal) == true)
            .Select(Assembly.Load)
            .Append(Host);

    private static bool IsEnumConverter(JsonConverterAttribute attribute)
    {
        for (var type = attribute.ConverterType; type is not null; type = type.BaseType)
        {
            if (type == typeof(JsonStringEnumConverter)) return true;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)) return true;
        }
        return false;
    }

    [Fact]
    public void No_host_type_or_property_carries_an_enum_converter()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in Host.GetTypes())
        {
            if (type.GetCustomAttributes<JsonConverterAttribute>().Any(IsEnumConverter))
                offenders.Add(type.FullName!);
            offenders.AddRange(type.GetProperties(all)
                .Where(p => p.GetCustomAttributes<JsonConverterAttribute>().Any(IsEnumConverter))
                .Select(p => $"{type.FullName}.{p.Name}"));
        }

        Assert.True(offenders.Count == 0,
            "Enum converters in the host give an enum a second wire form; ApiJson sets the only one:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_enum_carries_its_own_converter_or_renamed_members()
    {
        var offenders = Read2MeAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsEnum)
            .Where(t => t.GetCustomAttributes<JsonConverterAttribute>().Any(IsEnumConverter)
                        || t.GetFields(BindingFlags.Public | BindingFlags.Static)
                            .Any(f => f.IsDefined(typeof(JsonStringEnumMemberNameAttribute))))
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(offenders.Count == 0,
            "An enum's wire form is its member names, set by ApiJson; these override it:\n  " +
            string.Join("\n  ", offenders));
    }
}
