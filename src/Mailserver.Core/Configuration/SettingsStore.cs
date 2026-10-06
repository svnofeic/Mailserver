using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mailserver.Core.Configuration;

/// <summary>
/// Writes the settings that can be changed at runtime to data/settings.json. That file is loaded after appsettings.json,
/// overrides it and is watched for changes.
/// </summary>
/// <param name="configuration">
/// Reloaded right after writing. The file watcher alone reacts only after a short delay; a page shown in between (e.g. right
/// after "Speichern") would still show – and on the next save write back – the old values.
/// </param>
public sealed class SettingsStore(DataPaths paths, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public void Save(SpamOptions spam, SecurityOptions security, DeliveryOptions delivery, AntivirusOptions? antivirus = null)
    {
        lock (_lock)
        {
            var root = Load();
            var section = root["Mailserver"] as JsonObject ?? new JsonObject();
            root["Mailserver"] = section;
            section["Spam"] = JsonSerializer.SerializeToNode(spam, Json);
            section["Security"] = JsonSerializer.SerializeToNode(security, Json);
            if (antivirus is not null)
            {
                section["Antivirus"] = JsonSerializer.SerializeToNode(antivirus, Json);
            }

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

    /// <summary>Stores the backup settings (Mailserver:Backup).</summary>
    public void SaveBackup(BackupOptions backup)
    {
        lock (_lock)
        {
            var root = Load();
            var section = root["Mailserver"] as JsonObject ?? new JsonObject();
            root["Mailserver"] = section;
            section["Backup"] = JsonSerializer.SerializeToNode(backup, Json);
            Write(root);
        }
    }

    private void Write(JsonObject root)
    {
        var temp = paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Json));
        File.Move(temp, paths.SettingsFile, overwrite: true);
        (configuration as Microsoft.Extensions.Configuration.IConfigurationRoot)?.Reload();
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
