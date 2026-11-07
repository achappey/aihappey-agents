using System.Text.Json;
using AIHappey.Responses;
using AgentHappey.Common.Models;

namespace AgentHappey.AsyncResponses;

/// <summary>Response protocol state, independent of queue transport or persistence.</summary>
public static class AsyncResponseLifecycle
{
    public static ResponseResult CreateQueuedResponse(AgentResponseRequest request)
        => new()
        {
            Id = $"resp_{Guid.NewGuid():N}",
            Object = "response",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = null,
            Status = "queued",
            Background = true,
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
            Metadata = request.Metadata
        };

    public static AgentResponseRequest CloneForQueue(AgentResponseRequest request)
    {
        var clone = JsonSerializer.Deserialize<AgentResponseRequest>(
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
        response.Background = true;
        // Legacy extension entries would otherwise serialize beside the typed property.
        // Match the protocol's case-insensitive deserialization without dropping other extensions.
        if (response.AdditionalProperties is null) return;
        foreach (var key in response.AdditionalProperties.Keys
            .Where(key => string.Equals(key, "background", StringComparison.OrdinalIgnoreCase)).ToList())
            response.AdditionalProperties.Remove(key);
    }
}
