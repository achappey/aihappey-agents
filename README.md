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

## Per-run client tools

Supply additional function definitions in a Responses request's `tools` array
using the standard `type: "function"`, `name`, `description`, and `parameters`
fields. UI chat requests accept `tools` with `name`, `description`, `inputSchema`,
and optional `outputSchema`. These definitions apply only to that request; they
do not change the stored agent.

Configured tools still execute on the backend. Client tools are declarations
only: their function calls are returned for the caller to execute, including
when a model requests both kinds of tool in the same turn. UI streams mark
client calls with `providerExecuted: false` so the caller can execute them.
On `/api/chat`, submitting their completed results streams the same result-then-
approval handshake as server tools, before the agent continues. Resend the
conversation with the approvals answered to continue; rejected results follow
the existing server-tool denial behavior. Responses and workflows are unchanged.

To continue, resend the tool definitions and conversation history, including
the returned calls, backend results, and client results. Responses uses
`function_call_output` items correlated by `call_id`; UI chat uses its existing
tool output parts. No server-side session or new persistent state is required.
Unanswered client calls are omitted from inference replay until their results
are supplied, so abandoning a call does not prevent later conversation turns.
Client tool names must be unique (case-insensitive) and must not collide with
configured/backend tools or reserved runtime names; collisions reject the run
rather than replacing a tool or changing its execution owner. Client tools do
not add MCP connections, resources, prompts, or skills.

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