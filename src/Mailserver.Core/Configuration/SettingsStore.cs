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

            Write(root);
        }
    }

    /// <summary>Stores the Let's Encrypt settings (Mailserver:Tls:Acme); the other TLS settings stay in appsettings.json.</summary>
    public void SaveAcme(AcmeOptions acme)
    {
        lock (_lock)
        {
            var root = Load();
            var section = root["Mailserver"] as JsonObject ?? new JsonObject();
            root["Mailserver"] = section;
            var tls = section["Tls"] as JsonObject ?? new JsonObject();
            section["Tls"] = tls;
            tls["Acme"] = JsonSerializer.SerializeToNode(new
            {
                acme.Enabled,
                acme.Email,
                acme.Hostnames,
                acme.UseStaging,
                acme.ChallengeDirectory,
            }, Json);
            Write(root);
        }
    }

    private void Write(JsonObject root)
    {
        var temp = paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Json));
        File.Move(temp, paths.SettingsFile, overwrite: true);
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
