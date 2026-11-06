using CmlLib.Core.ProcessBuilder;

namespace ArysLauncher;

public static class JvmArgumentPolicy
{
    private const string G1Collector = "-XX:+UseG1GC";
    private const string Log4jProperty = "-Dlog4j2.formatMsgNoLookups";
    private static readonly string[] RecommendedArguments =
    {
        G1Collector, "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200"
    };
    private static readonly HashSet<string> Collectors = new(StringComparer.Ordinal)
    {
        "UseG1GC", "UseZGC", "UseShenandoahGC", "UseParallelGC", "UseSerialGC", "UseConcMarkSweepGC", "UseEpsilonGC"
    };

    public static string RecommendedText { get; } = string.Join(Environment.NewLine, RecommendedArguments);

    public static string GetEditableText(LauncherSettings settings)
    {
        var saved = settings.JvmArguments ?? "";
        if (settings.JvmArgumentsAreComplete)
        {
            return saved;
        }
        if (string.IsNullOrWhiteSpace(saved))
        {
            return RecommendedText;
        }
        var legacy = Parse(saved);
        var enabled = EnabledCollectors(legacy);
        var useG1 = enabled.Length == 0 ? !legacy.Contains("-XX:-UseG1GC", StringComparer.Ordinal) :
            enabled.Contains(G1Collector, StringComparer.Ordinal);
        if (!useG1)
        {
            return saved;
        }
        var keys = legacy.Select(OptionKey).ToHashSet(StringComparer.Ordinal);
        return string.Join(Environment.NewLine, RecommendedArguments.Where(argument => !keys.Contains(OptionKey(argument))).Append(saved));
    }

    public static MArgument[] Build(LauncherSettings settings)
    {
        var custom = Parse(GetEditableText(settings));
        if (custom.Any(argument => argument.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal) ||
            argument.StartsWith("-XX:InitialHeapSize=", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("初始内存由启动器管理。请在「内存分配」调整游戏内存上限。");
        }
        if (EnabledCollectors(custom).Length > 1)
        {
            throw new InvalidOperationException("JVM 参数中只能启用一种垃圾回收器。");
        }
        var arguments = custom.Any(argument => OptionKey(argument) == Log4jProperty) ? custom : custom.Prepend(Log4jProperty + "=true");
        return arguments.Select(argument => new MArgument(argument)).ToArray();
    }

    private static IReadOnlyCollection<string> Parse(string text)
    {
        return MArgument.FromCommandLine(text.ReplaceLineEndings(" ")).Values;
    }

    private static string[] EnabledCollectors(IEnumerable<string> arguments)
    {
        return arguments.Where(argument => argument.StartsWith("-XX:+", StringComparison.Ordinal) &&
            Collectors.Contains(argument[5..])).Distinct().ToArray();
    }

    private static string OptionKey(string argument)
    {
        if (argument.StartsWith("-XX:+", StringComparison.Ordinal) || argument.StartsWith("-XX:-", StringComparison.Ordinal))
        {
            return "-XX:" + argument[5..];
        }
        return argument.Split('=', 2)[0];
    }
}
