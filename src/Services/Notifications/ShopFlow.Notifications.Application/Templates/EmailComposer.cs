using System.Globalization;
using System.Net;
using System.Text;

namespace ShopFlow.Notifications.Application.Templates;

/// <summary>
/// Composes an e-mail in two versions (HTML and text) from the same building blocks.
/// </summary>
/// <remarks>
/// WHY: every text passes through <see cref="WebUtility.HtmlEncode(string)"/> in one place, and templates never build HTML
/// themselves. The customer name comes from a profile the customer edits, and product names from back-office. Without
/// encoding someone could put a link or a form into their name and we would send it from our trusted domain (ready-made
/// phishing with valid SPF and DKIM).
/// WHY: the text version is not decoration. Spam filters lower the score of HTML-only messages, and some mail clients
/// (and screen readers) use exactly that version.
/// </remarks>
internal sealed class EmailComposer
{
    private readonly string _language;
    private readonly string _subject;
    private readonly StringBuilder _html = new();
    private readonly StringBuilder _text = new();
    private readonly List<(string Label, string Value)> _facts = [];

    public EmailComposer(string language, string subject)
    {
        _language = language;
        _subject = subject;
    }

    public EmailComposer Greeting(string? displayName)
    {
        var greeting = string.IsNullOrWhiteSpace(displayName)
            ? Localize("Cześć,", "Hello,")
            : Localize($"Cześć {displayName},", $"Hi {displayName},");
        return Paragraph(greeting);
    }

    public EmailComposer Paragraph(string text)
    {
        FlushFacts();
        _html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:0 0 16px\">{WebUtility.HtmlEncode(text)}</p>");
        _text.Append(text).Append("\n\n");
        return this;
    }

    /// <summary>A "label: value" pair. Consecutive facts go into one table.</summary>
    public EmailComposer Fact(string label, string value)
    {
        _facts.Add((label, value));
        return this;
    }

    public EmailComposer Action(string label, Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        // Links in our messages use https only. Any other scheme (javascript:, http:) is a configuration error or an injection,
        // not something we want to send to a customer.
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Only absolute https links are allowed in notifications, got '{url}'.", nameof(url));
        }

        FlushFacts();
        _html.Append(CultureInfo.InvariantCulture, $"<p style=\"margin:24px 0\"><a href=\"{WebUtility.HtmlEncode(url.AbsoluteUri)}\" style=\"background:#0969da;color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block\">{WebUtility.HtmlEncode(label)}</a></p>");
        _text.Append(CultureInfo.InvariantCulture, $"{label}: {url.AbsoluteUri}\n\n");
        return this;
    }

    public EmailContent Build()
    {
        FlushFacts();

        var footer = Localize(
            "Ta wiadomość dotyczy Twojego zamówienia w ShopFlow i nie zawiera treści marketingowych.",
            "This message is about your ShopFlow order and contains no marketing content.");

        var html = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<!DOCTYPE html><html lang=\"{_language}\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(_subject)}</title></head>")
            .Append("<body style=\"margin:0;padding:24px;background:#f6f8fa;font-family:'Segoe UI',Arial,sans-serif;color:#1f2328\">")
            .Append("<div style=\"max-width:560px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px\">")
            .Append(_html)
            .Append(CultureInfo.InvariantCulture, $"<p style=\"margin:32px 0 0;color:#6e7781;font-size:12px\">{WebUtility.HtmlEncode(footer)}</p>")
            .Append("</div></body></html>")
            .ToString();

        var text = _text.Append("--\n").Append(footer).ToString();
        return new EmailContent(_subject, html, text);
    }

    private string Localize(string polish, string english) => _language == "en" ? english : polish;

    private void FlushFacts()
    {
        if (_facts.Count == 0)
        {
            return;
        }

        _html.Append("<table style=\"border-collapse:collapse;margin:0 0 16px\">");
        foreach (var (label, value) in _facts)
        {
            _html.Append(CultureInfo.InvariantCulture, $"<tr><td style=\"padding:4px 16px 4px 0;color:#6e7781\">{WebUtility.HtmlEncode(label)}</td><td style=\"padding:4px 0;font-weight:600\">{WebUtility.HtmlEncode(value)}</td></tr>");
            _text.Append(CultureInfo.InvariantCulture, $"{label}: {value}\n");
        }

        _html.Append("</table>");
        _text.Append('\n');
        _facts.Clear();
    }
}
