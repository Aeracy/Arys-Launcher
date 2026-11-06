using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace ArysLauncher;

public static class LauncherText
{
    private static readonly ResourceManager Messages = new("ArysLauncher.Resources.LauncherText", typeof(LauncherText).Assembly);

    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US");

    public static XmlLanguage Language { get; } = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

    public static string Get(string key)
    {
        return Messages.GetString(key, Culture) ?? throw new MissingManifestResourceException(key);
    }

    public static string Get(string key, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class TextExtension : MarkupExtension
{
    private readonly string key;

    public TextExtension(string key)
    {
        this.key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return LauncherText.Get(key);
    }
}
