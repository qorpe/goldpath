# Threat model — every surface with an auth boundary (GA gate G15, Part 2/2)

Status: **IN PROGRESS, 6 of 10 surfaces.** Below are:
- the admin routes, the served console, Campaign R2.8 signed callbacks, and Bulk upload with
  its gate (written 2026-10-25 against `main` c5bf84a, re-checked against `main` aff2d3e the
  same day);
- Approvals decisions and FileExchange ingress (written 2026-10-29 against `main` 92ad7a0).

The other four are the checklist at the end; [#340](https://github.com/qorpe/goldpath/issues/340)
stays open until they land.

## How to read this

Each surface is a STRIDE table. Every threat names what holds it, and that holder is one of
three things:

- **a test** (the class and method, runnable as named) or **an analyzer** rule;
- **an accepted risk**: listed again in [the signed list](#accepted-risks-signed-by-the-owner-2026-10-26) or [the list awaiting signature](#accepted-risks-awaiting-the-owners-signature),
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
| T-ADM-08 | R | An operator denies a verb they performed | The actor comes from the authenticated identity, never from the client, and every mutating verb but one records it server-side on its own surface's evidence: Jobs and Campaign write an admin audit row; Archival stamps the hold (`PlacedBy`, `LiftedBy`) and the erasure record (`RequestedBy`); the Bulk gate stamps `DecidedBy`; Approvals records the signer of each decision. Notification and FileExchange have no mutating verb, and archival `verify` writes nothing. **The exception is Bulk upload**: the batch records no uploader (T-BLK-04, [#453](https://github.com/qorpe/goldpath/issues/453)) | `Goldpath.Jobs.Tests.SchedulingSurfaceTests.A_second_trigger_can_be_added_to_a_DECLARED_job_and_removed_again` (asserts each row's actor), `Goldpath.IntegrationTests.JobsClusterTests.Management_head_verbs_the_cluster_through_the_admin_service_and_never_executes`; `Goldpath.Campaign.Tests.AdminServiceTests.ThrottlePatchesTheLiveRowAndAuditsOldToNew`, `…CreateAuditDetailIsAnExactPolicyDescription`; `Goldpath.Archival.Tests.AdminServiceTests.Hold_lifecycle_is_evidence_and_blocks_erasure_and_purge`, `…Erasure_redacts_classified_fields_restamps_content_and_keeps_the_chain_verifiable`; `Goldpath.Bulk.Tests.AdminServiceTests.Gate_verbs_delegate_to_the_engine_with_the_actor`; `Goldpath.Approvals.Tests.ApprovalAdminServiceTests.The_detail_carries_the_trail_and_the_signatures`; bulk upload: **issue [#453](https://github.com/qorpe/goldpath/issues/453)** |
| T-ADM-09 | R | With `exposeUnsecured: true`, every actor is `anonymous` | The opt-out is a visible decision with a startup warning; it is meant only behind an authenticating boundary | `Goldpath.Sdk.Tests.AdminSeamTests.The_optout_leaves_the_group_open_and_that_is_the_point_of_the_warning`. The residual risk is **accepted risk A1** |
| T-ADM-10 | D | Unbounded list reads | Every list clamps `take` to `[1, 500]`; large ordered sets page by keyset | `Goldpath.Sdk.Tests.AdminSeamTests.The_paging_clamp_answers_one_row_at_least_and_the_ceiling_at_most` |
| T-ADM-11 | I | A classified field leaves through an admin read (every read but archival retrieval: admin-contract R4.1 grants that read in full to the viewer role `goldpath-ops-read` as well as `goldpath-ops`, by owner decision 2026-10-25; T-ADM-13) | Masking at the boundary (DataProtection), and again on render in the console | `Goldpath.DataProtection.Tests.AuditMaskingTests.Classified_properties_are_masked_in_change_rows_and_the_rest_stay_full`; `Goldpath.Notification.Tests.AdminServiceTests.Recipients_are_masked_on_every_surface` |
| T-ADM-12 | E | A viewer who should only watch can also act (one policy for read and act) | Admin-contract R4.1 proposes a read role ([#304](https://github.com/qorpe/goldpath/issues/304), [#297](https://github.com/qorpe/goldpath/issues/297)) | **Issue [#297](https://github.com/qorpe/goldpath/issues/297)** (owner-approved, not built) |
| T-ADM-13 | I | An archived document's classified fields leave through `GET /archival/entries/{definition}/{key}` | None by masking: retrieval returns the archived document (`GoldpathArchiveEntry.Document`) unredacted, because it is the regulatory record, and erasure is the masking path (archival RFC D4; `GoldpathDocumentRedactor` runs only from erasure). The read is behind the ops floor and R1's tenant scope (T-ADM-01..03). Owner decision 2026-10-25: once admin-contract R4.1 lands ([#451](https://github.com/qorpe/goldpath/pull/451)), the viewer role `goldpath-ops-read` holds this read too; R4.1 and the upgrade guide state it, so an adopter withholds the role where archived content is sensitive | **Accepted risk A6** |

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

## 5. Approvals decisions (`MapGoldpathApprovalsAdmin`, `/goldpath/admin/approvals`, and the engine)

**Boundary:** two callers decide.
- **The app's own code** calls `GoldpathApprovalEngine.DecideAsync(id, decidedBy, deciderRole, …)` and supplies
  both the identity and the role.
- **An operator** decides through `POST /requests/{id}/approve|reject`. There the identity is
  `http.User.Identity?.Name ?? "anonymous"`, and the role is the body's `role` field. That name is not the identifier
  the app's own code gets from `ICurrentUser.UserId` (`NameIdentifier`, then `sub`), and under Goldpath's OIDC
  defaults it is often null (T-APR-12).

**Assets:** a grant, which a downstream system acts on (`GoldpathApprovalGranted`), and the trail that says who
decided under which rung.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-APR-01 | S / E | An unauthenticated caller, or one without the ops role, decides through the admin surface | The admin floor (T-ADM-01/02). Since admin-contract R4.1 the two verbs demand `goldpath-ops`, and the viewer role reads only | as T-ADM-01..02; `Goldpath.IntegrationTests.AdminReadRoleSurfaceTests.A_viewer_reads_every_surface_and_every_verb_is_refused_in_words`, `…Every_admin_route_is_a_read_for_the_viewer_or_a_verb_for_the_operator` |
| T-APR-02 | E | The requester approves their own request (maker-checker) | Four-eyes compares the decider with `RequestedBy`, case-insensitively, on every rung including a quorum rung. This holds when both sides pass the same identifier, which is what the engine tests prove. **On the admin surface it does not hold today:** the decider is the principal's `Identity.Name`, never a body field, but that is not the identifier a request is filed under, so four-eyes compares two identifier spaces (T-APR-12) | `Goldpath.Approvals.Tests.ApprovalEngineTests.Four_eyes_the_requester_may_never_decide_their_own_request`; `Goldpath.Approvals.Tests.ApprovalEngineV2Tests.Four_eyes_bars_the_requester_from_a_quorum_rung_too`; admin surface: **issue [#487](https://github.com/qorpe/goldpath/issues/487)** |
| T-APR-03 | E | A decider without the rung's authority decides it: an "expert" grants the top rung of a credit ladder | The engine refuses a role other than the pending rung's (`WrongRole`). **On the admin surface the role is the body's `role`**, which the caller types, so any ops holder can name any rung | engine: `Goldpath.Approvals.Tests.ApprovalEngineTests.Wrong_role_is_refused_and_right_role_is_applied`; admin surface: **issue [#478](https://github.com/qorpe/goldpath/issues/478)** |
| T-APR-04 | E | One person satisfies a quorum, or a two-step chain, alone | Distinct-eyes reads the whole chain: an identity signs at most once per request, even after escalation (`AlreadySigned`) | `Goldpath.Approvals.Tests.ApprovalEngineV2Tests.The_same_identity_may_not_sign_twice_on_one_rung`, `…Distinct_eyes_holds_across_escalation_a_lower_rung_signer_may_not_sign_the_higher_rung`, `…A_quorum_rung_stays_pending_until_the_required_distinct_grants_arrive` |
| T-APR-05 | E | A delegate decides beyond the delegator's authority | Delegation is bounded: a window no longer than `MaxDelegationWindow`, depth one, no delegation to self, and an expired one admits nobody. **But an active delegation admits its delegate to every rung**, because v0 delegations carry no role (`HasDelegationToRoleAsync`) | bounds: `Goldpath.Approvals.Tests.ApprovalEngineTests.Delegation_admits_the_delegate_and_depth_is_one`, `…Expired_delegation_admits_nobody`; `Goldpath.Approvals.Tests.ApprovalEngineDetailTests.Refusal_messages_name_the_rule_that_refused` (self-delegation, the window, re-delegation); `Goldpath.Approvals.Tests.ApprovalAdminServiceTests.A_delegation_of_exactly_the_maximum_window_is_allowed`; rung scope: **issue [#479](https://github.com/qorpe/goldpath/issues/479)** |
| T-APR-06 | T | Two decisions race: a double click signs twice, or a grant and a reject both apply and both publish | None today. The engine checks, then acts, across separate store calls; the signature table has no unique `(RequestId, SignedBy)`; the request row has no concurrency token | **Issue [#480](https://github.com/qorpe/goldpath/issues/480)** |
| T-APR-07 | T / R | A decided request is decided again, or a rejection is unexplained | A decided request refuses any further decision (`NotPending`), and a rejection needs a reason (`ReasonRequired`). Only the requester may withdraw | `Goldpath.Approvals.Tests.ApprovalEngineTests.A_decided_request_cannot_be_decided_again`; `Goldpath.Approvals.Tests.ApprovalEngineV2Tests.A_rejection_without_a_reason_is_refused`, `…Only_the_requester_may_withdraw` |
| T-APR-08 | R | A decider denies a decision, or the trail is reworded after the fact | Every lifecycle step lands in the trail with the actor and the time, and the trail's wording is pinned. The admin surface records the principal's `Identity.Name`, never "the console". **But that name is null for an OIDC token without a mapped name claim**, so the trail then records `anonymous` on a secured surface (T-APR-12) | `Goldpath.Approvals.Tests.ApprovalEngineTests.The_trail_records_every_lifecycle_step`; `Goldpath.Approvals.Tests.ApprovalEngineDetailTests.The_trail_wording_is_the_audit_surface`; `Goldpath.Approvals.Tests.ApprovalAdminEndpointsTests.Approve_answers_the_frozen_envelope_and_the_engine_applied_it`; an unnamed principal: **issue [#487](https://github.com/qorpe/goldpath/issues/487)** |
| T-APR-09 | E / I | On a multi-tenant app, an operator of tenant A reads and decides tenant B's requests | The approvals store carries no tenant column, so admin-contract R1 rule 5 applies: the surface should demand `goldpath-ops-all-tenants` outright, as campaign's does. **It does not** | **Issue [#481](https://github.com/qorpe/goldpath/issues/481)** |
| T-APR-10 | D | A request waits forever because nobody decides it | Escalation moves an overdue request one rung up. At the top rung it expires rather than loops, and the sweep is a declared job | `Goldpath.Approvals.Tests.ApprovalEngineTests.Escalation_moves_one_rung_on_deadline_and_resets_the_clock`, `…Overdue_at_the_top_rung_expires_not_loops` |
| T-APR-11 | I | A refusal leaks more than the rule | The refusal answers `400` with the rule's name only (`FourEyesViolation`, `WrongRole`, …). Lists clamp `take` | `Goldpath.Approvals.Tests.ApprovalAdminEndpointsTests.A_refusal_is_a_400_whose_message_is_the_rule_name`; `Goldpath.Approvals.Tests.ApprovalAdminServiceTests.Take_is_clamped_and_honored` |
| T-APR-12 | S / E | An operator decides on the admin surface as someone else, or as the requester. The decider's identity is `Identity.Name`, which (a) is null for OIDC tokens without a mapped name claim (JwtBearer's default `NameClaimType` is `ClaimTypes.Name`; a `sub` + `preferred_username` token or a client-credentials token maps none), so every operator decides as `anonymous`, and (b) is not the identifier `ICurrentUser.UserId` returns, so a request filed under the requester's `sub` is approved by that same requester on the admin route | None today. Measured: the requester, with `NameIdentifier` set and with or without a mapped name, approves their own request on the admin route and gets `Applied`, not `FourEyesViolation` | **Issue [#487](https://github.com/qorpe/goldpath/issues/487)** |

## 6. FileExchange ingress (`GoldpathFileRailEngine`, and `/goldpath/admin/fileexchange`)

**Boundary:** a counterparty's file reaches a declared rail.
- **How it arrives:** through a composed transport (SFTP, share, object store) and the app's pick-up job, which
  calls `ProcessAsync(rail, fileName, lines)`. The engine receives lines, never a path: the package opens no file
  and touches no filesystem.
- **The admin surface:** read-only. It has rails, files and the quarantine, and no verb.
- **Assets:** the rows a handler applies (payments-shaped, and once only), and the quarantine an operator acts on.

| # | STRIDE | Threat | Defence | Held by |
|---|---|---|---|---|
| T-FXI-01 | S | A forged file, dropped into the pick-up location, is ingested as the counterparty's | Authenticating the counterparty is the transport's job (SSH keys, bucket policy), composed and never rewritten (ADR-0003). The rail's `ValidateFile` can check a trailer, a count or a signature the counterparty supplies | **accepted risk A7** (awaiting signature) |
| T-FXI-02 | T | A file on an undeclared rail is ingested | Only declared rails ingest; an undeclared name throws | `Goldpath.FileExchange.Tests.FileRailEngineTests.An_undeclared_rail_is_refused` |
| T-FXI-03 | T | A truncated or malformed file is applied in part | `ValidateFile` rejects the whole file: nothing ingests, and `GoldpathFileRejected` says why | `Goldpath.FileExchange.Tests.FileRailEngineTests.Truncated_file_is_rejected_whole_and_ingests_nothing`; `Goldpath.FileExchange.Tests.FileRailContractTests.A_rejected_file_announces_the_rejection_and_nothing_else` |
| T-FXI-04 | T | A re-delivered file applies twice, or a different file under a reused name is silently skipped | The `(rail, file, line)` key dedups a re-delivery, across a restart too. **The key is the file's name, not its content**, so new content under a processed name is skipped as "duplicates", with nothing but a `skipped` count. The name is compared exactly, so on a case-insensitive transport (SMB) `ABC.csv` and `abc.csv` are two files | dedup: `Goldpath.FileExchange.Tests.FileRailEngineTests.Replaying_the_same_file_applies_zero_duplicates`; `Goldpath.FileExchange.Tests.EfFileLedgerTests.Zero_duplicates_survive_a_restart`; name reuse: **issue [#482](https://github.com/qorpe/goldpath/issues/482)** |
| T-FXI-05 | T | A bad row reaches the handler, or stops the batch | Row validation runs before the handler, and a parse failure, contract failure or handler throw quarantines that row with its reason while the batch continues. Reprocessing retries only the quarantined rows | `Goldpath.FileExchange.Tests.FileRailContractTests.Row_validation_wins_over_the_handler_the_handler_never_sees_a_bad_row`, `…A_throwing_handler_quarantines_the_row_with_its_words`; `Goldpath.FileExchange.Tests.FileRailEngineTests.Bad_rows_quarantine_and_the_batch_continues`, `…Reprocess_after_fix_retries_only_the_quarantined_row` |
| T-FXI-06 | T | A path in a file name escapes a directory | Not applicable to the module: it receives the name as a key and the lines as strings, opens nothing, and the admin surface carries file names in the query, never the path. A transport adapter that maps names to paths owns that check | by construction (no `System.IO` file access in `packages/Goldpath.FileExchange`) |
| T-FXI-07 | R | A counterparty disputes what was applied, and when | The ledger keeps every processed `(rail, file, line)`, every quarantined row with its reason and first time, and one archive mark per run. The lifecycle events carry the rail and the file | `Goldpath.FileExchange.Tests.EfFileLedgerTests.The_archive_mark_is_idempotent_one_run_one_row`; `Goldpath.FileExchange.Tests.FileRailEngineTests.Clean_file_ingests_every_row_once` |
| T-FXI-08 | I | The quarantine shows a row's classified values to operators | The surface is behind the ops floor (T-ADM-01..02); since R4.1 a viewer reads it. The reason is the app's own words, from its parser, validator or handler, and is stored and shown as written. Goldpath does not mask free text | `Goldpath.FileExchange.Tests.FileExchangeAdminTests.The_routes_answer_over_real_HTTP_with_the_R3_query_shape`; the content is **accepted risk A8** (awaiting signature) |
| T-FXI-09 | E / I | On a multi-tenant app, an operator of tenant A reads every rail's files and quarantine | The ledger carries no tenant column, so R1 rule 5 applies. **The surface does not demand `goldpath-ops-all-tenants`** | **Issue [#481](https://github.com/qorpe/goldpath/issues/481)** |
| T-FXI-10 | E / T | An operator changes ingestion state from the console | There are no verbs: re-delivering the file is the reprocess, and a `POST` anywhere under the surface is never a `200` | `Goldpath.FileExchange.Tests.FileExchangeAdminTests.The_routes_answer_over_real_HTTP_with_the_R3_query_shape` |
| T-FXI-11 | D | A huge file exhausts memory, or the ledger with one round trip per line | Lines arrive as a list the pick-up job built. The module sets no line or byte ceiling, and makes up to three ledger calls per line (processed?, mark, release the quarantine). Lists on the admin surface clamp `take` | lists: `Goldpath.FileExchange.Tests.FileExchangeAdminTests.Take_is_clamped_and_files_filter_by_rail`; the size: **accepted risk A9** (awaiting signature) |

## Accepted risks (signed by the owner 2026-10-26)

These have no test because the answer is a decision, not code. The owner signed A1–A6 on
2026-10-26 on [#455](https://github.com/qorpe/goldpath/pull/455), the pull request that added
them, as written below. Each stays accepted until its "Revisit when" trigger fires; a new
accepted risk needs its own signature.

| # | Risk | Why it is accepted | Revisit when |
|---|---|---|---|
| A1 | `exposeUnsecured: true` makes every admin actor `anonymous` and opens the surface (T-ADM-09) | It is an explicit, warning-logged opt-out for hosts that sit behind an authenticating boundary. Refusing it would push those adopters to a worse workaround | an adopter's deployment is found using it without such a boundary |
| A2 | `?base=` lets a crafted link show an attacker's service inside the real console's frame (T-CON-06) | It is the documented developer override. No credential of the app reaches the other origin. Removing it costs the dev loop and the "service that did not serve this console" path | an operator is phished through it, or the console starts holding a token (console RFC §3, case 3), which would make it a credential leak |
| A3 | The callback signature's constant-time comparison has no timing test (T-CBK-03) | `CryptographicOperations.FixedTimeEquals` is the framework's primitive (ADR-0003). A timing test would be flaky by nature. The no-early-exit loop is visible in a ten-line method | the verification code changes shape |
| A4 | The callback window trusts the host's clock (T-CBK-10) | Every HMAC-with-timestamp scheme does this. A drifting clock is an operations alarm, not a protocol flaw | an adopter runs callbacks across hosts without time sync |
| A5 | Bulk upload size is bounded by the host's request-body limit, not by the module (T-BLK-07) | Kestrel's default (30 MB) applies unless an adopter raises it deliberately. `MaxRows` bounds the work. A module-level byte ceiling would duplicate a host setting | an adopter needs larger files (then the module should take its own ceiling) |
| A6 | Archival retrieval returns the archived document's classified fields unmasked to any `goldpath-ops` holder in scope, and, under admin-contract R4.1 (owner decision 2026-10-25), to any `goldpath-ops-read` holder in scope (T-ADM-13) | The archive is the regulatory record: an operator retrieving it for a case needs it as stored, and erasure is the path that masks it (archival RFC D4). The read already demands the ops role and the tenant scope | an adopter needs a masked viewer of archived content (open-threads T50 once R4 merges), or the archive read is opened to a principal outside the two ops roles |

## Accepted risks awaiting the owner's signature

Added 2026-10-29 with §5 and §6. They are not accepted until the owner signs them on the pull
request that adds them.

| # | Risk | Why it would be accepted | Revisit when |
|---|---|---|---|
| A7 | A forged file in the pick-up location is ingested as the counterparty's (T-FXI-01) | Authenticating the sender is the transport's job: SSH keys, bucket policy, a share's ACL. The module composes transports and never rewrites them (ADR-0003). A rail that needs content authenticity can verify a signature or trailer in `ValidateFile` | an adopter's counterparty signs its files and asks for a declared verifier, or a transport adapter ships inside Goldpath |
| A8 | A quarantine reason can carry a row's classified values to anyone who may read the surface (T-FXI-08) | The reason is the app's own words, from its parser, validator or handler, and it is what an operator needs to fix the row. Masking free text would guess at it | an adopter's validator is found echoing classified values, or the quarantine is opened beyond the two ops roles |
| A9 | A rail has no line or byte ceiling of its own (T-FXI-11) | The pick-up job builds the line list and is the place to bound it. Counterparty files on these rails are bounded by contract. Bulk is the module for large uploads (`MaxRows`) | an adopter routes files larger than the transport's own limits through a rail, or a ledger's per-line round trips become the bottleneck (then the ledger should batch) |

## Still to model (Part 2/2, remaining checklist)

- [x] Approvals decisions (§5)
- [x] FileExchange ingress (§6)
- [ ] Notification channels (credentials, template injection)
- [ ] DataProtection keys
- [ ] Idempotency key scope
- [ ] MultiTenancy isolation
