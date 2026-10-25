# Threat model — every surface with an auth boundary (GA gate G15, Part 2/2)

Status: **IN PROGRESS.** The first four surfaces are below: the admin routes, the served
console, Campaign R2.8 signed callbacks, and Bulk upload with its gate. The other six are the
checklist at the end ([#340](https://github.com/qorpe/goldpath/issues/340) stays open until
they land). Written 2026-10-25 against `main` c5bf84a.

## How to read this

Each surface is a STRIDE table. Every threat names what holds it, and that holder is one of
three things:

- **a test** (the class and method, runnable as named) or **an analyzer** rule;
- **an accepted risk**: listed again in [the accepted-risk list](#accepted-risks-awaiting-the-owners-signature),
  which the owner signs on the pull request. That signature is the gate's last step;
- **an issue**: a threat with no holder today. The issue says what would hold it.

To check a threat, run its test. For example:
`dotnet test tests/Goldpath.Campaign.Tests --filter "FullyQualifiedName~R2CallbackTests.TheTimestampWindowIsExactlyTheTolerance_OnTheHostsClock"`.
The console's tests run with `pnpm --dir ui/console exec vitest run -t "<test name>"`.

STRIDE: **S**poofing, **T**ampering, **R**epudiation, **I**nformation disclosure, **D**enial of
service, **E**levation of privilege.

## 1. Admin routes (`/goldpath/admin/*`, all seven surfaces)

**Boundary:** an HTTP caller against the management head.
- **The floor:** every admin mapper calls `AdminSurfaceGuard.Apply`, which puts
  `GoldpathPolicies.Ops` (role `goldpath-ops`) on the whole group unless `exposeUnsecured: true`.
- **Tenant scope:** on a multi-tenant app, admin-contract R1 scopes every read and verb to the
  ambient tenant, and widening it demands `goldpath-ops-all-tenants`.
- **Assets:** the operational state of every module, and the verbs that change it.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-ADM-01 | S / E | An unauthenticated caller reaches an admin route | The group demands the ops policy by default; the opt-out is explicit and logs a warning | `Goldpath.Campaign.Tests.AdminAuthFloorTests.The_admin_surface_demands_the_ops_policy_by_default`; `Goldpath.Sdk.Tests.AdminSeamTests.The_secured_path_stamps_the_ops_policy_on_the_group` |
| T-ADM-02 | E | An authenticated user without the ops role acts on a surface | The policy requires the role. With `Strategy = None` the policies are still registered, so the refusal is a clean `403`, not a `500` and not a pass | `Goldpath.Auth.Tests.AuthContractTests.The_ops_policies_demand_their_default_roles`; `Goldpath.Auth.Tests.AuthEdgeTests.Strategy_none_stays_open_but_the_ops_floor_refuses_cleanly` |
| T-ADM-03 | E / I | An operator of tenant A reads or changes tenant B (multi-tenant app) | R1: the ambient tenant is the scope. A foreign `?tenant=` without `goldpath-ops-all-tenants` is `403`, and no ambient tenant is `400`, never "all" | `Goldpath.Sdk.Tests.AdminTenantScopeTests.Asking_for_a_foreign_tenant_without_the_policy_is_a_403_naming_it`, `…No_ambient_tenant_on_a_multitenant_app_is_a_400_not_a_silent_all`; `Goldpath.Bulk.Tests.AdminServiceTests.The_error_report_is_tenant_scoped_and_fails_closed` |
| T-ADM-04 | E | A surface whose rows carry no tenant (campaign; archival holds and erasures) leaks across tenants | R1 rules 5–6: the surface demands the all-tenants privilege outright on a multi-tenant app | `Goldpath.Sdk.Tests.AdminTenantScopeTests.A_surface_with_no_tenant_rows_demands_the_privilege_outright`; `Goldpath.Bulk.Tests.AdminTenantScopeTests.A_marked_multi_tenant_app_still_gates_the_tenant_less_surfaces` |
| T-ADM-05 | E | A new admin endpoint takes a `tenant` parameter and forgets the scope seam | Analyzer GP0904 | `Goldpath.Analyzers.Tests.Batch3AnalyzerTests.GOLDPATH0904_flags_a_map_lambda_taking_tenant_without_the_seam` |
| T-ADM-06 | T / E | A runtime verb brings a job into existence (drift from the manifest, ADR-0001) | No such route exists, and its absence is asserted | `Goldpath.Jobs.Tests.AdminRouteInventoryTests.NOTHING_here_can_bring_a_job_into_existence_or_remove_one` |
| T-ADM-07 | T / E | Cross-site request forgery: a page on another site makes an operator's browser `POST` a body-less verb (`pause`, `pause-all`, `rerun`) | Goldpath's own strategies send no cookie (bearer, API key), so there is nothing for the browser to attach. Where the floor is a gateway's session cookie, only that cookie's `SameSite` stands between a form and the verb | **Issue [#452](https://github.com/qorpe/goldpath/issues/452)** |
| T-ADM-08 | R | An operator denies a verb they performed | The actor comes from the authenticated identity, never from the client. Every mutating verb writes an audit row server-side (contract *Conventions*) | `Goldpath.Campaign.Tests.AdminServiceTests.ThrottlePatchesTheLiveRowAndAuditsOldToNew`, `…CreateAuditDetailIsAnExactPolicyDescription` |
| T-ADM-09 | R | With `exposeUnsecured: true`, every actor is `anonymous` | The opt-out is a visible decision with a startup warning; it is meant only behind an authenticating boundary | `Goldpath.Sdk.Tests.AdminSeamTests.The_optout_leaves_the_group_open_and_that_is_the_point_of_the_warning`. The residual risk is **accepted risk A1** |
| T-ADM-10 | D | Unbounded list reads | Every list clamps `take` to `[1, 500]`; large ordered sets page by keyset | `Goldpath.Sdk.Tests.AdminSeamTests.The_paging_clamp_answers_one_row_at_least_and_the_ceiling_at_most` |
| T-ADM-11 | I | A classified field leaves through an admin read | Masking at the boundary (DataProtection), and again on render in the console | `Goldpath.DataProtection.Tests.AuditMaskingTests.Classified_properties_are_masked_in_change_rows_and_the_rest_stay_full`; `Goldpath.Notification.Tests.AdminServiceTests.Recipients_are_masked_on_every_surface` |
| T-ADM-12 | E | A viewer who should only watch can also act (one policy for read and act) | Admin-contract R4.1 proposes a read role ([#304](https://github.com/qorpe/goldpath/issues/304), [#297](https://github.com/qorpe/goldpath/issues/297)) | **Issue [#297](https://github.com/qorpe/goldpath/issues/297)** (owner-approved, not built) |

## 2. The served console (`MapGoldpathConsole`, `/goldpath/console`)

**Boundary:** the operator's browser.
- **Where it runs:** the console is a static bundle embedded in `Goldpath.Console`, served by the
  app it drives, behind the same ops floor as the admin surfaces.
- **Its calls:** it calls the admin routes with the operator's own credentials, and it holds no
  token of its own (console RFC §3, case 3).
- **Assets:** the operator's session, the registry of services (`console.config.json`), and the
  links it offers.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-CON-01 | S / I | An unauthenticated browser loads the console or its registry | The console's group goes through the same `AdminSurfaceGuard`; the page itself answers `401` on an auth-floored host | `Goldpath.Console.Tests.MapGoldpathConsoleTests.The_console_sits_behind_the_SAME_ops_floor_as_the_admin_surfaces`; `Goldpath.Console.Tests.ConsoleLinkTests.Link_addresses_sit_behind_the_ops_floor`; console smoke `e2e/served.spec.ts` "the console served by an AUTH-FLOORED app refuses its own page" (`401`, real app, `scripts/console-smoke.sh`) |
| T-CON-02 | T / S | A configured product link points the operator somewhere hostile (`javascript:`, `file:`, a protocol-relative host) | Only a root-relative path or an absolute `http(s)` address is a link. Any other address is refused at startup, and the refusal never echoes the address | `Goldpath.Console.Tests.ConsoleLinkTests.Any_other_address_is_refused_at_the_line_that_wrote_it`, `…A_refusal_never_echoes_the_address_because_it_may_carry_a_credential_into_a_log`, `…A_link_that_bypassed_AddLink_is_still_refused_at_MapGoldpathConsole`; console `registry.test.ts` "judges the RESOLVED address: a root-relative path on a console opened from file: is not http(s)" |
| T-CON-03 | I | An asset path walks out of the bundle (`../appsettings.json`) | Assets come from an `EmbeddedFileProvider`, which can only return resources compiled into the assembly. No test pins it | **Issue [#454](https://github.com/qorpe/goldpath/issues/454)** |
| T-CON-04 | T | Clickjacking: another site frames the console and tricks an operator into a verb | Nothing yet: the page sends no `X-Frame-Options` or CSP `frame-ancestors` (ZAP 10020, 10038). Verbs need a second click in a confirm dialog, which makes a framed attack harder but does not stop one | **open-threads [T43](../strategy/open-threads.md)** (the three missing security headers, carried as INFO in `scripts/zap-baseline.conf`) |
| T-CON-05 | T / I | Script injection through server data rendered in the console (a job name, a failure message, a file name) | React escapes text content. The console renders no server string as HTML. Recipients arrive masked and are never unmasked | console `NotificationPanel.test.tsx` "the row shows the MASKED recipient and nothing more — the console never has the address"; CodeQL `security-extended` on the TypeScript (`.github/workflows/codeql.yml`) |
| T-CON-06 | S | A crafted link opens the console with `?base=https://attacker`, so the operator sees the attacker's data and the attacker's verbs | `?base=` is a one-service developer override. It carries no product links (`registry.test.ts` "§5: ?base= is a one-service override and carries NO links"), and the browser sends the attacker's origin only the attacker's own cookies, never the app's. What remains is a convincing fake screen | **Accepted risk A2** |
| T-CON-07 | I | A refused or unreachable surface is shown as "no module", hiding blindness during an incident | Forbidden, absent and unreachable are three different sentences on screen | console `adminClient.test.ts` "tells 'forbidden' apart from 'absent' — the panel exists, this operator may not see it", "a service that never ANSWERS is unreachable, not module-less" |
| T-CON-08 | R | — | The console holds no state the API does not own, and every verb is audited server-side (T-ADM-08) | as T-ADM-08 |

## 3. Campaign R2.8 signed callbacks (`MapGoldpathCampaignCallbacks`)

**Boundary:** a target system calling back with the verdict on an accepted item.
- **Authentication is the signature:** HMAC-SHA256 over `{timestamp}\n{METHOD}\n{path}\n` plus
  the raw body, keyed by the campaign type's secret.
- **Headers:** `X-Goldpath-Timestamp` and `X-Goldpath-Signature: v1=<hex>`.
- **Window:** the timestamp is accepted within `CallbackTimestampTolerance`, 5 minutes by default.
- **Assets:** the item's terminal state, and with it the campaign's success and failure counts.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-CBK-01 | S | Anyone forges a verdict | A type without a secret has no callback surface at all. An unsigned or wrongly signed call is `401` and changes nothing | `Goldpath.Campaign.Tests.R2CallbackTests.ATypeWithoutASecretHasNoSurface`, `…AnUnauthenticCallbackIs401_AndChangesNothing`, `…MappingCallbacksWithNoSigningType_RefusesToStart` |
| T-CBK-02 | S | A weak key is brute-forced | A secret shorter than 32 bytes (`MinimumSecretBytes`, the HMAC-SHA256 key width) is refused, and so is a rotation to the same secret | `…AShortSecretOrASameSecretRotationIsRefused` |
| T-CBK-03 | S / I | A timing side channel reveals the signature or which secret matched | `CryptographicOperations.FixedTimeEquals` per secret, with no early exit across the secrets of a rotation | `…DuringARotationBothSecretsVerify`, `…TheSignatureRecipeIsPinned` (the recipe); constant time is held by code review of `GoldpathCampaignCallbackEndpoints.Verify`, with no timing test. **Accepted risk A3** |
| T-CBK-04 | T | A captured callback is replayed later | Outside the timestamp window it is refused. Inside it, settling is idempotent: a signed repeat changes nothing | `…TheTimestampWindowIsExactlyTheTolerance_OnTheHostsClock`, `…ASignedCallbackSettles_AndASignedRepeatIsIdempotent` |
| T-CBK-05 | T | A valid signature for one item is reused for another item or campaign | The signed path names the campaign and the correlation id; a callback settles only the campaign it names | `…ACallbackSettlesOnlyTheCampaignItNames`, `…TheQueryStringIsNotPartOfTheSignedPath` |
| T-CBK-06 | T | A downgrade to a weaker or future signature scheme | Only `v1=` with exactly 64 hex characters verifies | `…OnlyTheV1SchemeVerifies` |
| T-CBK-07 | D | Oversized or garbage bodies exhaust the host | The body is read up to 16 KiB; larger is `413`. A signed body that is not a callback is `400`. Early callbacks park and expire | `…ABodyOverTheLimitIs413_AndOneAtTheLimitIsRead`, `…ASignedBodyThatIsNotACallbackIs400`, `…AParkedCallbackNobodyClaims_Expires` |
| T-CBK-08 | R | A target system denies sending a verdict, or a flood of refusals goes unseen | Every answer says what happened, and refused callbacks are counted in a metric | `…EveryAnswerSaysWhatHappened`, `…ARefusedCallbackIsCounted` |
| T-CBK-09 | E | An adopter's extra authorization replaces the signature | An optional policy only adds to the signature, never replaces it | `…TheSignatureAuthenticates_AndAPolicyIsOnlyAnExplicitOptIn` |
| T-CBK-10 | S | The host's clock is wrong, so the window moves | The window is measured on the host's clock by design. An NTP failure on the host widens or shuts the door | **Accepted risk A4** |

## 4. Bulk upload and its gate (`/goldpath/admin/bulk`)

**Boundary:** an operator uploading a file, and an operator deciding the batch.
- **Where the file goes:** into the app's database as SHA-256-stamped chunks, then it is
  validated row by row.
- **What runs it:** only a batch in `Validated` that someone approves (or a definition opted into
  `AutoApprove`) runs as a Jobs run.
- **Assets:** the rows that will execute (payments-shaped work), and the evidence of who decided.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-BLK-01 | E | Someone without the ops role uploads or decides | The admin floor (T-ADM-01/02) and tenant scope (T-ADM-03) apply to every bulk route | as T-ADM-01..03; `Goldpath.Bulk.Tests.AdminServiceTests.Batch_queries_filter_by_state_and_fail_closed_on_tenant` |
| T-BLK-02 | T | A file with invalid rows is executed | The gate refuses approval while invalid rows exist, unless the definition explicitly tolerates them (and then the report is the evidence) | `Goldpath.Bulk.Tests.EngineTests.The_gate_blocks_invalid_rows_by_default_and_a_rejection_needs_a_reason`, `…Tolerating_invalid_rows_executes_the_valid_subset_with_the_report_as_evidence` |
| T-BLK-03 | T | The same file is submitted twice and executes twice | The content hash is the idempotence key: identical bytes return the existing batch. Dedup is tenant-scoped, and an executed row replays as evidence, not as a second execution | `Goldpath.Bulk.Tests.EngineTests.Identical_bytes_return_the_same_batch_but_a_rejected_file_may_be_resubmitted`, `…Dedup_is_tenant_scoped`; `Goldpath.Bulk.Tests.EngineEdgeTests.Replaying_an_already_executed_row_is_idempotent_evidence` |
| T-BLK-04 | E / R | The uploader approves their own batch (no four-eyes) | Bulk RFC D2 keeps the four-eyes POLICY in the app, and the module records `DecidedBy`. But the batch does not record who uploaded it, so an app cannot enforce or detect self-approval from the batch | **Issue [#453](https://github.com/qorpe/goldpath/issues/453)** |
| T-BLK-05 | R | A rejection with no reason, so the next uploader learns nothing and the decision is unexplained | A rejection needs a note. Decider, time and note are stored on the batch | `Goldpath.Bulk.Tests.EngineTests.The_gate_blocks_invalid_rows_by_default_and_a_rejection_needs_a_reason` |
| T-BLK-06 | I | Tenant A reads tenant B's batch or its validation report | The report scopes through its batch's tenant; a foreign batch id answers like an absent one | `Goldpath.Bulk.Tests.AdminServiceTests.The_error_report_is_tenant_scoped_and_fails_closed` |
| T-BLK-07 | D | A huge file exhausts memory or the database | `MaxRows` is mandatory per definition, and exactly `MaxRows` is within the ceiling. Bytes are stored in chunks, never whole in memory. The request body is bounded only by the host's limit (Kestrel's default `MaxRequestBodySize`, 30 MB); the module sets no byte ceiling of its own | `Goldpath.Bulk.Tests.OptionsTests.MaxRows_is_mandatory_the_ceiling_is_a_decision`, `Goldpath.Bulk.Tests.ValidationFlowTests.A_file_of_exactly_MaxRows_rows_is_within_the_ceiling`, `Goldpath.Bulk.Tests.FileStoreTests.A_trickling_upload_still_chunks_and_hashes_exactly`. The byte bound is **accepted risk A5** |
| T-BLK-08 | T | A crafted CSV breaks the parser (quotes, delimiters) and shifts fields into the wrong columns | The CSV format honours quoting and escaped quotes; the delimiter is configuration | `Goldpath.Bulk.Tests.CsvFormatTests.Quoted_fields_carry_delimiters_and_escaped_quotes`, `…Semicolon_delimiter_is_a_configuration` |

## Accepted risks awaiting the owner's signature

These have no test because the answer is a decision, not code. The owner signs this list on the
pull request that adds it. Until then, each one is an open G15 item.

| # | Risk | Why it is accepted (proposed) | Revisit when |
|---|---|---|---|
| A1 | `exposeUnsecured: true` makes every admin actor `anonymous` and opens the surface (T-ADM-09) | It is an explicit, warning-logged opt-out for hosts that sit behind an authenticating boundary. Refusing it would push those adopters to a worse workaround | an adopter's deployment is found using it without such a boundary |
| A2 | `?base=` lets a crafted link show an attacker's service inside the real console's frame (T-CON-06) | It is the documented developer override. No credential of the app reaches the other origin. Removing it costs the dev loop and the "service that did not serve this console" path | an operator is phished through it, or the console starts holding a token (console RFC §3, case 3), which would make it a credential leak |
| A3 | The callback signature's constant-time comparison has no timing test (T-CBK-03) | `CryptographicOperations.FixedTimeEquals` is the framework's primitive (ADR-0003). A timing test would be flaky by nature. The no-early-exit loop is visible in a ten-line method | the verification code changes shape |
| A4 | The callback window trusts the host's clock (T-CBK-10) | Every HMAC-with-timestamp scheme does this. A drifting clock is an operations alarm, not a protocol flaw | an adopter runs callbacks across hosts without time sync |
| A5 | Bulk upload size is bounded by the host's request-body limit, not by the module (T-BLK-07) | Kestrel's default (30 MB) applies unless an adopter raises it deliberately. `MaxRows` bounds the work. A module-level byte ceiling would duplicate a host setting | an adopter needs larger files (then the module should take its own ceiling) |

## Still to model (Part 2/2, remaining checklist)

- [ ] Approvals decisions (distinct eyes, self-approval, the rung's role)
- [ ] FileExchange ingress (path, format, quarantine)
- [ ] Notification channels (credentials, template injection)
- [ ] DataProtection keys
- [ ] Idempotency key scope
- [ ] MultiTenancy isolation
