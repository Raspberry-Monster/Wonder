using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wonder.Core;

namespace Wonder.Infrastructure;

public static class EnumModelConfiguration
{
    public static void Apply(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes().ToArray())
        foreach (var property in entity.GetProperties().ToArray())
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!type.IsEnum) continue;
            var asString = type.IsDefined(typeof(StringStoredEnumAttribute));
            if (asString)
                property.SetValueConverter(
                    (ValueConverter)Activator.CreateInstance(typeof(StableEnumConverter<>).MakeGenericType(type))!);
            var values = Enum.GetValues(type).Cast<object>().Select(value => asString
                ? "'" + WireName(type, value).Replace("'", "''") + "'"
                : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            builder.Entity(entity.ClrType).ToTable(t => t.HasCheckConstraint(
                $"CK_{entity.GetTableName()}_{property.Name}",
                $"\"{property.GetColumnName()}\" IN ({string.Join(",", values)})"));
        }
    }

    private static string WireName(Type type, object value)
    {
        return type.GetField(value.ToString()!)!
                   .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ??
               value.ToString()!;
    }
}

public sealed class StableEnumConverter<T>() : ValueConverter<T, string>(value => EnumWire.Name(value),
    value => EnumWire.Parse<T>(value)) where T : struct, Enum;