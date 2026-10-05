using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

internal static class LocalResponseFiles
{
    // Hashes preserve case-sensitive identities on Windows and avoid reserved names,
    // traversal, invalid characters, and path-length limits. JSON retains the original ID.
    internal static string Segment(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, ResponseJson.Default, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<T>(stream, ResponseJson.Default, cancellationToken);
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }
}
