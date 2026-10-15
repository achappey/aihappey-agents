using System.Text;
using System.Text.Json;
using AgentHappey.AsyncResponses;
using AIHappey.Responses;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentHappey.Tests;

public sealed class AsyncResponsesPayloadTests
{
    [Theory]
    [InlineData(1024, false)]
    [InlineData(60 * 1024, false)]
    [InlineData(165884, false)]
    [InlineData(1024 * 1024, true)]
    public async Task Azure_requests_use_small_opaque_references_and_round_trip(int size, bool file)
    {
        var fixture = new Fixture();
        var request = CreateRequest(size, file);
        var response = await fixture.Service.EnqueueAsync(request, Context());
        var reference = Assert.Single(fixture.Queue.Messages);
        using var json = JsonDocument.Parse(reference);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.True(Guid.TryParseExact(json.RootElement.GetProperty("payload_id").GetString(), "N", out _));
        Assert.True(Convert.ToBase64String(Encoding.UTF8.GetBytes(reference)).Length < 64 * 1024);
        Assert.DoesNotContain("private", reference);
        Assert.Equal("queued", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);
        Assert.Null(await fixture.Service.GetAsync(response.Id, userId: "other-user"));

        var stored = await fixture.Payloads.ReadAsync(reference, default);
        Assert.Equal(size > 1024, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(stored, ResponseJson.Default)) > 60 * 1024);
        var expected = AzureAsyncResponsesService.CloneForQueue(request);
        Assert.Equal(JsonSerializer.Serialize(expected, ResponseJson.Default),
            JsonSerializer.Serialize(stored.Request, ResponseJson.Default));
        Assert.False(stored.Request.Background);
        Assert.False(stored.Request.Stream);
        Assert.True(request.Background);
        Assert.True(request.Stream);
        Assert.Equal("private-context", stored.Context.CorrelationId);
        Assert.StartsWith("requests/", Assert.Single(fixture.Container.Blobs.Keys));

        await fixture.Worker.ProcessMessageAsync(Delivery(reference), default);

