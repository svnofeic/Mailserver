using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Mailserver.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mailserver.Core.Dmarc;

/// <summary>One row of an aggregate report: how many mails from one IP address, and whether they passed.</summary>
public sealed record DmarcRow(string SourceIp, int Count, string Disposition, string Dkim, string Spf, string HeaderFrom, string? DkimDomain, string? SpfDomain)
{
    /// <summary>DMARC passes when DKIM or SPF passed and fits the From domain (the report has already evaluated the alignment).</summary>
    public bool Passed => Dkim == "pass" || Spf == "pass";
}

public sealed record DmarcReport(string Organization, string ReportId, string Domain, DateTimeOffset Begin, DateTimeOffset End, string Policy,
    IReadOnlyList<DmarcRow> Rows);

/// <summary>A sending source of the last days: IP address with the mail count the providers saw from it.</summary>
public sealed record DmarcSource(string Ip, int Messages, int Passed, string HeaderFrom, string? DkimDomain, string? SpfDomain,
    IReadOnlyList<string> Reporters)
{
    public int Failed => Messages - Passed;
}

public sealed record DmarcDomainSummary(string Domain, int Reports, int Messages, int Passed, string? Policy, DateTimeOffset? LastReport,
    IReadOnlyList<DmarcSource> Sources)
{
    public int Failed => Messages - Passed;
    public double PassRate => Messages == 0 ? 0 : 100.0 * Passed / Messages;
}

/// <summary>
/// Aggregate reports (RFC 7489, "rua") sent by Google, Microsoft, GMX and others: which servers sent mail in the name of our
/// domains and whether SPF and DKIM passed. Reports are recognized on delivery and stored; the mail itself is filed away.
/// </summary>
public sealed class DmarcReports(Database database, ILogger<DmarcReports> logger)
{
    public const string Folder = "DMARC-Berichte";
    private const long MaxXmlBytes = 20 * 1024 * 1024;

    /// <summary>Stores the report(s) in <paramref name="message"/>; false if it is no aggregate report.</summary>
    public bool TryImport(byte[] message)
    {
        MimeMessage parsed;
        try
        {
            parsed = MimeMessage.Load(new MemoryStream(message, writable: false));
        }
        catch (FormatException)
        {
            return false;
        }

        return Import(parsed).IsReport;
    }

    /// <summary>Stores the reports attached to <paramref name="message"/>; New counts those not known before.</summary>
    public (bool IsReport, int New) Import(MimeMessage message)
    {
        var found = false;
        var added = 0;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (Xml(part) is not { } xml)
            {
                continue;
            }

            try
            {
                if (Parse(xml) is { } report)
                {
                    added += Store(report) ? 1 : 0;
                    found = true;
                }
            }
            catch (Exception ex) when (ex is XmlException or FormatException or InvalidDataException)
            {
                logger.LogDebug(ex, "Attachment {Name} of {Subject} is no readable DMARC report", part.FileName, message.Subject);
            }
        }

