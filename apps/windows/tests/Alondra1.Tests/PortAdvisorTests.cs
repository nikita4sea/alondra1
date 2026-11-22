using Alondra1.Core.Config;
using Alondra1.Core.Security;

namespace Alondra1.Tests;

/// <summary>Port/security contradictions are caught before connecting (they would only time out).</summary>
public class PortAdvisorTests
{
    private static AccountConfig Manual(MailProtocol protocol, int inPort, TransportSecurity inSecurity, int outPort, TransportSecurity outSecurity) => new()
    {
        Id = "a",
        DisplayName = "Dev",
        Address = "dev@example.com",
        Incoming = new IncomingSettings { Protocol = protocol, Host = "mail.example.com", Port = inPort, Security = inSecurity, Username = "dev@example.com" },
        Outgoing = new OutgoingSettings { Host = "smtp.example.com", Port = outPort, Security = outSecurity, Username = "dev@example.com" },
    };

    [Theory]
    [InlineData(MailProtocol.Imap, 993, TransportSecurity.ImplicitTls, 465, TransportSecurity.ImplicitTls)]
    [InlineData(MailProtocol.Pop3, 995, TransportSecurity.ImplicitTls, 587, TransportSecurity.RequiredStartTls)]
    [InlineData(MailProtocol.Imap, 143, TransportSecurity.RequiredStartTls, 587, TransportSecurity.RequiredStartTls)]
    [InlineData(MailProtocol.Pop3, 110, TransportSecurity.RequiredStartTls, 25, TransportSecurity.RequiredStartTls)]
    [InlineData(MailProtocol.Imap, 10993, TransportSecurity.RequiredStartTls, 2525, TransportSecurity.ImplicitTls)] // non-standard ports are not judged
    public void Consistent_settings_pass(MailProtocol protocol, int inPort, TransportSecurity inSecurity, int outPort, TransportSecurity outSecurity)
    {
        var account = Manual(protocol, inPort, inSecurity, outPort, outSecurity);
        Assert.Null(PortAdvisor.IncomingProblem(account.Incoming));
        Assert.Null(PortAdvisor.OutgoingProblem(account.Outgoing));
    }

    [Theory]
    [InlineData(465, TransportSecurity.RequiredStartTls, "port 465 uses Implicit TLS")]   // the outgoing default security on 465
    [InlineData(465, TransportSecurity.None, "port 465 uses Implicit TLS")]
    [InlineData(587, TransportSecurity.ImplicitTls, "port 587 uses STARTTLS")]
    [InlineData(995, TransportSecurity.ImplicitTls, "is for incoming mail")]
    public void Outgoing_contradictions_are_explained(int port, TransportSecurity security, string expected)
    {
        var problem = PortAdvisor.OutgoingProblem(new OutgoingSettings { Port = port, Security = security });
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData(MailProtocol.Pop3, 993, TransportSecurity.ImplicitTls, "is an IMAP port")] // POP3 chosen, IMAP port kept
    [InlineData(MailProtocol.Imap, 995, TransportSecurity.ImplicitTls, "is a POP3 port")]
    [InlineData(MailProtocol.Pop3, 995, TransportSecurity.RequiredStartTls, "port 995 uses Implicit TLS")]
    [InlineData(MailProtocol.Imap, 143, TransportSecurity.ImplicitTls, "port 143 uses STARTTLS")]
    public void Incoming_contradictions_are_explained(MailProtocol protocol, int port, TransportSecurity security, string expected)
    {
        var problem = PortAdvisor.IncomingProblem(new IncomingSettings { Protocol = protocol, Port = port, Security = security });
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Saving_and_connecting_refuse_a_contradiction_with_the_fix()
    {
        var account = Manual(MailProtocol.Pop3, 995, TransportSecurity.ImplicitTls, 465, TransportSecurity.RequiredStartTls);
        var validation = ConfigWorkflow.Validate(new AppConfig { Accounts = [account] });
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, error => error.Contains("port 465 uses Implicit TLS"));

        account.Incoming.Port = 993;
        var decision = ConnectionPolicy.Evaluate(account, new SecretBundle { Password = "x" });
        Assert.False(decision.Allowed);
        Assert.Contains("is an IMAP port", decision.Reason);
    }

    [Fact]
    public void Defaults_match_the_rules()
    {
        foreach (var protocol in Enum.GetValues<MailProtocol>())
        {
            foreach (var security in new[] { TransportSecurity.ImplicitTls, TransportSecurity.RequiredStartTls })
            {
                var port = PortAdvisor.DefaultIncomingPort(protocol, security);
                Assert.Null(PortAdvisor.IncomingProblem(new IncomingSettings { Protocol = protocol, Port = port, Security = security }));
                var outPort = PortAdvisor.DefaultOutgoingPort(security);
                Assert.Null(PortAdvisor.OutgoingProblem(new OutgoingSettings { Port = outPort, Security = security }));
            }
        }

        // The defaults of a new manual account are consistent.
        Assert.Null(PortAdvisor.IncomingProblem(new IncomingSettings()));
        Assert.Null(PortAdvisor.OutgoingProblem(new OutgoingSettings()));
    }
}
