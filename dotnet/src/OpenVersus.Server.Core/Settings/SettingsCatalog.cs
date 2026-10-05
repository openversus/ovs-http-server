using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace OpenVersus.Server.Core.Settings;

/// <summary>A setting's value can only change at startup: the listeners are bound once, for example. On a class, all of its settings.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class RestartRequiredAttribute : Attribute;

/// <summary>A setting whose value is never shown by the control API (passwords, keys).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>One setting: its configuration key, type, description, and whether it can change while running.</summary>
public sealed record SettingInfo(string Key, Type OptionsType, PropertyInfo Property, string Description, bool RestartRequired, bool Secret)
{
    public string TypeName
    {
        get
        {
            var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
            string name = type.IsEnum ? string.Join("|", Enum.GetNames(type)) : type.Name;
            return type == Property.PropertyType ? name : name + "?";
        }
    }
}

/// <summary>
/// Every setting the service knows, from the options classes registered with
/// <see cref="SettingsServiceCollectionExtensions.AddSetting{T}"/>: a class's public settable properties under its
/// section, with <see cref="DescriptionAttribute"/>, <see cref="RestartRequiredAttribute"/>, <see cref="SecretAttribute"/>
/// and data-annotation validation. The control API lists these and refuses keys that are not here, so a typo
/// cannot quietly create a setting nothing reads.
/// </summary>
public sealed class SettingsCatalog
{
    private readonly Dictionary<string, SettingInfo> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, string> _sections = [];

    public IEnumerable<SettingInfo> All => _settings.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public void Register(Type optionsType, string section)
    {
        _sections[optionsType] = section;
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            string key = $"{section}:{property.Name}";
            _settings[key] = new SettingInfo(key, optionsType, property,
                property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                property.IsDefined(typeof(RestartRequiredAttribute)) || optionsType.IsDefined(typeof(RestartRequiredAttribute)),
                property.IsDefined(typeof(SecretAttribute)));
        }
    }

    public bool TryGet(string key, out SettingInfo setting) => _settings.TryGetValue(key, out setting!);

    /// <summary>
    /// Null when <paramref name="value"/> is acceptable for <paramref name="key"/> given the rest of
    /// <paramref name="current"/>; otherwise why not. Binds the whole options class with the new value in place and
    /// runs its validation, so a value is judged exactly as the service would read it.
    /// </summary>
    public string? Validate(string key, string value, IConfiguration current)
    {
        if (!TryGet(key, out var setting))
        {
            return $"unknown setting '{key}'";
        }

        string section = _sections[setting.OptionsType];
        var trial = new ConfigurationBuilder()
            .AddConfiguration(current)
            .AddInMemoryCollection([new KeyValuePair<string, string?>(setting.Key, value)])
            .Build();
        object options;
        try
        {
            options = trial.GetSection(section).Get(setting.OptionsType, o => o.ErrorOnUnknownConfiguration = false)
                ?? Activator.CreateInstance(setting.OptionsType)!;
        }
        catch (InvalidOperationException e)
        {
            return $"'{value}' is not a valid {setting.TypeName}: {e.InnerException?.Message ?? e.Message}";
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
        {
            return string.Join("; ", results.Select(r => r.ErrorMessage));
        }

        return null;
    }
}
