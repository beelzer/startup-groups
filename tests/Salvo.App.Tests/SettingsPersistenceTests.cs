using System;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.App.Localization;
using Salvo.App.Services;

namespace Salvo.App.Tests;

/// <summary>
/// Regression tests for the settings-persistence data-loss bugs: a partial
/// per-field copy anywhere on the mutate-and-save path silently resets every
/// field it forgot back to its default. These tests force every AppSettings
/// field to a non-default value via reflection and assert the whole object
/// survives a round-trip, so adding a new field that some copy path forgets
/// fails the build rather than shipping a silent reset.
/// </summary>
public sealed class SettingsPersistenceTests
{
    [Fact]
    public void Clone_CopiesEveryField()
    {
        var original = MakeAllNonDefault();

        var clone = original.Clone();

        // BeEquivalentTo compares every public member, including any field
        // added later — so a field missing from Clone() (left at default)
        // fails here.
        clone.Should().BeEquivalentTo(original);
        clone.Should().NotBeSameAs(original);
    }

    [Fact]
    public void SetLanguage_ChangesOnlyUiCulture_PreservingEveryOtherField()
    {
        // The original bug: picking a UI language rebuilt AppSettings from a
        // 4-of-9-field copy, wiping UpdateChannel, AlwaysRunAsAdmin, etc.
        var original = MakeAllNonDefault();
        original.UiCulture = "de";
        var store = new FakeSettingsStore(original);
        var service = new LanguageService(store, NullLogger<LanguageService>.Instance);

        service.SetLanguage(new SupportedLanguage("ja", "日本語"));

        store.Saved.Should().NotBeNull();
        store.Saved!.UiCulture.Should().Be("ja");
        store.Saved.Should().BeEquivalentTo(original, opts => opts.Excluding(s => s.UiCulture));
    }

    /// <summary>
    /// Sets every writable AppSettings property to a value that differs from a
    /// fresh default instance, so a dropped field shows up as a default in the
    /// round-trip assertions. Throws for any property type it doesn't know how
    /// to vary, forcing the test to be taught about genuinely new field types.
    /// </summary>
    private static AppSettings MakeAllNonDefault()
    {
        var settings = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            var current = prop.GetValue(settings);
            object next = prop.PropertyType switch
            {
                Type t when t == typeof(bool) => !(bool)current!,
                Type t when t == typeof(string) => "round-trip-marker",
                Type t when t.IsEnum => FirstEnumValueOtherThan(t, current!),
                _ => throw new NotSupportedException(
                    $"MakeAllNonDefault has no rule for {prop.Name} of type {prop.PropertyType}. " +
                    "Add one so this field is covered by the persistence round-trip tests."),
            };
            prop.SetValue(settings, next);
        }

        return settings;
    }

    private static object FirstEnumValueOtherThan(Type enumType, object current)
    {
        foreach (var value in Enum.GetValues(enumType))
        {
            if (!value!.Equals(current))
            {
                return value;
            }
        }

        return current; // single-member enum: nothing else to pick
    }

    private sealed class FakeSettingsStore(AppSettings initial) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = initial;

        public AppSettings? Saved { get; private set; }

        public event EventHandler<AppSettings>? Changed;

        public void Save(AppSettings settings)
        {
            Current = settings;
            Saved = settings;
            Changed?.Invoke(this, settings);
        }
    }
}