        return (found, added);
    }

    /// <summary>The XML of a report attachment (plain, .gz or .zip), or null.</summary>
    private static string? Xml(MimePart part)
    {
        var name = (part.FileName ?? "").ToLowerInvariant();
        var type = part.ContentType.MimeType.ToLowerInvariant();
        var kind = name.EndsWith(".zip") || type.Contains("zip") && !type.Contains("gzip") ? "zip"
            : name.EndsWith(".gz") || type.Contains("gzip") ? "gz"
            : name.EndsWith(".xml") || type is "text/xml" or "application/xml" ? "xml"
            : null;
        if (kind is null || part.Content is null)
        {
            return null;
        }

        try
        {
            using var decoded = new MemoryStream();
            part.Content.DecodeTo(decoded);
            decoded.Position = 0;
            Stream source = decoded;
            ZipArchive? archive = null;
            if (kind == "gz")
            {
                source = new GZipStream(decoded, CompressionMode.Decompress);
            }
            else if (kind == "zip")
            {
                archive = new ZipArchive(decoded, ZipArchiveMode.Read);
                var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    return null;
                }

                source = entry.Open();
            }

            using (archive)
            using (source)
            {
                // Limited, so a compressed bomb cannot fill the memory.
                using var limited = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    if (limited.Length + read > MaxXmlBytes)
                    {
                        return null;
                    }

                    limited.Write(buffer, 0, read);
                }

                return System.Text.Encoding.UTF8.GetString(limited.ToArray());
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    public static DmarcReport? Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), settings);
        var root = XDocument.Load(reader).Root;
        if (root?.Name.LocalName != "feedback")
        {
            return null;
        }

        static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        static string Text(XElement? parent, string name) => Child(parent, name)?.Value.Trim() ?? "";

        var metadata = Child(root, "report_metadata");
        var range = Child(metadata, "date_range");
        var policy = Child(root, "policy_published");
        var domain = Text(policy, "domain").ToLowerInvariant();
        if (domain.Length == 0 || !long.TryParse(Text(range, "begin"), out var begin) || !long.TryParse(Text(range, "end"), out var end))
        {
            return null;
        }

        var rows = new List<DmarcRow>();
        foreach (var record in root.Elements().Where(e => e.Name.LocalName == "record"))
        {
            var row = Child(record, "row");
            var evaluated = Child(row, "policy_evaluated");
            var auth = Child(record, "auth_results");
            // Of several DKIM signatures the one that passed is the interesting one.
            var dkim = auth?.Elements().Where(e => e.Name.LocalName == "dkim").OrderByDescending(e => Text(e, "result") == "pass").FirstOrDefault();
            var spf = Child(auth, "spf");
            rows.Add(new DmarcRow(
                Text(row, "source_ip"),
                int.TryParse(Text(row, "count"), out var count) ? count : 0,
                Text(evaluated, "disposition").ToLowerInvariant(),
                Text(evaluated, "dkim").ToLowerInvariant(),
                Text(evaluated, "spf").ToLowerInvariant(),
                Text(Child(record, "identifiers"), "header_from").ToLowerInvariant(),
                dkim is null ? null : Text(dkim, "domain").ToLowerInvariant(),
                spf is null ? null : Text(spf, "domain").ToLowerInvariant()));
        }

        return new DmarcReport(Text(metadata, "org_name"), Text(metadata, "report_id"), domain, DateTimeOffset.FromUnixTimeSeconds(begin),
            DateTimeOffset.FromUnixTimeSeconds(end), Text(policy, "p").ToLowerInvariant(), rows);
    }

    /// <summary>Stores a report; one sent twice (same organization and id) is kept once.</summary>
    public bool Store(DmarcReport report)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (connection.Execute(
                """
                INSERT OR IGNORE INTO dmarc_reports (org, report_id, domain, begin_utc, end_utc, policy, received_utc)
                VALUES ($org, $id, $domain, $begin, $end, $policy, $now)
                """,
                transaction, ("$org", report.Organization), ("$id", report.ReportId), ("$domain", report.Domain), ("$begin", report.Begin.ToDbTime()),
                ("$end", report.End.ToDbTime()), ("$policy", report.Policy), ("$now", DateTimeOffset.UtcNow.ToDbTime())) == 0)
        {
            return false;
        }

        var id = connection.Scalar("SELECT last_insert_rowid()", transaction);
        foreach (var row in report.Rows)
        {
            connection.Execute(
                """
                INSERT INTO dmarc_rows (report, source_ip, count, disposition, dkim, spf, header_from, dkim_domain, spf_domain)
                VALUES ($report, $ip, $count, $disposition, $dkim, $spf, $from, $dkimDomain, $spfDomain)
                """,
                transaction, ("$report", id), ("$ip", row.SourceIp), ("$count", row.Count), ("$disposition", row.Disposition), ("$dkim", row.Dkim),
                ("$spf", row.Spf), ("$from", row.HeaderFrom), ("$dkimDomain", row.DkimDomain), ("$spfDomain", row.SpfDomain));
        }

        transaction.Commit();
        logger.LogInformation("DMARC report {Id} from {Org} for {Domain}: {Count} messages", report.ReportId, report.Organization, report.Domain,
            report.Rows.Sum(r => r.Count));
        return true;
    }

    /// <summary>Per domain: reports and sending sources whose report period ended after <paramref name="since"/>.</summary>
    public IReadOnlyList<DmarcDomainSummary> Summary(DateTimeOffset since)
    {
        using var connection = database.Open();
        var reports = connection.Query(
            "SELECT id, org, domain, end_utc, policy FROM dmarc_reports WHERE end_utc >= $since ORDER BY end_utc",
            r => (Id: r.GetInt64(0), Org: r.GetString(1), Domain: r.GetString(2), End: r.GetDbTime(3), Policy: r.GetString(4)),
            ("$since", since.ToDbTime()));
        var rows = connection.Query(
            """
            SELECT r.report, r.source_ip, r.count, r.dkim, r.spf, r.header_from, r.dkim_domain, r.spf_domain
            FROM dmarc_rows r JOIN dmarc_reports p ON p.id = r.report WHERE p.end_utc >= $since
            """,
            r => (Report: r.GetInt64(0), Row: new DmarcRow(r.GetString(1), r.GetInt32(2), "", r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7))),
            ("$since", since.ToDbTime()));
        var byReport = reports.ToDictionary(r => r.Id);
        return reports.GroupBy(r => r.Domain).OrderBy(g => g.Key).Select(domain =>
        {
            var ids = domain.Select(r => r.Id).ToHashSet();
            var domainRows = rows.Where(r => ids.Contains(r.Report)).ToList();
            var sources = domainRows.GroupBy(r => r.Row.SourceIp).Select(g => new DmarcSource(
                    g.Key,
                    g.Sum(r => r.Row.Count),
                    g.Where(r => r.Row.Passed).Sum(r => r.Row.Count),
                    g.Select(r => r.Row.HeaderFrom).FirstOrDefault(f => f.Length > 0) ?? domain.Key,
                    g.Select(r => r.Row.DkimDomain).FirstOrDefault(d => !string.IsNullOrEmpty(d)),
                    g.Select(r => r.Row.SpfDomain).FirstOrDefault(d => !string.IsNullOrEmpty(d)),
                    g.Select(r => byReport[r.Report].Org).Distinct().Order().ToList()))
                .OrderByDescending(s => s.Messages).ToList();
            var last = domain.Last();
            return new DmarcDomainSummary(domain.Key, domain.Count(), sources.Sum(s => s.Messages), sources.Sum(s => s.Passed), last.Policy, last.End,
                sources);
        }).ToList();
    }

    /// <summary>Removes reports older than <paramref name="before"/>.</summary>
    public int Cleanup(DateTimeOffset before)
    {
        using var connection = database.Open();
        return connection.Execute("DELETE FROM dmarc_reports WHERE end_utc < $before", ("$before", before.ToDbTime()));
    }
}
