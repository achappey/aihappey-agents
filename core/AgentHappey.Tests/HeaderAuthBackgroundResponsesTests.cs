using System.Text.Json;
using AgentHappey.AsyncResponses;
using AgentHappey.Common.Models;
using AgentHappey.Core;
using AgentHappey.Core.ChatRuntime;
using AgentHappey.Core.Responses;
using AgentHappey.HeaderAuth;
using AgentHappey.HeaderAuth.AsyncResponses;
using AgentHappey.HeaderAuth.Controllers;
using AIHappey.Responses;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentHappey.Tests;

public sealed class HeaderAuthBackgroundResponsesTests
{
    private static readonly string[] Secrets =
    [
        "header-api-SECRET", "custom-header-SECRET", "access-token-SECRET", "tool-auth-SECRET",
        "provider-header-SECRET", "mcp-header-SECRET", "nested-api-SECRET", "url-api-SECRET", "url-password-SECRET"
    ];

    [Fact]
    public async Task Controller_real_processor_and_runtime_forward_credentials_but_poll_and_delete_only_durable_safe_responses()
    {
        var f = new Fixture();
        using var services = new ServiceCollection().BuildServiceProvider();
        var hostOptions = Options.Create(new HeaderAuthHostOptions { PortableMcpOnly = true });
        var config = Options.Create(new Config { AiConfig = new AiConfig { AiEndpoint = "https://example.test/" } });
        var orchestrator = new ChatRuntimeOrchestrator(new StreamingContentMapper(), null!);
        var mapper = new ResponsesNativeMapper();
        var calls = 0;
        var factory = new StubHttpClientFactory((httpRequest, _) =>
        {
            calls++;
            Assert.Equal("header-api-SECRET", Assert.Single(httpRequest.Headers.GetValues("X-Api-Key")));
            Assert.Equal("provider-header-SECRET", Assert.Single(httpRequest.Headers.GetValues("X-Provider-Key")));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"resp_downstream","object":"response","created_at":1,"status":"completed","model":"test/model",
                     "output":[{"id":"msg_answer","type":"message","role":"assistant","status":"completed",
                       "content":[{"type":"output_text","text":"real runtime answer; header-api-SECRET","annotations":[]}]}]}
                    """, System.Text.Encoding.UTF8, "application/json")
            });
        });
        var processor = new HeaderAuthAsyncResponsesProcessor(config, factory, orchestrator, mapper, hostOptions, services);
        using var service = new HeaderAuthBackgroundResponsesService(f.Store, processor,
            Options.Create(new HeaderAuthBackgroundOptions()), f.Clock, f.Logger);
        var controller = new ResponsesController(factory, config, orchestrator, mapper, service, hostOptions,
            new ForegroundResponsePersistence(hostOptions, services))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Api-Key"] = "header-api-SECRET";
        var request = new ResponseRequest
        {
            Input = new ResponseInput("run"), Background = true, Store = false,
            Metadata = new()
            {
                ["business"] = "preserved",
                ["agents"] = new[]
                {
                    new Agent
                    {
                        Name = "TestAgent", Instructions = "Answer briefly.",
                        Model = new AIModel { Id = "test/model", Options = new AIModelOptions { Temperature = 0 },
                            ProviderHeaders = new() { ["X-Provider-Key"] = "provider-header-SECRET" } }
                    }
                }
            }
        };
        var queued = Assert.IsType<ResponseResult>(Assert.IsType<OkObjectResult>(await controller.Post(request, default)).Value);
        AssertSafe(f);
        await service.ProcessNextAsync();
        var completed = Assert.IsType<ResponseResult>(Assert.IsType<OkObjectResult>(await controller.Get(queued.Id, default)).Value);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(1, calls);
        Assert.Contains("real runtime answer", JsonSerializer.Serialize(completed.Output, ResponseJson.Default));
        Assert.Equal("preserved", Metadata(completed).GetProperty("business").GetString());
        AssertSafe(f);
        Assert.IsType<OkObjectResult>(await controller.Delete(queued.Id, default));
        Assert.IsType<NotFoundObjectResult>(await controller.Get(queued.Id, default));
    }

    [Fact]
    public async Task Credentials_execute_in_memory_but_only_safe_responses_are_written_and_remain_durable()
    {
        var f = new Fixture();
        using var service = f.Service();
        var request = Request();
        var context = Context();
        var queued = await service.EnqueueAsync(request, context);
        Assert.Equal("queued", queued.Status);
        Assert.False(queued.Store); // Background lifecycle remains durable even when store=false.
        Assert.Equal("preserved", Metadata(queued).GetProperty("business").GetString());
        AssertSafe(f);
        Assert.All(f.Container.Blobs.Keys, name => Assert.StartsWith("responses/", name));
        Assert.All(f.Container.Blobs.Values, blob => Assert.Equal("headerauth_execution_deadline", Assert.Single(blob.Metadata).Key));

        // Mutation/disposal of the HTTP request's objects must not affect queued execution.
        request.Metadata!.Clear();
        context.Headers.Clear();
        f.Processor.Run = (message, _) =>
        {
            Assert.False(message.Request.Stream);
            Assert.False(message.Request.Background);
            Assert.Equal("header-api-SECRET", message.Context.Headers["X-Api-Key"]);
            Assert.Equal("access-token-SECRET", message.Context.UserAccessToken);
            var raw = JsonSerializer.Serialize(message.Request, ResponseJson.Default);
            foreach (var secret in Secrets.Skip(3)) Assert.Contains(secret, raw);
            return Task.FromResult(Result(message, "answer preserved; echo header-api-SECRET and tool-auth-SECRET"));
        };
        Assert.True(await service.ProcessNextAsync());
        Assert.Equal(1, f.Processor.Calls);
        AssertSafe(f);

        // A second instance has no credentials, but can retrieve/list/delete the full durable Response.
        using var other = f.Service();
        var completed = await other.GetAsync(queued.Id);
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(queued.Id, completed.Id);
        Assert.Equal(queued.CreatedAt, completed.CreatedAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("preserved", Metadata(completed).GetProperty("business").GetString());
        Assert.Contains("answer preserved", JsonSerializer.Serialize(completed.Output, ResponseJson.Default));
        Assert.Contains("[redacted]", JsonSerializer.Serialize(completed.Output, ResponseJson.Default));
        Assert.Equal(queued.Id, Assert.Single(await other.ListAsync()).Id);
        Assert.True(await other.DeleteAsync(queued.Id));
        Assert.Null(await service.GetAsync(queued.Id));
        Assert.Empty(f.Container.Blobs);
        Assert.False(await other.DeleteAsync(queued.Id));
    }

    [Fact]
    public async Task Debug_json_tool_credentials_and_known_echoes_are_redacted_without_dropping_legitimate_metadata_or_schema()
    {
        var f = new Fixture();
        using var service = f.Service();
        var queued = await service.EnqueueAsync(Request(), Context());
        f.Processor.Run = (message, _) =>
        {
            var result = Result(message, "normal output");
            result.Metadata!["debug"] = JsonSerializer.Serialize(new
            {
                request = message.Request,
                headers = message.Context.Headers,
                safe = "debug diagnostic retained",
                auth = new { type = "string", arbitrary = "new-tool-SECRET" }
            }, ResponseJson.Default);
            result.Metadata["echo"] = "new-tool-SECRET and access-token-SECRET";
            result.Metadata["echo-header-api-SECRET"] = "must not persist credential-bearing keys";
            return Task.FromResult(result);
        };
        await service.ProcessNextAsync();

        AssertSafe(f);
        Assert.DoesNotContain("new-tool-SECRET", string.Join('\n', f.Container.Writes));
        var completed = (await service.GetAsync(queued.Id))!;
        var json = JsonSerializer.SerializeToElement(completed, ResponseJson.Default);
        Assert.Equal("normal output", json.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        var tool = json.GetProperty("tools")[0];
        Assert.True(tool.TryGetProperty("parameters", out var parameters), tool.GetRawText());
        Assert.True(parameters.TryGetProperty("properties", out var properties), parameters.GetRawText());
        Assert.True(properties.TryGetProperty("api_key", out var apiKey), properties.GetRawText());
        Assert.Equal("string", apiKey.GetProperty("type").GetString());
        Assert.Equal("preserved", Metadata(completed).GetProperty("business").GetString());
        Assert.Contains("debug diagnostic retained", Metadata(completed).GetProperty("debug").GetString());
    }

    [Fact]
    public async Task Restart_does_not_replay_credentials_and_another_instance_expires_orphaned_work()
    {
        var f = new Fixture();
        string id;
        using (var first = f.Service()) id = (await first.EnqueueAsync(Request(), Context())).Id;
        using var restarted = f.Service();
        Assert.False(await restarted.ProcessNextAsync());
        Assert.Equal("queued", (await restarted.GetAsync(id))!.Status);
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        await restarted.SweepAsync();
        var failed = await restarted.GetAsync(id);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("background_execution_expired", failed.Error!.Code);
        Assert.NotNull(failed.CompletedAt);
        Assert.Equal("preserved", Metadata(failed).GetProperty("business").GetString());
        Assert.Equal(0, f.Processor.Calls);
        AssertSafe(f);
    }

    [Fact]
    public async Task Polling_alone_finalizes_expired_work_and_terminal_results_have_no_automatic_retention_limit()
    {
        var f = new Fixture();
        using var service = f.Service();
        var queued = await service.EnqueueAsync(Request(), Context());
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("failed", (await service.GetAsync(queued.Id))!.Status);
        f.Clock.Advance(TimeSpan.FromDays(365));
        await service.SweepAsync();
        Assert.Equal("failed", (await service.GetAsync(queued.Id))!.Status);
        Assert.True(await service.DeleteAsync(queued.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_before_or_during_execution_never_recreates_response(bool during)
    {
        var f = new Fixture();
        using var owner = f.Service();
        using var other = f.Service();
        var queued = await owner.EnqueueAsync(Request(), Context());
        f.Processor.Run = async (message, _) =>
        {
            Assert.True(await other.DeleteAsync(message.ResponseId));
            return Result(message, "late output");
        };
        if (!during) Assert.True(await owner.DeleteAsync(queued.Id));
        await owner.ProcessNextAsync();
        Assert.Equal(during ? 1 : 0, f.Processor.Calls);
        Assert.Null(await other.GetAsync(queued.Id));
        Assert.Empty(f.Container.Blobs);
        AssertSafe(f);
    }

    [Fact]
    public async Task Cross_instance_expiry_wins_against_late_completion()
    {
        var f = new Fixture();
        using var owner = f.Service();
        using var other = f.Service();
        var queued = await owner.EnqueueAsync(Request(), Context());
        f.Processor.Run = async (message, _) =>
        {
            f.Clock.Advance(TimeSpan.FromMinutes(16));
            Assert.Equal("failed", (await other.GetAsync(message.ResponseId))!.Status);
            return Result(message, "must not overwrite terminal failure");
        };
        await owner.ProcessNextAsync();
        var result = (await other.GetAsync(queued.Id))!;
        Assert.Equal("failed", result.Status);
        Assert.Equal("background_execution_expired", result.Error!.Code);
    }

    [Fact]
    public async Task Processor_failures_have_safe_durable_errors_and_safe_logs_without_execution_retry()
    {
        var f = new Fixture();
        using var service = f.Service();
        var queued = await service.EnqueueAsync(Request(), Context());
        f.Processor.Run = (_, _) => throw new InvalidOperationException("Authorization: Bearer header-api-SECRET; body tool-auth-SECRET");
        await service.ProcessNextAsync();
        Assert.Equal("failed", (await service.GetAsync(queued.Id))!.Status);
        Assert.Equal("background_execution_failed", (await service.GetAsync(queued.Id))!.Error!.Code);
        Assert.False(await service.ProcessNextAsync());
        Assert.Equal(1, f.Processor.Calls);
        AssertSafe(f);
    }

    [Fact]
    public async Task Capacity_is_bounded_and_enqueue_failures_do_not_leave_execution_payloads()
    {
        var f = new Fixture();
        using var service = f.Service(capacity: 1);
        var first = await service.EnqueueAsync(Request(), Context());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(Request(), Context()));
        Assert.Single(f.Container.Blobs);
        await service.DeleteAsync(first.Id);
        await service.ProcessNextAsync(); // Drain the credential-free ID of the deleted job.
        var next = await service.EnqueueAsync(Request(), Context());
        await service.ProcessNextAsync();
        Assert.Equal("completed", (await service.GetAsync(next.Id))!.Status);
        AssertSafe(f);
    }

    [Fact]
    public async Task Ambiguous_response_upload_failure_only_leaves_safe_data_and_releases_capacity()
    {
        var f = new Fixture();
        using var service = f.Service(capacity: 1);
        f.Container.FailNextUploadAfterCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(Request(), Context()));
        Assert.Empty(f.Container.Blobs);
        Assert.False(await service.ProcessNextAsync());
        await service.EnqueueAsync(Request(), Context());
        AssertSafe(f);
    }

    [Fact]
    public async Task Terminal_response_persistence_retry_does_not_repeat_agent_or_tool_execution()
    {
        var f = new Fixture();
        using var service = f.Service();
        var queued = await service.EnqueueAsync(Request(), Context());
        f.Processor.Run = (message, _) =>
        {
            f.Container.FailNextUploadAfterCommit = true;
            return Task.FromResult(Result(message, "completed before acknowledgement lost"));
        };
        await service.ProcessNextAsync();
        Assert.Equal(1, f.Processor.Calls);
        Assert.Equal("completed", (await service.GetAsync(queued.Id))!.Status);
        AssertSafe(f);
    }

    [Fact]
    public async Task Hosted_worker_starts_execution_independently_of_request_and_shutdown_cancels_active_execution()
    {
        var f = new Fixture();
        using var service = f.Service();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Processor.Run = async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.SetResult(); }
            throw new InvalidOperationException("Unreachable.");
        };
        await service.StartAsync(default);
        var queued = await service.EnqueueAsync(Request(), Context());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("in_progress", (await service.GetAsync(queued.Id))!.Status);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(shutdown.Token);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, f.Processor.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(Request(), Context()));
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        using var restarted = f.Service();
        Assert.Equal("failed", (await restarted.GetAsync(queued.Id))!.Status);
        AssertSafe(f);
    }

    [Fact]
    public async Task Expiry_releases_memory_capacity_even_during_storage_outage_and_never_executes_later()
    {
        var f = new Fixture();
        using var service = f.Service(capacity: 1);
        var queued = await service.EnqueueAsync(Request(), Context());
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        f.Container.ReadFailure = true;
        await Assert.ThrowsAsync<IOException>(() => service.SweepAsync());
        f.Container.ReadFailure = false;
        await service.ProcessNextAsync();
        Assert.Equal(0, f.Processor.Calls);
        await service.EnqueueAsync(Request(), Context());
        Assert.Equal("failed", (await service.GetAsync(queued.Id))!.Status);
        AssertSafe(f);
    }

    [Fact]
    public async Task Conditional_blob_updates_reject_stale_writes_and_cannot_resurrect_deleted_responses()
    {
        var f = new Fixture();
        using var service = f.Service();
        var queued = await service.EnqueueAsync(Request(), Context());
        var old = (await f.Store.ReadAsync(queued.Id, default, null))!;
        var changed = JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(old.Response, ResponseJson.Default), ResponseJson.Default)!;
        changed.Status = "in_progress";
        Assert.True(await f.Store.ReplaceAsync(old, changed, default, null));
        Assert.False(await f.Store.ReplaceAsync(old, old.Response, default, null));
        var latest = (await f.Store.ReadAsync(queued.Id, default, null))!;
        Assert.True(await service.DeleteAsync(queued.Id));
        Assert.False(await f.Store.ReplaceAsync(latest, changed, default, null));
        Assert.Empty(f.Container.Blobs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hosted_registration_is_explicitly_scoped_and_never_registers_a_durable_execution_worker(bool configured)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AsyncAgents:ConnectionString"] = configured ? "UseDevelopmentStorage=true" : null
        }).Build();
        var services = new ServiceCollection();
        services.AddHeaderAuthBackgroundResponses(config);
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(AzureAsyncResponsesService)
            || descriptor.ImplementationType == typeof(AsyncResponsesWorker)
            || descriptor.ImplementationType == typeof(LocalAsyncResponsesWorker));
        if (configured)
        {
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(HeaderAuthBackgroundResponsesService));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
            Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(HeaderAuthAsyncResponsesProcessor));
        }
        else Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(DisabledAsyncResponsesService));
    }

    private static ResponseRequest Request() => JsonSerializer.Deserialize<ResponseRequest>("""
        {
          "model": "test-agent", "input": "private input need not be persisted", "background": true, "store": false,
          "tools": [{"type":"function","name":"test_tool","parameters":{"type":"object","properties":{
            "api_key":{"type":"string","description":"An API key argument","default":"nested-api-SECRET"},
            "query":{"type":"string"}}},"authorization":"Bearer tool-auth-SECRET"}],
          "metadata": {
            "business":"preserved",
            "agents":[{"name":"test-agent","model":{"id":"test/model","providerHeaders":{"X-Api-Key":"provider-header-SECRET"}},
              "mcpServers":{"example":{"url":"https://example.test/mcp","headers":{"X-Custom":"mcp-header-SECRET"}}}}],
            "nested":{"api_key":"nested-api-SECRET"},
            "server_url":"https://user:url-password-SECRET@example.test/mcp?api_key=url-api-SECRET&region=eu"
          }
        }
        """, ResponseJson.Default)!;

    private static AsyncResponsesRequestContext Context() => new()
    {
        Headers = new() { ["X-Api-Key"] = "header-api-SECRET", ["X-Unusual"] = "custom-header-SECRET" },
        UserAccessToken = "access-token-SECRET",
        AiEndpoint = "https://example.test/"
    };

    private static ResponseResult Result(AsyncResponsesQueueMessage message, string output)
    {
        var result = AsyncResponseLifecycle.CreateQueuedResponse(message.Request);
        result.Output = JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(new
        {
            output = new[] { new { type = "message", role = "assistant", id = "msg_test", status = "completed",
                content = new[] { new { type = "output_text", text = output, annotations = Array.Empty<object>() } } } }
        }), ResponseJson.Default)!.Output;
        return result;
    }

    private static JsonElement Metadata(ResponseResult result) => JsonSerializer.SerializeToElement(result.Metadata, ResponseJson.Default);

    private static void AssertSafe(Fixture f)
    {
        var durable = string.Join('\n', f.Container.Writes.Concat(f.Container.Blobs.Values.Select(value => value.Json)));
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, durable);
            Assert.DoesNotContain(secret, string.Join('\n', f.Logger.Messages));
        }
        Assert.All(f.Logger.Exceptions, exception => Assert.Null(exception));
        Assert.DoesNotContain("private input need not be persisted", f.Container.Writes[0]);
    }

    private sealed class Fixture
    {
        internal readonly MemoryContainer Container = new();
        internal readonly RecordingProcessor Processor = new();
        internal readonly TestClock Clock = new();
        internal readonly RecordingLogger Logger = new();
        internal readonly HeaderAuthBackgroundResponseStore Store;
        internal Fixture() => Store = new(Container);
        internal HeaderAuthBackgroundResponsesService Service(int capacity = 64)
            => new(Store, Processor, Options.Create(new HeaderAuthBackgroundOptions { Capacity = capacity }), Clock, Logger);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class RecordingProcessor : IAsyncResponsesProcessor
    {
        internal int Calls;
        internal Func<AsyncResponsesQueueMessage, CancellationToken, Task<ResponseResult>> Run =
            (message, _) => Task.FromResult(Result(message, "answer preserved"));
        public Task<ResponseResult> ProcessAsync(AsyncResponsesQueueMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Run(message, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<HeaderAuthBackgroundResponsesService>
    {
        internal readonly List<string> Messages = [];
        internal readonly List<Exception?> Exceptions = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }

    private sealed record BlobEntry(string Json, Dictionary<string, string> Metadata, string Version);

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpHandler(responder));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class MemoryContainer : BlobContainerClient
    {
        internal readonly Dictionary<string, BlobEntry> Blobs = [];
        internal readonly List<string> Writes = [];
        internal bool FailNextUploadAfterCommit;
        internal bool ReadFailure;
        internal int Version;

        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null, BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null,
            CancellationToken cancellationToken = default) => Task.FromResult<Response<BlobContainerInfo>>(null!);

        public override BlobClient GetBlobClient(string blobName) => new MemoryBlob(this, blobName);

        public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits = BlobTraits.None, BlobStates states = BlobStates.None,
            string? prefix = null, CancellationToken cancellationToken = default)
        {
            if (ReadFailure) throw new IOException("Storage unavailable.");
            return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(Blobs.Keys
                .Where(name => name.StartsWith(prefix ?? "", StringComparison.Ordinal))
                .Select(name => BlobsModelFactory.BlobItem(name: name)).ToList(), null, null!)]);
        }
    }

    private sealed class MemoryBlob(MemoryContainer container, string name) : BlobClient
    {
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options,
            CancellationToken cancellationToken = default)
        {
            container.Blobs.TryGetValue(name, out var previous);
            if (options.Conditions.IfNoneMatch == ETag.All && previous is not null
                || options.Conditions.IfMatch is { } match && previous?.Version != match.ToString())
                throw new RequestFailedException(412, "ETag mismatch.");
            using var reader = new StreamReader(content);
            var json = await reader.ReadToEndAsync(cancellationToken);
            container.Writes.Add(json);
            container.Blobs[name] = new(json, new(options.Metadata), (++container.Version).ToString());
            if (container.FailNextUploadAfterCommit)
            {
                container.FailNextUploadAfterCommit = false;
                throw new IOException("Upload acknowledgement lost.");
            }
            return null!;
        }

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
        {
            if (container.ReadFailure) throw new IOException("Storage unavailable.");
            if (!container.Blobs.TryGetValue(name, out var entry)) throw new RequestFailedException(404, "Not found.");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(entry.Json),
                BlobsModelFactory.BlobDownloadDetails(metadata: entry.Metadata, eTag: new ETag(entry.Version))), null!));
        }

        public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = default,
            BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Response.FromValue(container.Blobs.Remove(name), null!));
    }
}
