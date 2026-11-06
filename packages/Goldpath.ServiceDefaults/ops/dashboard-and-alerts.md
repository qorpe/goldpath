# Baseline Dashboard & Alerts — Goldpath.ServiceDefaults

Every Goldpath service is born observable; this defines the baseline every service gets.
(Grafana JSON is provisioned with the Template CI work — tracked there, not silently missing.)

## Dashboard panels (RED + saturation)
| Panel | Source |
|---|---|
| Request rate / error ratio / duration p50-p95-p99 | `http.server.request.duration` (ASP.NET OTel) |
| 429 rejections | rate-limiter rejection count |
| Readiness status + flap count | `/health/ready` probe results |
| Outbound dependency latency/errors | `http.client.request.duration` |
| Runtime saturation | GC, thread pool queue, CPU (runtime instrumentation) |

## Default alert rules
| Alert | Condition (suggested) | Severity |
|---|---|---|
| Error ratio high | 5xx / total > 2% for 5m | critical |
| Readiness flapping | ready↔unready ≥ 3 in 10m | critical |
| Saturation | 429 rate > 0 sustained 5m | major |
| Latency regression | p95 > NFR target (manifest `nfr.p95LatencyMs`) for 10m | major |

These four are still prose: the baseline has no rule file yet (open thread T60).

## Severity scheme (owner decision 2026-10-20, #295)

Every alert in the family carries one `severity` label, from three classes — the ones an
operations centre integrates alarms in:

| Severity | Means | Example |
|---|---|---|
| `critical` | the service is not doing its job now; somebody acts now | no campaign pacer has ticked for a minute |
| `major` | it is doing its job badly, or will stop if nothing changes; act within the shift | one acceptance in twenty times out |
| `minor` | something needs a look, and nothing is being lost meanwhile | released items being published again |

The earlier `warning` class reads `major`. Routing and paging per class are the adopter's
Alertmanager configuration, not Goldpath's.

## Where the rule files live

A package that ships alert rules ships them as **`packages/<Package>/ops/alerts.yaml`**: plain
Prometheus rule groups, loaded with `rule_files:` in `prometheus.yml`. Beside it,
**`alerts.test.yaml`** holds its `promtool test rules` cases, and `scripts/alert-rules.sh` refuses a
rule file without them. The gate runs offline in the `alert-rules` workflow: `promtool check rules`,
`promtool test rules`, every rule's `severity` from the scheme above, and every rule's
`runbook_url` pointing at a heading that exists. promtool is taken from a pinned Prometheus
release (3.5.0) whose archive the script checks against its SHA-256; an air-gapped run passes the
verified archive in `PROMETHEUS_ARCHIVE`, or a provisioned binary of that version in `PROMTOOL`.

| Package | Rule file | Runbook |
|---|---|---|
| Goldpath.Campaign | [`alerts.yaml`](../../Goldpath.Campaign/ops/alerts.yaml) | [runbooks.md](../../Goldpath.Campaign/ops/runbooks.md) |

The other packages' alerts are still candidates written into their dashboards' `__alerts` blocks
(Jobs, Archival) or into prose, without a severity; each becomes a rule file of this shape in its
own change (open thread T60).

**Series names.** Rules query the Prometheus spelling of the meters, as the OpenTelemetry
Collector's `prometheus` exporter writes them: an instrument with unit `s` gains `_seconds`
(`goldpath_campaign_handler_duration` → `goldpath_campaign_handler_duration_seconds_bucket`), and the
service's name becomes `job`. Scrape the collector with `honor_labels: true`, or `job` is the
collector's own scrape job and every application behind it is judged as one.
A series the application stops reporting stays in the collector for the exporter's
`metric_expiration` (5 min by default): measured on Collector 0.120.0, a campaign whose binding
constraint changed showed the old constraint beside the new one until then, and by the same mechanism a former leader's
per-campaign gauges outlive its slice. The campaign dashboard reads per-campaign gauges through
`max by (campaign, …)` for that reason; lower `metric_expiration` if five minutes is too long to read
two values.

**With the Prometheus Operator**, wrap the file's `groups:` unchanged in a `PrometheusRule`:

```yaml
apiVersion: monitoring.coreos.com/v1
kind: PrometheusRule
metadata:
  name: goldpath-campaign
  labels:
    release: prometheus   # whatever your Prometheus' ruleSelector matches
spec:
  groups:
    # … the groups: list of packages/Goldpath.Campaign/ops/alerts.yaml, as it is
```
