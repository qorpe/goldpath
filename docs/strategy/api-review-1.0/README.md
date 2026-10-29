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
- a missing or extra file, or a wrong header.
