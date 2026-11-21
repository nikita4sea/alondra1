using System.Text.Json;
using Alondra1.Core.Config;

namespace Alondra1.Persistence;

public sealed class ConfigFileStore
{
    private readonly string _path;
    private readonly string _backup;

    public ConfigFileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
        _backup = Path.Combine(directory, "config.json.bak");
    }

    public string PathToFile => _path;

    public bool Exists => File.Exists(_path) || File.Exists(_backup);

    public AppConfig Load()
    {
        if (!File.Exists(_path))
        {
            if (File.Exists(_backup) && TryRead(_backup, out var recovered, out _))
                return recovered;
            return new AppConfig();
        }

        if (TryRead(_path, out var config, out var failure) && config is not null)
            return config;
        if (failure?.Newer == true)
            throw new ConfigFormatException(failure.Message);

        if (File.Exists(_backup) && TryRead(_backup, out var backup, out var backupFailure) && backup is not null && backupFailure is null)
        {
            Save(backup);
            return backup;
        }

        throw new ConfigFormatException(failure?.Message ?? "The configuration file is damaged and no known-good copy is available.");
    }

    public void Save(AppConfig config)
    {
        var validation = ConfigWorkflow.Validate(config);
        if (!validation.Ok)
            throw new ConfigFormatException(string.Join(" ", validation.Errors));
        config.SchemaVersion = ConfigSchema.Current;
        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        if (File.Exists(_path))
            File.Replace(temporary, _path, _backup, ignoreMetadataErrors: true);
        else
            File.Move(temporary, _path);
    }

    private static bool TryRead(string path, out AppConfig? config, out ReadFailure? failure)
    {
        config = null;
        failure = null;
        try
        {
            var json = File.ReadAllText(path);
            config = ConfigWorkflow.Parse(json);
            var validation = ConfigWorkflow.Validate(config);
            if (!validation.Ok)
            {
                failure = new ReadFailure(string.Join(" ", validation.Errors), false);
                config = null;
                return false;
            }

            return true;
        }
        catch (ConfigFormatException ex)
        {
            var newer = ex.Message.Contains("supports version", StringComparison.Ordinal);
            failure = new ReadFailure(ex.Message, newer);
            return false;
        }
        catch (Exception ex)
        {
            failure = new ReadFailure(ex.Message, false);
            return false;
        }
    }

    private sealed record ReadFailure(string Message, bool Newer);
}
