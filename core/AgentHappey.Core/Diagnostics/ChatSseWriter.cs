using System.Text.Json;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHappey.Core.Diagnostics;

/// <summary>
/// Chat-only presentation adapter. Normal and diagnostic parts share a single awaited
/// writer, including connection parts and errors. No event history is retained.
/// </summary>
public sealed class ChatSseWriter : IProviderDebugSink, IDisposable
{
    public const string DebugPartType = "data-aihappey-debug";
    private static readonly object ContextKey = new();
    private readonly HttpContext _context;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly IDisposable? _subscription;

    public ChatSseWriter(HttpContext context)
    {
        _context = context;
        if (context.Items.ContainsKey(ContextKey))
            throw new InvalidOperationException("A chat SSE writer is already active for this request.");
        var events = context.RequestServices.GetRequiredService<RequestDebugEvents>();
        context.Items[ContextKey] = this;
        if (events.Enabled)
            _subscription = events.Subscribe(this);
    }

    internal static ChatSseWriter? Current(HttpContext context)
        => context.Items.TryGetValue(ContextKey, out var writer) ? writer as ChatSseWriter : null;

    public async ValueTask WriteAsync(UIMessagePart part, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            await _context.Response.WriteAsync($"data: {JsonSerializer.Serialize(part, JsonSerializerOptions.Web)}\n\n", cancellationToken);
            await _context.Response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            _writes.Release();
        }
    }

    public ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken)
        => WriteAsync(new DataUIPart
        {
            Type = DebugPartType,
            Id = $"{debugEvent.RequestId}:{debugEvent.Sequence}",
            Data = debugEvent,
            Transient = true
        }, cancellationToken);

    public void Dispose()
    {
        _subscription?.Dispose();
        _context.Items.Remove(ContextKey);
    }
}
