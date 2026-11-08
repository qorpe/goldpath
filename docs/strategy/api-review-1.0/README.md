# The 1.0 API review — verdict tables

One TSV per package that has a public-API ledger (`packages/<Package>/PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`). These are the data of the 1.0 API review. The narrative, the method and
the policies are in [`docs/rfc/goldpath-1.0-api-review.md`](../../rfc/goldpath-1.0-api-review.md),
and the owner signs the whole review once, in API-13 (#402). These files are Tier A (owner decision
2026-10-24 on #390), so the review parts #391–#396 merge after the verifier.

## Format

The first line is the header, tab-separated:

```
line	verdict	target	reason
```

Each following line is one verdict for one ledger line:

| Column | Holds |
|---|---|
| `line` | The ledger line exactly as the ledger writes it. One leading `[ID]` prefix is ignored on both sides, because PublicApiAnalyzers 5.x may prefix an experimental member |
| `verdict` | `keep`, `rename`, `remove` or `experimental` |
| `target` | For `rename`, the full new ledger line. For `experimental`, the diagnostic id (`GPEXP001` for Approvals, `GPEXP002` for FileExchange). Empty otherwise |
| `reason` | Mandatory for every verdict but `keep`. For `remove`, it also says what replaces the line, or why no adopter needs it |

A row is a promise about the line, not a change to it. Renames and removals are applied only by
API-14 (#403), in one train, with no compatibility shims.

## The counter

`scripts/api-review-gate.sh` is the only source of every number the record quotes:

```bash
./scripts/api-review-gate.sh                          # covered/total and verdict counts per package; fails on a malformed table
./scripts/api-review-gate.sh --require-complete       # also fails on any live ledger line without a row
./scripts/api-review-gate.sh --require-complete --package Goldpath.Jobs   # one package (a review part's own bar)
./scripts/api-review-gate.sh --list-non-keep          # the owner's reading list for API-13
./scripts/api-review-gate.sh --require-applied        # after API-14: every verdict is applied
./scripts/api-review-gate.sh --selftest               # each fault planted in a scratch copy goes red
```

A table fails the gate on:
- a stale row: a `keep` or `experimental` whose line is no longer live, or a `rename` whose source and
  target are both not live (a `remove` whose line has gone, or a `rename` whose target is live, is an
  applied verdict, not a stale one);
- an unknown verdict;
- a non-keep row without a reason;
- a `rename` or `experimental` row without a target;
- a duplicate row;
- a `keep` in an experimental package (Approvals and FileExchange: GA readiness Q4);
- an `experimental` in Approvals or FileExchange naming another id than its package's (`GPEXP001` /
  `GPEXP002`, compared exactly), or an `experimental` in any other package naming one of those two ids;
- a missing or extra file, or a wrong header.

## Findings for API-13

What a review part found that binds more than its own table. The owner reads these beside
`--list-non-keep` and the record's P1–P3 when signing (API-13, #402). A part adds a finding
here and never answers another part's lines.

- **API-2 (#391), `GoldpathAdminResult` placement (P2).** Answered as P2 proposes: the 14 Jobs lines are
  `remove` rows whose reason names the move to `Goldpath.Abstractions` as the same line, same name and namespace.
  A same-text `rename` cannot express a move between ledgers: the gate reads a rename whose target is live as
  already applied. API-4 (#393) adds the lines to the Abstractions table, and API-14 (#403) moves the type.
- **API-2 (#391), the `ct` parameter name (method Q3), proposed as a policy.** Jobs' admin service names its
  token `ct` on 18 methods, while every other public Jobs signature says `cancellationToken`. The Jobs rows rename
  them. The same name is on 13 Archival, 10 Campaign, 7 Bulk and 5 Notification live lines, Shipped and Unshipped
  (measured at 9513a76, unchanged at fd5d37f).
  **Proposed:** every part renames them the same way. The rename is binary-compatible, and it breaks only a caller
  that passes the token by name.
- **API-2 (#391), a job cannot be unit-tested from outside.** `GoldpathJobContext` and `GoldpathJobChunk` have no
  public constructor, so an adopter's test of `IGoldpathJob.ExecuteChunkAsync` cannot build either. Goldpath's own
  Campaign tests use reflection. The runner path (`GoldpathJobRunner<TContext>`, kept public for this) runs a whole
  job, not one chunk. A test factory would be new API, out of scope for a review part. It is listed here for the owner.

