using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailserver.Core.Antivirus;

public enum ScanOutcome
{
    Clean,
    Infected,
    Error,
}

public sealed record ScanResult(ScanOutcome Outcome, string? Threat = null, string? Detail = null)
{
    public static readonly ScanResult Clean = new(ScanOutcome.Clean);
}

public interface IVirusScanner
{
    /// <summary>Name for logs and the X-Virus-Scanned header.</summary>
    string Name { get; }

    Task<ScanResult> ScanAsync(IReadOnlyList<Attachment> files, CancellationToken cancellationToken);
}

/// <summary>
/// Microsoft Defender via MpCmdRun.exe (part of every Windows Server). The files are written to a private folder and scanned
/// there; if real-time protection removes one of them right away, that counts as a finding as well.
/// </summary>
public sealed partial class DefenderScanner(string executable, string workDirectory) : IVirusScanner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public string Name => "Microsoft Defender";

    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var classic = Path.Combine(programFiles, "Windows Defender", "MpCmdRun.exe");
        if (File.Exists(classic))
        {
            return classic;
        }

        // Newer platform updates install into ProgramData\Microsoft\Windows Defender\Platform\<version>.
        var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        return Directory.Exists(platform)
            ? Directory.GetDirectories(platform).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "MpCmdRun.exe")).FirstOrDefault(File.Exists)
            : null;
    }

    public async Task<ScanResult> ScanAsync(IReadOnlyList<Attachment> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return ScanResult.Clean;
        }

        var folder = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var written = new List<(string Path, string Name)>();
            for (var i = 0; i < files.Count; i++)
            {
                // Neutral names: the scanner looks at the content; the original name could contain anything.
                var path = Path.Combine(folder, $"{i:D3}.bin");
                try
                {
                    await File.WriteAllBytesAsync(path, files[i].Content, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new ScanResult(ScanOutcome.Infected, "vom Echtzeitschutz blockiert", files[i].FileName);
                }

                written.Add((path, files[i].FileName));
            }

            // Real-time protection may already have removed a file.
            await Task.Delay(200, cancellationToken);
            if (written.FirstOrDefault(w => !File.Exists(w.Path)) is { Path: not null } removed)
            {
                return new ScanResult(ScanOutcome.Infected, "vom Echtzeitschutz entfernt", removed.Name);
            }

            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", folder, "-DisableRemediation" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("MpCmdRun konnte nicht gestartet werden.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return new ScanResult(ScanOutcome.Error, Detail: "Zeitüberschreitung beim Virenscan");
            }

            var text = await output;
            // 0 = no threat, 2 = threat found; everything else is an error of the scanner itself.
            return process.ExitCode switch
            {
                0 => ScanResult.Clean,
                2 => new ScanResult(ScanOutcome.Infected, ThreatLine().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : "Schadsoftware"),
                _ => new ScanResult(ScanOutcome.Error, Detail: $"MpCmdRun beendet mit Code {process.ExitCode}: {text.Trim().Split('\n').LastOrDefault()?.Trim()}"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ScanResult(ScanOutcome.Error, Detail: ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^\s*Threat\s*:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex ThreatLine();
}

/// <summary>ClamAV via clamd (INSTREAM command), e.g. on this machine or a Linux server nearby.</summary>
public sealed class ClamAvScanner(string host, int port) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;

    public string Name => "ClamAV";

    public async Task<ScanResult> ScanAsync(IReadOnlyList<Attachment> files, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            var result = await ScanOneAsync(file, cancellationToken);
            if (result.Outcome != ScanOutcome.Clean)
            {
                return result;
            }
        }

        return ScanResult.Clean;
    }

    private async Task<ScanResult> ScanOneAsync(Attachment file, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
            var length = new byte[4];
            for (var offset = 0; offset < file.Content.Length; offset += ChunkSize)
            {
                var size = Math.Min(ChunkSize, file.Content.Length - offset);
                BinaryPrimitives.WriteInt32BigEndian(length, size);
                await stream.WriteAsync(length, timeout.Token);
                await stream.WriteAsync(file.Content.AsMemory(offset, size), timeout.Token);
            }

            BinaryPrimitives.WriteInt32BigEndian(length, 0);
            await stream.WriteAsync(length, timeout.Token);

            using var reader = new MemoryStream();
            await stream.CopyToAsync(reader, timeout.Token);
            var reply = Encoding.ASCII.GetString(reader.ToArray()).TrimEnd('\0', '\n', '\r');
            // "stream: OK", "stream: Eicar-Signature FOUND" or "... ERROR"
            if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
            {
                return new ScanResult(ScanOutcome.Infected, reply[(reply.IndexOf(':') + 1)..^" FOUND".Length].Trim(), file.FileName);
            }

            return reply.EndsWith(": OK", StringComparison.Ordinal) ? ScanResult.Clean : new ScanResult(ScanOutcome.Error, Detail: $"clamd: {reply}");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new ScanResult(ScanOutcome.Error, Detail: $"clamd auf {host}:{port} nicht erreichbar: {ex.Message}");
        }
    }
}
