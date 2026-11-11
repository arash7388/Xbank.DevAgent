using System.Text;

namespace Xbank.DevAgent.Core;

internal static class AifaCurlLog
{
    internal static async Task<string> FormatAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        StringBuilder command = new("curl --request ");
        command.Append(Quote(request.Method.Method)).Append(" --url ").Append(Quote(request.RequestUri!.AbsoluteUri));
        foreach (var header in request.Headers)
            AppendHeader(command, header.Key, header.Value);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                AppendHeader(command, header.Key, header.Value);
            // Reading buffers the content, leaving the original request available to the transport and retries.
            string body = await request.Content.ReadAsStringAsync(cancellationToken);
            command.Append(" --data-raw ").Append(Quote(body));
        }
        return command.ToString();
    }

    private static void AppendHeader(StringBuilder command, string name, IEnumerable<string> values)
    {
        // Only known diagnostic headers are logged verbatim; credentials and unknown headers are redacted.
        bool visible = name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-user-id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-session-id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("x-request-id", StringComparison.OrdinalIgnoreCase);
        foreach (string value in values)
            command.Append(" --header ").Append(Quote($"{name}: {(visible ? value : "[REDACTED]")}"));
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
