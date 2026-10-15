# aihappey-agents

A .NET agent and workflow runtime built on [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

Define agents and multi-agent workflows in portable YAML or client-side agent JSON, connect models, tools, [MCP](https://modelcontextprotocol.io/), [Skills](https://agentskills.io) and [Plugins](https://agent-plugins.org) and execute them through a consistent runtime exposed through familiar OpenAI- and AI SDK-compatible APIs.

The runtime supports the standard [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) orchestration patterns:

- **Sequential** — agents execute one after another
- **Concurrent** — agents execute in parallel
- **Handoff** — control moves between specialized agents
- **Group Chat** — agents collaborate in a shared conversation
- **Magentic** — a manager agent dynamically coordinates the team

**Documentation:** https://docs.aihappey.com/agents  
**API:** https://agents.aihappey.net

## Quick start

```bash
curl "https://agents.aihappey.net/v1/responses" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "model": "OpenAIAgent",
    "input": "Say hi"
  }'
```

See the [API documentation](https://docs.aihappey.com/agents) for endpoints, authentication, request schemas and examples.

## Background response storage

Azure background responses store the request and execution context in the existing
`AsyncAgents:ResponseContainerName` blob container (default `agents-async-responses`),
under `requests/`. Queue messages contain only an opaque payload identifier, so
large documents do not need to fit Azure Queue Storage's Base64-encoded message limit.
Polling results remain separate under `responses/`; request payloads are not returned
by response listing or polling.

Workers accept both payload references and older inline queue messages. Deploy the
updated workers together with the enqueueing runtime; older workers cannot resolve
the new references. Local runtimes continue using their existing disk-backed queue.
Payloads are removed after processing, including agent failures, and retained during
host cancellation or retryable reads. An ambiguous enqueue transport failure retains
the payload because the queue may have accepted the message.