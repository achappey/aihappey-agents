using Microsoft.Extensions.AI;

namespace AgentHappey.Common.Extensions;

public static class FileContentExtensions
{
    public static AIContent ToFileContent(this string url, string mediaType, string? filename = null)
    {
        // Remote attachments stay references; DataContent only accepts embedded data URIs.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                return new UriContent(uri, mediaType)
                {
                    AdditionalProperties = filename is null ? null : new() { ["filename"] = filename }
                };
            }

            if (!string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Attachment URL scheme '{uri.Scheme}' is not supported.");
        }

        return new DataContent(url, mediaType) { Name = filename };
    }
}
