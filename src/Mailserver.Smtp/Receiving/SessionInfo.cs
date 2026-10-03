using System.Net;
using SmtpServer;
using SmtpServer.Net;
using SmtpServer.Protocol;

namespace Mailserver.Smtp.Receiving;

/// <summary>Per-session facts that SmtpServer does not expose directly.</summary>
internal static class SessionInfo
{
    private const string HeloKey = "Mailserver:Helo";
    private const string InboundKey = "Mailserver:Inbound";

    /// <summary>Spam filter state for this session, created on first use.</summary>
    public static Mailserver.AntiSpam.InboundSession GetInbound(ISessionContext context)
    {
        if (!context.Properties.TryGetValue(InboundKey, out var value) || value is not Mailserver.AntiSpam.InboundSession session)
        {
            session = new Mailserver.AntiSpam.InboundSession(GetRemoteAddress(context), GetHelo(context), context.SessionId.ToString("N"));
            context.Properties[InboundKey] = session;
        }

        return session;
    }

    public static IPAddress? GetRemoteAddress(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var value) && value is IPEndPoint endpoint
            ? endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address
            : null;

    public static string? GetHelo(ISessionContext context) =>
        context.Properties.TryGetValue(HeloKey, out var value) ? value as string : null;

    /// <summary>Records the EHLO/HELO name for the Received header.</summary>
    public static void Track(ISessionContext context)
    {
        context.CommandExecuting += (_, e) =>
        {
            switch (e.Command)
            {
                case EhloCommand ehlo:
                    e.Context.Properties[HeloKey] = ehlo.DomainOrAddress;
                    break;
                case HeloCommand helo:
                    e.Context.Properties[HeloKey] = helo.DomainOrAddress;
                    break;
            }
        };
    }
}
