using System.Text;
using System.Text.Json;
using AgentHappey.Core;
using AIHappey.Responses;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Options;

namespace AgentHappey.AsyncResponses;

public sealed class AzureAsyncResponsesService : IAsyncResponsesService
{
    private const int MaxQueueMessageUtf8Bytes = 60 * 1024;
    private readonly QueueClient queue;
    private readonly IAsyncResponseStore store;

    public AzureAsyncResponsesService(IOptions<AsyncAgentsConfig> options, IAsyncResponseStore store)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            throw new InvalidOperationException("AsyncAgents:ConnectionString is required.");

        queue = new QueueClient(
            config.ConnectionString,
            config.QueueName ?? "agents-async",
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        this.store = store;
    }

    public bool IsEnabled => true;

    public async Task<ResponseResult> EnqueueAsync(
        ResponseRequest request,
        AsyncResponsesRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var response = CreateQueuedResponse(request);
        var message = new AsyncResponsesQueueMessage
        {
            ResponseId = response.Id,
            CreatedAt = response.CreatedAt,
            Request = CloneForQueue(request),
            Context = context
        };

        var json = JsonSerializer.Serialize(message, ResponseJson.Default);
        var messageSize = Encoding.UTF8.GetByteCount(json);
        if (messageSize > MaxQueueMessageUtf8Bytes)
            throw new InvalidOperationException($"Background response queue message is too large ({messageSize} bytes). Reduce request size or externalize large file inputs.");

        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await store.SaveAsync(response, cancellationToken, context.UserId);
        await queue.SendMessageAsync(json, cancellationToken);

        return response;
    }

    public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.GetAsync(responseId, cancellationToken, userId);

    public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
        => store.ListAsync(cancellationToken, userId);

    public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
        => store.DeleteAsync(responseId, cancellationToken, userId);

    public static ResponseResult CreateQueuedResponse(ResponseRequest request)
        => new()
        {
            Id = $"resp_{Guid.NewGuid():N}",
            Object = "response",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = null,
            Status = "queued",
            Model = string.IsNullOrWhiteSpace(request.Model) ? "agent" : request.Model!,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            Text = request.Text,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>().ToList() ?? [],
            Reasoning = request.Reasoning,
            Store = request.Store,
            MaxOutputTokens = request.MaxOutputTokens,
            ServiceTier = request.ServiceTier,
            Output = [],
            Metadata = request.Metadata,
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["background"] = JsonSerializer.SerializeToElement(true, ResponseJson.Default)
            }
        };

    public static ResponseRequest CloneForQueue(ResponseRequest request)
    {
        var clone = JsonSerializer.Deserialize<ResponseRequest>(
            JsonSerializer.Serialize(request, ResponseJson.Default),
            ResponseJson.Default) ?? throw new InvalidOperationException("Failed to clone background response request.");

        clone.Stream = false;
        clone.Background = false;
        return clone;
    }
}
