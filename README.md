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