        var completed = await fixture.Service.GetAsync(response.Id, userId: "private-user");
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(response.CreatedAt, completed.CreatedAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(response.Id, Assert.Single(fixture.Processor.Processed).ResponseId);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Legacy_inline_messages_are_still_processed()
    {
        var fixture = new Fixture();
        var request = CreateRequest(1024);
        var response = AzureAsyncResponsesService.CreateQueuedResponse(request);
        var message = new AsyncResponsesQueueMessage
        {
            ResponseId = response.Id,
            CreatedAt = response.CreatedAt,
            Request = AzureAsyncResponsesService.CloneForQueue(request),
            Context = Context()
        };

        await fixture.Worker.ProcessMessageAsync(Delivery(JsonSerializer.Serialize(message, ResponseJson.Default)), default);

        Assert.Single(fixture.Processor.Processed);
        Assert.Equal("completed", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Processor_failure_persists_failed_response_and_cleans_payload()
    {
        var fixture = new Fixture();
        var response = await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        fixture.Processor.Failure = new InvalidOperationException("Agent failed.");

        await fixture.Worker.ProcessMessageAsync(Delivery(fixture.Queue.Messages.Single()), default);

        var failed = await fixture.Service.GetAsync(response.Id, userId: "private-user");
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("Agent failed.", failed.Error!.Message);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Worker_logs_do_not_include_request_context_or_exception_contents()
    {
        var fixture = new Fixture();
        await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        fixture.Processor.Failure = new InvalidOperationException("private request content must not be logged");

        await fixture.Worker.ProcessMessageAsync(Delivery(fixture.Queue.Messages.Single()), default);

        Assert.NotEmpty(fixture.Logger.Entries);
        Assert.All(fixture.Logger.Entries, entry =>
        {
            Assert.DoesNotContain("private", entry.Message);
            Assert.Null(entry.Exception);
        });
    }

    [Fact]
    public async Task Host_cancellation_keeps_payload_for_redelivery()
    {
        var fixture = new Fixture();
        await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        var delivery = Delivery(fixture.Queue.Messages.Single());
        using var cancellation = new CancellationTokenSource();
        fixture.Processor.OnProcess = () =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fixture.Worker.ProcessMessageAsync(delivery, cancellation.Token));
        Assert.Single(fixture.Container.Blobs);
        Assert.Equal(0, fixture.Queue.Deleted);

        fixture.Processor.OnProcess = null;
        await fixture.Worker.ProcessMessageAsync(delivery, default);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Payload_read_failure_retries_without_deleting_payload()
    {
        var fixture = new Fixture();
        await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        var reference = fixture.Queue.Messages.Single();
        fixture.Container.ReadFailure = new IOException("Storage unavailable.");

        await fixture.Worker.ProcessMessageAsync(Delivery(reference), default);
        Assert.Single(fixture.Container.Blobs);
        Assert.Empty(fixture.Processor.Processed);
        Assert.Equal(0, fixture.Queue.Deleted);

        fixture.Container.ReadFailure = null;
        await fixture.Worker.ProcessMessageAsync(Delivery(reference, 2), default);
        Assert.Single(fixture.Processor.Processed);
        Assert.Empty(fixture.Container.Blobs);
    }

    [Fact]
    public async Task Storage_outage_beyond_poison_retry_limit_keeps_payload_until_recovery()
    {
        var fixture = new Fixture();
        var response = await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        fixture.Container.ReadFailure = new IOException("Storage unavailable.");
        var reference = fixture.Queue.Messages.Single();

        await fixture.Worker.ProcessMessageAsync(Delivery(reference, 5), default);

        Assert.Single(fixture.Container.Blobs);
        Assert.Equal(0, fixture.Queue.Deleted);
        Assert.Equal("queued", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);
        fixture.Container.ReadFailure = null;
        await fixture.Worker.ProcessMessageAsync(Delivery(reference, 6), default);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
        Assert.Equal("completed", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);
    }

    [Fact]
    public async Task Acknowledgement_failure_does_not_change_terminal_result_or_run_agent_again()
    {
        var fixture = new Fixture();
        var response = await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        var reference = fixture.Queue.Messages.Single();
        fixture.Queue.DeleteFailure = new IOException("Queue unavailable.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Worker.ProcessMessageAsync(Delivery(reference), default));
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal("completed", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);

        fixture.Queue.DeleteFailure = null;
        await fixture.Worker.ProcessMessageAsync(Delivery(reference, 5), default);
        Assert.Single(fixture.Processor.Processed);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_queue_entry_for_retry_without_downgrading_result()
    {
        var fixture = new Fixture();
        var response = await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        var reference = fixture.Queue.Messages.Single();
        fixture.Container.DeleteFailure = new IOException("Storage unavailable.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Worker.ProcessMessageAsync(Delivery(reference), default));

        Assert.Single(fixture.Container.Blobs);
        Assert.Equal(0, fixture.Queue.Deleted);
        Assert.Equal("completed", (await fixture.Service.GetAsync(response.Id, userId: "private-user"))!.Status);
        fixture.Container.DeleteFailure = null;
        await fixture.Worker.ProcessMessageAsync(Delivery(reference, 2), default);
        Assert.Single(fixture.Processor.Processed);
        Assert.Empty(fixture.Container.Blobs);
        Assert.Equal(1, fixture.Queue.Deleted);
    }

    [Fact]
    public async Task Failed_failure_persistence_keeps_payload_for_retry()
    {
        var fixture = new Fixture();
        await fixture.Service.EnqueueAsync(CreateRequest(165884), Context());
        fixture.Store.SaveFailure = new IOException("Storage unavailable.");

        await fixture.Worker.ProcessMessageAsync(Delivery(fixture.Queue.Messages.Single()), default);

        Assert.Single(fixture.Container.Blobs);
        Assert.Equal(0, fixture.Queue.Deleted);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"payload_id\":\"../private\"}")]
    [InlineData("{\"response_id\":\"resp_missing_request\"}")]
    public async Task Invalid_messages_are_acknowledged_after_existing_retry_limit(string message)
    {
        var fixture = new Fixture();

        await fixture.Worker.ProcessMessageAsync(Delivery(message), default);
        Assert.Equal(0, fixture.Queue.Deleted);
        await fixture.Worker.ProcessMessageAsync(Delivery(message, 5), default);
        Assert.Equal(1, fixture.Queue.Deleted);
        Assert.Empty(fixture.Container.Blobs);
    }

    [Fact]
    public async Task Failed_upload_before_publish_cleans_any_committed_blob()
    {
        var fixture = new Fixture();
        fixture.Container.UploadFailure = new IOException("Upload acknowledgement lost.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueAsync(CreateRequest(165884), Context()));

        Assert.Empty(fixture.Container.Blobs);
        Assert.Empty(fixture.Queue.Messages);
        Assert.Empty(await fixture.Service.ListAsync(userId: "private-user"));
    }

    [Fact]
    public async Task Response_save_failure_before_publish_removes_payload()
    {
        var fixture = new Fixture();
        fixture.Store.SaveFailure = new IOException("Storage unavailable.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueAsync(CreateRequest(165884), Context()));

        Assert.Empty(fixture.Queue.Messages);
        Assert.Empty(fixture.Container.Blobs);
    }

    [Fact]
    public async Task Definitive_queue_rejection_removes_unpublished_payload_and_response()
    {
        var fixture = new Fixture();
        fixture.Queue.SendFailure = new RequestFailedException(400, "Rejected.");

        await Assert.ThrowsAsync<RequestFailedException>(() => fixture.Service.EnqueueAsync(CreateRequest(165884), Context()));

        Assert.Empty(fixture.Container.Blobs);
        Assert.Empty(await fixture.Service.ListAsync(userId: "private-user"));
    }

    [Fact]
    public async Task Ambiguous_send_failure_keeps_payload_for_possible_delivery()
    {
        var fixture = new Fixture();
        fixture.Queue.SendFailure = new IOException("Connection lost after sending.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueAsync(CreateRequest(165884), Context()));

        Assert.Single(fixture.Container.Blobs);
        Assert.Single(await fixture.Service.ListAsync(userId: "private-user"));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(165884)]
    public async Task Local_queue_accepts_and_processes_document_inputs(int size)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new LocalResponseStorageOptions { RootPath = root });
            using var queue = new LocalAsyncResponsesQueue(options);
            var store = new LocalAsyncResponseStore(options);
            var service = new LocalAsyncResponsesService(queue, store);
            var processor = new RecordingProcessor();
            using var worker = new LocalAsyncResponsesWorker(queue, processor, store,
                NullLogger<LocalAsyncResponsesWorker>.Instance);
            var request = CreateRequest(size, file: true);
            var response = await service.EnqueueAsync(request, Context());

            Assert.True(await worker.ProcessNextAsync());

            Assert.Equal(JsonSerializer.Serialize(AzureAsyncResponsesService.CloneForQueue(request), ResponseJson.Default),
                JsonSerializer.Serialize(Assert.Single(processor.Processed).Request, ResponseJson.Default));
            Assert.Equal("completed", (await service.GetAsync(response.Id, userId: "private-user"))!.Status);
            Assert.False(await worker.ProcessNextAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ResponseRequest CreateRequest(int size, bool file = false)
    {
        if (!file)
            return new ResponseRequest
            {
                Model = "agent",
                Input = new ResponseInput("private extracted PDF text " + new string('文', size)),
                Background = true,
                Stream = true
            };

        return JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new
        {
            model = "agent",
            background = true,
            stream = true,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_file", filename = "private-document.pdf",
                            file_data = "data:application/pdf;base64," + Convert.ToBase64String(new byte[size]) }
                    }
                }
            }
        }), ResponseJson.Default)!;
    }

    private static AsyncResponsesRequestContext Context()
        => new() { UserId = "private-user", CorrelationId = "private-context" };

    private static QueueMessage Delivery(string text, long attempts = 1)
        => QueuesModelFactory.QueueMessage("message-id", "receipt", text, attempts);

    private sealed class Fixture
    {
        internal readonly MemoryContainer Container = new();
        internal readonly MemoryQueue Queue = new();
        internal readonly MemoryStore Store = new();
        internal readonly RecordingProcessor Processor = new();
        internal readonly RecordingLogger Logger = new();
        internal readonly AzureAsyncResponsePayloadStore Payloads;
        internal readonly AzureAsyncResponsesService Service;
        internal readonly AsyncResponsesWorker Worker;

        internal Fixture()
        {
            Payloads = new AzureAsyncResponsePayloadStore(Container);
            Service = new AzureAsyncResponsesService(Queue, Store, Payloads);
            Worker = new AsyncResponsesWorker(Queue, Payloads, Processor, Store, Logger);
        }
    }

    private sealed class RecordingLogger : ILogger<AsyncResponsesWorker>
    {
        internal readonly List<(string Message, Exception? Exception)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((formatter(state, exception), exception));
    }

    private sealed class RecordingProcessor : IAsyncResponsesProcessor
    {
        internal readonly List<AsyncResponsesQueueMessage> Processed = [];
        internal Exception? Failure;
        internal Action? OnProcess;

        public Task<ResponseResult> ProcessAsync(AsyncResponsesQueueMessage message, CancellationToken cancellationToken = default)
        {
            OnProcess?.Invoke();
            if (Failure is not null)
                throw Failure;
            Processed.Add(message);
            return Task.FromResult(new ResponseResult { Object = "response" });
        }
    }

    private sealed class MemoryStore : IAsyncResponseStore
    {
        private readonly Dictionary<(string?, string), ResponseResult> responses = [];
        internal Exception? SaveFailure;

        public Task SaveAsync(ResponseResult response, CancellationToken cancellationToken = default, string? userId = null)
        {
            if (SaveFailure is not null)
                throw SaveFailure;
            responses[(userId, response.Id)] = response;
            return Task.CompletedTask;
        }

        public Task<ResponseResult?> GetAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult(responses.GetValueOrDefault((userId, responseId)));

        public Task<IReadOnlyList<ResponseResult>> ListAsync(CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult<IReadOnlyList<ResponseResult>>(responses.Where(pair => pair.Key.Item1 == userId)
                .Select(pair => pair.Value).ToList());

        public Task<bool> DeleteAsync(string responseId, CancellationToken cancellationToken = default, string? userId = null)
            => Task.FromResult(responses.Remove((userId, responseId)));
    }

    private sealed class MemoryQueue : QueueClient
    {
        internal readonly List<string> Messages = [];
        internal int Deleted;
        internal Exception? SendFailure;
        internal Exception? DeleteFailure;

        public override Task<Response> CreateIfNotExistsAsync(
            IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Response>(null!);

        public override Task<Response<SendReceipt>> SendMessageAsync(string messageText, CancellationToken cancellationToken = default)
        {
            if (SendFailure is not null)
                throw SendFailure;
            Messages.Add(messageText);
            return Task.FromResult<Response<SendReceipt>>(null!);
        }

        public override Task<Response> DeleteMessageAsync(string messageId, string popReceipt,
            CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null)
                throw DeleteFailure;
            Deleted++;
            return Task.FromResult<Response>(null!);
        }
    }

    private sealed class MemoryContainer : BlobContainerClient
    {
        internal readonly Dictionary<string, byte[]> Blobs = [];
        internal Exception? ReadFailure;
        internal Exception? DeleteFailure;
        internal Exception? UploadFailure;

        public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
            PublicAccessType publicAccessType = PublicAccessType.None,
            IDictionary<string, string>? metadata = null,
            BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Response<BlobContainerInfo>>(null!);

        public override BlobClient GetBlobClient(string blobName) => new MemoryBlob(this, blobName);
    }

    private sealed class MemoryBlob(MemoryContainer container, string name) : BlobClient
    {
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, bool overwrite,
            CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream();
            await content.CopyToAsync(stream, cancellationToken);
            container.Blobs[name] = stream.ToArray();
            if (container.UploadFailure is not null)
                throw container.UploadFailure;
            return null!;
        }

        public override Task<Stream> OpenReadAsync(BlobOpenReadOptions options, CancellationToken cancellationToken = default)
        {
            if (container.ReadFailure is not null)
                throw container.ReadFailure;
            if (!container.Blobs.TryGetValue(name, out var content))
                throw new RequestFailedException(404, "Payload missing.");
            return Task.FromResult<Stream>(new MemoryStream(content));
        }

        public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = default,
            BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            if (container.DeleteFailure is not null)
                throw container.DeleteFailure;
            return Task.FromResult(Response.FromValue(container.Blobs.Remove(name), null!));
        }
    }
}
