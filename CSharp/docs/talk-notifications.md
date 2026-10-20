# Talk notifications

`src/Maf.Handoff.Talk` wraps the supplied POD Chat SDK separately from the workflow
engine. The API registers its background sender and connects repository session
events to it. The CLI does not currently send Talk notifications.

The API publishes notifications for:

- Agent handoffs: the source agent finished its current step. This does not imply review approval.
- Clarification requests: the workflow is waiting for a human answer.
- Completed workflows and patches awaiting approval, including runs with no changes.
- Commit, push and PR/MR completion.
- Failed or blocked runs, failed approvals and PR/MR creation, cancellation and rejection.

Streaming tokens, tools and pending summaries do not produce messages. Session
status notifications are emitted on status or error changes, so repeated snapshot
persistence does not repeat a notification. Agent handoffs can recur during review
remediation and each handoff produces a message.

## Configuration

Notifications default to disabled and require no credentials until enabled. Supply
the settings under the API's `Talk` configuration section, using user secrets or
environment variables for credentials. Every property below is required when
enabled except the switches, queue port and timeout, which have the shown defaults.
Get the queue connection settings and destination thread ID from your Talk administrator.
The bot/service user must already be a participant in the destination conversation.

```json
{
  "Talk": {
    "Enabled": true,
    "NotifyAgentSteps": true,
    "IncludeDetails": false,
    "ThreadId": 12345,
    "Token": "<Talk service-user token>",
    "QueueHost": "<queue host>",
    "QueuePort": 61616,
    "QueueUsername": "<queue user>",
    "QueuePassword": "<queue password>",
    "QueueReceive": "<receive queue>",
    "QueueSend": "<send queue>",
    "ServerName": "<chat server name>",
    "FileServer": "https://<file server>",
    "PodspaceHost": "https://<podspace host>",
    "PlatformHost": "https://<platform host>",
    "SsoHost": "https://<SSO host>",
    "TimeoutSeconds": 15
  }
}
```

For example, set `Talk__Enabled`, `Talk__ThreadId`, `Talk__Token`, and the other
`Talk__...` environment variables, or use the API project's user-secrets store:

```powershell
dotnet user-secrets set 'Talk:Token' '<token>' --project src/Maf.Handoff.Api
dotnet user-secrets set 'Talk:QueuePassword' '<password>' --project src/Maf.Handoff.Api
```

Messages contain a run ID, step and status. `IncludeDetails` opts into sending
clarification questions and failure descriptions, truncated to 2,000 characters;
these can contain internal task information. No patch, conversation transcript or
repository credentials are deliberately included. `NotifyAgentSteps: false` keeps
only workflow outcomes and human input requests.

## Delivery behavior

The sender connects lazily, checks user authentication, correlates the SDK's sent
acknowledgement by message ID, and removes callback subscriptions after each attempt.
The SDK owns one process-wide connection/account; all repository runs use the same
configured destination thread. Restart the API after changing Talk settings. This
implementation uses the SDK's queue transport, not its WebSocket transport.

A bounded in-memory queue holds 256 notifications. Workflow execution does not wait
for Talk. A full queue drops new messages with a warning; delivery failures also log
a warning and allow later notifications to proceed. Messages are attempted once;
timeouts have uncertain delivery, so the sender does not retry automatically.
Normal shutdown attempts to drain the queue within the host shutdown timeout.
Pending notifications are lost on process failure. This is a best-effort notifier,
not a durable outbox or exactly-once delivery system.

The legacy SDK has no public disconnect/dispose API and its synchronous connection
setup cannot be interrupted by a cancellation token. SDK transport timeouts still
apply. Integration tests use a fake Talk sender; live connectivity and delivery need
the company's Talk service settings. No live messages are sent by the tests.

SDK packages and rebuild instructions are in [vendor/talk](../vendor/talk/README.md).
