# Air-gapped install — what the offline shape runs, step by step

A generated app is claimed to install, build, migrate and boot on a network with no route to
nuget.org or Docker Hub (SECURITY.md; ADR-0008; foundation §11). Since GA gate G14 (#338) one
golden-manifest shape proves it every night: **GmOffline** (`.github/gm-shapes.yml`, run by
[`scripts/validate-gm-offline.sh`](../../scripts/validate-gm-offline.sh)). This page is that
job written out as the steps an operator takes, so a site can do by hand exactly what CI does.

The two halves are the two sides of an air gap. The **connected** side fills a NuGet feed and an
image registry. The **air-gapped** side reaches nothing else.

## 1. Connected side: build the mirror

1. **Generate and run the app once, connected, with an empty global-packages folder.**
   What that folder holds afterwards is the closure the app needs, and nothing more. That
   includes the pinned `dotnet-ef` tool (`.config/dotnet-tools.json`), the Aspire AppHost SDK,
   DCP and the dashboard, and the test SDK.

   ```bash
   export NUGET_PACKAGES=$PWD/closure          # empty before the first restore
   goldpath new solution -n MyApp --auth none   # or: dotnet new goldpath-solution …
   cd MyApp
   dotnet build && goldpath db init --path . && goldpath db bundle --path .
   dotnet test tests/MyApp.SmokeTests           # starts every container the AppHost runs
   ```

   The CI job does this with `validate-gm.sh`, pointing `GOLDPATH_GM_APP_PACKAGES` at the folder.
   It sets the folder only *after* packing the Goldpath repository, so the closure is the
   app's, not the repository's.

2. **Flatten the closure into a feed.** A folder of `.nupkg` files is a valid NuGet source.

   ```bash
   mkdir feed && find closure -name '*.nupkg' -exec cp {} feed/ \;
   ```

3. **Mirror every image a container ran.** The CI job reads them from `docker events`: the
   `create` events between the run's start and end. It pushes each one to a registry, then
   removes it from the daemon, so the only copy left is the registry's.

   ```bash
   docker run -d -p 127.0.0.1:5000:5000 docker.io/library/registry:2.8.3
   docker tag docker.io/library/postgres:<tag> 127.0.0.1:5000/library/postgres:<tag>
   docker push 127.0.0.1:5000/library/postgres:<tag>      # likewise rabbitmq, …
   ```

4. **Carry the tools.** The pinned `specdrift` (installed with `--tool-path`) and the `goldpath`
   CLI. CI runs the CLI it built on the connected side (`dotnet run --no-build`).

## 2. Air-gapped side: install from the mirror alone

1. **Deny the network, and check it is denied.**
   - **What CI rejects:** `iptables` rejects every outbound packet except loopback and the
     Docker bridges (172.16.0.0/12). That includes the port of any HTTP proxy listening on
     loopback.
   - **Forwarded traffic:** the `DOCKER-USER` chain rejects every forwarded packet leaving the
     bridges.
   - **Proxy variables** are unset.
   - **The check:** the job stops unless `curl https://api.nuget.org/v3/index.json` and
     `docker pull docker.io/library/busybox:1.37.0` both fail.
2. **Pull the images from the registry** and tag them with the names the AppHost asks for.
   The AppHost's pull policy is "missing", so it uses them as they are.
3. **Point the app at the feed and nothing else.** The generated `nuget.config` names one
   source. Replace it with the feed: `<clear />` followed by `<add key="mirror" value="/path/to/feed" />`.
   In CI, `GOLDPATH_GM_FEED_ONLY=1` does this.
4. **Restore and build into an empty global-packages folder,** so nothing restored on the
   connected side can stand in for the feed:

   ```bash
   export NUGET_PACKAGES=$PWD/packages
   dotnet tool restore && dotnet build
   goldpath db init --path .                    # migrations, as on any install
   ```

5. **Bundle and apply.**

   ```bash
   goldpath db bundle --path .                  # artifacts/migrations/<Owner>-migrations
   artifacts/migrations/<Owner>-migrations --connection "Host=…;Database=…;Username=…;Password=…"
   ```

   CI applies each bundle twice to a fresh PostgreSQL started from the mirrored image. The
   second apply must report `No migrations were applied`.
6. **Smoke.** `dotnet test tests/MyApp.SmokeTests` boots the AppHost, which runs its containers
   from the mirrored images and drives the shape's first-click contract.

## 3. How the job proves the network was really the limit

The planted fault is `GOLDPATH_GM_OFFLINE_DROP=<package id>`. It removes one package from the
offline feed after the connected run. The air-gapped restore must then fail with `NU1101`. If
anything other than the feed could serve a package, that run would stay green.

```bash
GOLDPATH_GM_OFFLINE_DROP=Npgsql.EntityFrameworkCore.PostgreSQL ./scripts/validate-gm-offline.sh GmOffline --auth none
```

## 4. What it does not cover

- **SQL Server and the worker template** (open-threads T56). The apply step uses the shape's own PostgreSQL image,
  and GmOffline is the only offline shape.
- **A proxy that authenticates, and mirroring nuget.org wholesale.** Both are out of scope (#338).
- **The console's build.** It needs Node, and it runs only on the connected side, inside the
  Goldpath repository's pack. An adopter never builds it: the assets ship inside
  `Goldpath.Console`.
