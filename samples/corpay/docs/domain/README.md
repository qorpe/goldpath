# Domain memory

What this application's business has decided, in a form a reviewer can approve and a tool can
check. The code says how; this folder says what must be true, and why.

It starts empty. It fills when a change carries a business rule: the rule is written down
here first, approved, and then implemented (the Goldpath authoring flow).

## Layout

```
docs/domain/
└── <context>/                    one folder per bounded context
    ├── terms/<term>.yaml          a glossary entry (specanchor ledger-term)
    └── rules/RULE-nnnn.yaml       one business rule per file (specanchor rule)

specs/proposals/SPEC-nnnn/         one proposed change
    ├── spec.yaml                  its acceptance criteria (specanchor spec)
    ├── decisions.yaml             what it decides about the manifest, and on what source
    └── manifest.proposed.yaml     the whole manifest after the change, if it changes
```

The formats are specanchor's v1 artefact schemas, which Goldpath pins and ships inside its
CLI, plus Goldpath's `decisions` schema. There is no other spec format.

## The rules every card follows

- **Ids come from the tool, never from a person or a model:** `goldpath spec ready --next-id rule`
  (or `spec`). An id is never reused.
- **Every rule has a source:** the lines of a committed request (`docs/requests/`) or a scenario
  card it came from. A rule without one is an invention.
- **What the source does not settle is a question,** in the rule's `open_questions` or as an
  `[OPEN — …]` bracket in the spec. It is never folded into the statement.
- **`confidence: inferred`** until a test pins the rule; then `evidenced`, naming that test.
- **A spec is approved** when `approved_by` is set by someone other than the change's author, no
  `[OPEN …]` bracket is left, and the spec's merge request is merged.

`goldpath spec ready` checks all of it: every card's shape (through `specdrift validate`), every
proposed manifest against this app's own rules, ids, sources and open brackets, and with
`--gate SPEC-nnnn --author <who>` the approval itself. It ships with the Goldpath CLI from the
first release that carries it; see the Goldpath CHANGELOG.
