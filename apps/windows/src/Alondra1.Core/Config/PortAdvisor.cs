namespace Alondra1.Core.Config;

/// <summary>
/// The well-known mail ports and the connection security each one needs. A setting that contradicts its port (STARTTLS
/// on 465, POP3 on the IMAP port 993, ...) never works: the client waits for a greeting the server never sends, and
/// the user sees a timeout that looks like "server unreachable". These rules let the account dialog fill in matching
/// values, refuse to save a contradiction, and explain an existing one before any connection is attempted.
/// </summary>
public static class PortAdvisor
{
    /// <summary>The security a standard port requires, or null for a non-standard port.</summary>
    public static TransportSecurity? SecurityFor(int port) => port switch
    {
        465 or 993 or 995 => TransportSecurity.ImplicitTls,
        25 or 110 or 143 or 587 => TransportSecurity.RequiredStartTls,
        _ => null,
    };

    public static int DefaultIncomingPort(MailProtocol protocol, TransportSecurity security) => (protocol, security) switch
    {
        (MailProtocol.Imap, TransportSecurity.ImplicitTls) => 993,
        (MailProtocol.Imap, _) => 143,
        (MailProtocol.Pop3, TransportSecurity.ImplicitTls) => 995,
        (MailProtocol.Pop3, _) => 110,
        _ => 993,
    };

    public static int DefaultOutgoingPort(TransportSecurity security) => security switch
    {
        TransportSecurity.ImplicitTls => 465,
        TransportSecurity.RequiredStartTls => 587,
        _ => 25,
    };

    /// <summary>True for a port that is the standard port of some protocol and security (safe to replace automatically).</summary>
    public static bool IsStandardIncomingPort(int port) => port is 993 or 143 or 995 or 110;

    public static bool IsStandardOutgoingPort(int port) => port is 465 or 587 or 25;

    /// <summary>Why the incoming settings cannot work, or null.</summary>
    public static string? IncomingProblem(IncomingSettings incoming)
    {
        var name = incoming.Protocol == MailProtocol.Imap ? "IMAP" : "POP3";
        if (incoming.Protocol == MailProtocol.Pop3 && incoming.Port is 993 or 143)
            return $"Port {incoming.Port} is an IMAP port. POP3 uses 995 (Implicit TLS) or 110 (STARTTLS).";
        if (incoming.Protocol == MailProtocol.Imap && incoming.Port is 995 or 110)
            return $"Port {incoming.Port} is a POP3 port. IMAP uses 993 (Implicit TLS) or 143 (STARTTLS).";
        return SecurityProblem($"Incoming {name}", incoming.Port, incoming.Security);
    }

    /// <summary>Why the outgoing settings cannot work, or null.</summary>
    public static string? OutgoingProblem(OutgoingSettings outgoing)
    {
        if (outgoing.Port is 993 or 995 or 143 or 110)
            return $"Port {outgoing.Port} is for incoming mail. Outgoing SMTP uses 465 (Implicit TLS) or 587 (STARTTLS).";
        return SecurityProblem("Outgoing SMTP", outgoing.Port, outgoing.Security);
    }

    private static string? SecurityProblem(string what, int port, TransportSecurity security)
    {
        var expected = SecurityFor(port);
        if (expected == TransportSecurity.ImplicitTls && security != TransportSecurity.ImplicitTls)
            return $"{what} port {port} uses Implicit TLS (SSL/TLS from the first byte). Set Security to ImplicitTls, or use the STARTTLS port.";
        if (expected == TransportSecurity.RequiredStartTls && security == TransportSecurity.ImplicitTls)
            return $"{what} port {port} uses STARTTLS. Set Security to RequiredStartTls, or use the Implicit TLS port.";
        return null;
    }
}
