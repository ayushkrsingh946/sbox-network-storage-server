using System.Net;
using System.Net.Mail;

namespace SboxNetworkStorage.Server.Alerts;

/// <summary>Sends one plain-text mail. Test fakes capture the call instead of touching the network.</summary>
public interface ISmtpTransport
{
    Task SendAsync(
        string host, int port, bool useTls,
        string username, string password,
        string from, IReadOnlyList<string> to,
        string subject, string body,
        CancellationToken cancellationToken);
}

/// <summary>Production <see cref="ISmtpTransport"/> over <see cref="SmtpClient"/>.</summary>
public sealed class SmtpTransport : ISmtpTransport
{
    public async Task SendAsync(
        string host, int port, bool useTls,
        string username, string password,
        string from, IReadOnlyList<string> to,
        string subject, string body,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = useTls,
            Timeout = 10_000,
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        using var message = new MailMessage
        {
            From = new MailAddress(from),
            Subject = subject,
            Body = body,
        };
        foreach (var recipient in to)
        {
            message.To.Add(recipient);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
