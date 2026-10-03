using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailserver.Core.Configuration;

/// <summary>
/// Writes the settings that can be changed at runtime to data/settings.json. That file is loaded after appsettings.json,
/// overrides it and is watched for changes.
/// </summary>
public sealed class SettingsStore(DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public void Save(SpamOptions spam, SecurityOptions security, DeliveryOptions delivery)
    {
        lock (_lock)
        {
            var root = Load();
            var section = root["Mailserver"] as JsonObject ?? new JsonObject();
            root["Mailserver"] = section;
            section["Spam"] = JsonSerializer.SerializeToNode(spam, Json);
            section["Security"] = JsonSerializer.SerializeToNode(security, Json);
            section["Delivery"] = JsonSerializer.SerializeToNode(new
            {
                delivery.MaxQueueLifetime,
                delivery.MaxParallelDeliveries,
                delivery.SmartHost,
            }, Json);

            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }

    private JsonObject Load()
    {
        try
        {
            return File.Exists(paths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(paths.SettingsFile)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
