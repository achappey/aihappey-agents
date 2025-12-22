using ModelContextProtocol.Protocol;

namespace AgentHappey.Common.Extensions;

public static class ModelContextHelpers
{
    public static ContentBlock ToContentBlock(this string text) => new TextContentBlock()
    {
        Text = text
    };

    public static IEnumerable<ContentBlock> ToContentBlocks(this IEnumerable<ResourceContents> resources) =>
        resources.Select(a => a.ToContentBlock());

    public static ContentBlock ToContentBlock(this ResourceContents resource) => new EmbeddedResourceBlock()
    {
        Resource = resource
    };

    public static string ToReverseDnsKey(this string url)
    {
        var uri = new Uri(url, UriKind.Absolute);

        var reversedHost = string.Join('.',
            uri.Host
               .Split('.', StringSplitOptions.RemoveEmptyEntries)
               .Reverse()
               .Select(p => p.ToLowerInvariant())
        );

        var path = uri.AbsolutePath
            .Trim('/')
            .ToLowerInvariant();

        return string.IsNullOrEmpty(path)
            ? reversedHost
            : $"{reversedHost}/{path}";
    }


}
