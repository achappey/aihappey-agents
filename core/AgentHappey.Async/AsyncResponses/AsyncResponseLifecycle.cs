using System.Text.Json;
using AIHappey.Responses;

namespace AgentHappey.AsyncResponses;

/// <summary>Response protocol state, independent of queue transport or persistence.</summary>
public static class AsyncResponseLifecycle
{
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
            JsonSerializer.Serialize(request, ResponseJson.Default), ResponseJson.Default)
            ?? throw new InvalidOperationException("Failed to clone background response request.");
        clone.Stream = false;
        clone.Background = false;
        return clone;
    }

    public static ResponseResult NormalizeBackgroundResult(AsyncResponsesQueueMessage message, ResponseResult result)
    {
        result.Id = message.ResponseId;
        result.CreatedAt = message.CreatedAt;
        result.Status = result.Error is null ? "completed" : "failed";
        result.CompletedAt ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        result.Model = string.IsNullOrWhiteSpace(result.Model) ? (message.Request.Model ?? "agent") : result.Model;
        EnsureBackgroundProperty(result);
        return result;
    }

    public static void EnsureBackgroundProperty(ResponseResult response)
    {
        response.AdditionalProperties ??= new Dictionary<string, JsonElement>();
        response.AdditionalProperties["background"] = JsonSerializer.SerializeToElement(true, ResponseJson.Default);
    }
}
