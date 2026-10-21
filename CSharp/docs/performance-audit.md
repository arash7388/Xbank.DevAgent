# Agent run performance audit

The local `llm-checkpoints.db` was inspected read-only on 2026-10-21. These are
historical measurements, not a controlled comparison of models. Some runs are
cancelled, failed, or still marked running. AI durations include the call's streaming
lifecycle; they are not isolated measurements of server inference time. Tool durations
exclude the audit writes before and after execution.

## Recorded timings

| Tool | Calls | Average | Maximum |
| --- | ---: | ---: | ---: |
| `search_repository` | 1,764 | 1,498 ms | 9,201 ms |
| `list_repository_files` | 300 | 380 ms | 3,033 ms |
| `get_repository_diff` | 146 | 756 ms | 1,950 ms |
| `get_repository_status` | 168 | 408 ms | 1,657 ms |
| `read_repository_file` | 1,623 | 3.5 ms | 334 ms |
| CodeGraph `get_context` | 147 | 76 ms | 1,512 ms |

Jira clarification calls include user wait time: the longest lasted 2.8 hours.
Treat these separately from tool processing time.

| Configured model | Agent | Completed calls | Average AI call | Average input tokens |
| --- | --- | ---: | ---: | ---: |
| `developer-model` | Developer | 732 | 33.0 s | 81,824 |
| `developer-model` | Planner | 75 | 60.7 s | 44,463 |
| `developer-model` | Reviewer | 86 | 64.3 s | 92,469 |
| `deepseek-flash` | Developer | 266 | 7.6 s | 58,563 |
| `deepseek-flash` | Planner | 28 | 8.3 s | 23,377 |
| `deepseek-flash` | Reviewer | 38 | 9.2 s | 58,814 |

For run `9ced8b05-af04-4b40-a10d-5303f2611aaa`, 53 AI calls totalled 45.6 minutes;
tool execution totalled 2.7 minutes. Another run,
`523e631a-275c-4a12-8247-36e8a3d002db`, recorded 6.1 minutes in AI calls and
1.1 minutes in tools against 39 minutes between its recorded start and finish.
Differences can include user decisions, builds, persistence, workflow processing,
and scheduling; these totals alone cannot assign the remaining time to a single cause.

## Changes

- Usage summaries read compact `metrics_json` rather than repeatedly deserializing
  all prior request and response bodies. Full audit records remain available.
  Existing databases receive an additive column; legacy rows fall back to their full
  records. No bulk rewrite of historical data is required.
- Streaming text is coalesced over 250 ms or 4,096 characters. The first chunk is
  immediate; buffered text flushes before another agent, non-text event, end of stream,
  or stream failure. This reduces synchronous SQLite event writes while preserving text
  and ordering. The interval is checked when chunks arrive; it does not add a sleep
  or force a stalled provider to produce another chunk.
- Repository search reuses the validated full path from file enumeration instead of
  repeating filesystem and symlink checks for each file.
- Local Git commands have a 30-second limit; clone, pull, push, and remote branch
  listing have a five-minute limit. Cancellation or timeout kills the process tree
  and drains redirected output before returning.

## Local benchmark

A synthetic run with 30 prior requests of 1 MB each took **7,496 ms** for five call
start/finish pairs using full audit payloads, versus **1,041 ms** with compact metadata
(about 7.2 times faster for this operation). This is not an end-to-end speedup claim.

Persisting 1,000 individual synthetic text events took **41,032 ms**, demonstrating
that per-chunk persistence can be material even when the tool execution timer is low.
With coalescing, the same 1,000 burst chunks plus completion took **112 ms** and
four database writes, retaining all 5,000 text characters. This burst benchmark
does not represent a provider's naturally spaced streaming output.

## Remaining contributors

The subsequent tool-cache change shares successful read results between agents in the same run.
It keeps compact/full file representations separate, checks content or metadata freshness, expires
Jira reads after 30 seconds, and limits retained results to 128 entries / 8 MiB of serialized payloads.
Actual tool executions and cache hits are audited separately. See the README for the cache policy.

In a synthetic repository containing 500 source files of approximately 20 KB each, three repeated
full scans took **5,147 ms**. Three cached searches, including the Git/metadata freshness checks,
took **1,029 ms** (about five times faster for these repeated searches). This benchmark omitted
tool audit storage and does not establish an end-to-end workflow speedup.

Long AI calls and many sequential rounds remain major costs. The recorded model
comparisons involve different tasks and settings and do not establish equal quality.
Changing models or reasoning effort requires evaluating completion quality as well
as time. Large request histories (tens of thousands of tokens on average above) also
deserve attention when evaluating context policies.

Build verification already has a five-minute limit per target. HTTP tools have
service-specific timeouts. Model HTTP requests have a 120-second transport timeout;
the full streaming lifecycle is a separate concern. Explicit user-decision waits
should retain their interactive behavior rather than being failed as slow tools.

## Validation

77 targeted tests passed across AI accounting, tool auditing, text coalescing,
workflow execution, repository operations, and repository search scenarios.
Coverage includes migration of existing databases, retaining full audit payloads,
flushing text on stream failures, and terminating Git subprocesses on cancellation.
An initial run hit a temporary repository setup timeout; the same scenario passed
in the final run. No live provider run was started and the historical database was
not migrated or rewritten during this audit.

After adding tool-result caching, 126 targeted tests passed, covering cross-agent sharing,
representation separation, source changes, expiry, concurrent reads, cancellation, failures,
capacity limits, persistent cache-hit records, Jira clarification, and workflow termination.
