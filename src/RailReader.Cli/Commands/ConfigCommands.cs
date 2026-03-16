using System.CommandLine;
using System.Reflection;
using RailReader.Cli.Output;
using RailReader.Core.Services;

namespace RailReader.Cli.Commands;

public static class ConfigCommands
{
    public static Command Create()
    {
        var cmd = new Command("config", "View and modify application settings");

        var showCmd = new Command("show", "Show current configuration");
        var keyArg = new Argument<string?>("key") { Description = "Specific config key (snake_case or PascalCase)", DefaultValueFactory = _ => null };
        showCmd.Add(keyArg);
        showCmd.SetAction(pr =>
        {
            var config = SessionBinder.Session.Config;
            var fmt = SessionBinder.Formatter;
            var props = GetConfigProperties();
            var key = pr.GetValue(keyArg);

            if (key is not null)
            {
                var prop = FindProperty(props, key);
                if (prop is null) { fmt.WriteError($"Unknown config key: {key}"); return; }
                fmt.WriteResult(new { key = ToSnakeCase(prop.Name), value = prop.GetValue(config) });
                return;
            }

            if (fmt is JsonFormatter)
            {
                fmt.WriteResult(props.ToDictionary(p => ToSnakeCase(p.Name), p => p.GetValue(config)));
                return;
            }

            HumanFormatter.WriteTable(
                props.Select(p => new object[] { ToSnakeCase(p.Name), FormatValue(p.GetValue(config)) }),
                "Key", "Value");
        });

        var setCmd = new Command("set", "Set a configuration value");
        var setKeyArg = new Argument<string>("key") { Description = "Config key" };
        var setValueArg = new Argument<string>("value") { Description = "New value" };
        setCmd.Add(setKeyArg); setCmd.Add(setValueArg);
        setCmd.SetAction(pr =>
        {
            var config = SessionBinder.Session.Config;
            var fmt = SessionBinder.Formatter;
            var key = pr.GetValue(setKeyArg);
            var value = pr.GetValue(setValueArg);
            var prop = FindProperty(GetConfigProperties(), key);
            if (prop is null) { fmt.WriteError($"Unknown config key: {key}"); return; }
            try
            {
                var converted = ConvertValue(value, prop.PropertyType);
                prop.SetValue(config, converted);
                config.Save();
                fmt.WriteMessage($"Set {ToSnakeCase(prop.Name)} = {converted}");
                fmt.WriteResult(new { key = ToSnakeCase(prop.Name), value = converted });
            }
            catch (Exception ex) { fmt.WriteError($"Invalid value for {prop.Name} ({prop.PropertyType.Name}): {ex.Message}"); }
        });

        var resetCmd = new Command("reset", "Reset configuration to defaults");
        resetCmd.SetAction(pr =>
        {
            var session = SessionBinder.Session;
            var fresh = new AppConfig();
            foreach (var prop in GetConfigProperties())
            {
                if (!prop.CanWrite) continue;
                prop.SetValue(session.Config, prop.GetValue(fresh));
            }
            session.Config.Save();
            SessionBinder.Formatter.WriteMessage("Configuration reset to defaults.");
            SessionBinder.Formatter.WriteResult(new { reset = true });
        });

        var pathCmd = new Command("path", "Show config file location");
        pathCmd.SetAction(pr =>
        {
            SessionBinder.Formatter.WriteResult(new { path = AppConfig.ConfigPath });
        });

        cmd.Add(showCmd); cmd.Add(setCmd); cmd.Add(resetCmd); cmd.Add(pathCmd);
        return cmd;
    }

    private static PropertyInfo[] GetConfigProperties() =>
        typeof(AppConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.Name != "NavigableClasses").ToArray();

    private static PropertyInfo? FindProperty(PropertyInfo[] props, string key) =>
        props.FirstOrDefault(p =>
            p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            ToSnakeCase(p.Name).Equals(key, StringComparison.OrdinalIgnoreCase));

    private static object? ConvertValue(string value, Type t)
    {
        if (t == typeof(double)) return double.Parse(value);
        if (t == typeof(float)) return float.Parse(value);
        if (t == typeof(int)) return int.Parse(value);
        if (t == typeof(bool)) return bool.Parse(value);
        if (t == typeof(string)) return value;
        if (t.IsEnum) return Enum.Parse(t, value, ignoreCase: true);
        if (t == typeof(List<string>))
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (t == typeof(List<int>))
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        throw new NotSupportedException($"Cannot convert to {t.Name}");
    }

    private static string ToSnakeCase(string name) =>
        PascalCaseHelper.SplitPascalCase(name, '_').ToLowerInvariant();

    private static string FormatValue(object? value) => value switch
    {
        null => "(null)",
        System.Collections.IList list => $"[{string.Join(", ", list.Cast<object>())}]",
        _ => value.ToString() ?? "(null)",
    };
}
