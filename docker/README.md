# Docker

Two images — `orkis-daemon` and `orkis-web` (gateway) — and two compose files. These are the
long-lived services; the CLI is a client tool that runs natively (see below). The SDK image
is used only to *build*; nothing shipped carries the .NET runtime (the services are
self-contained single-file binaries, so `runtime-deps` supplies only native libraries).

## Configure

One `config.json` (here in `docker/`) is bind-mounted read-only into both the daemon and the
gateway. It uses container paths and references secrets by env-var name; put the actual
secrets in an `.env` beside the compose file:

```sh
echo "ANTHROPIC_API_KEY=sk-ant-..." > docker/.env
```

## Run from source (dev)

```sh
docker compose -f docker/docker-compose.yml up --build
```

The gateway is on http://localhost:7420. The daemon owns state (the `orkis-data` volume) and
the gateway reaches it over the shared Unix socket (`orkis-sock`). For a keyless demo,
uncomment `command: ["--offline"]` on the daemon.

## Run from GHCR (deploy)

Images publish to `ghcr.io/craig-b/orkis-*` on every `v*` tag (see
`.github/workflows/docker-publish.yml`).

```sh
docker compose -f docker/docker-compose.ghcr.yml pull
docker compose -f docker/docker-compose.ghcr.yml up -d
```

Public packages pull without auth; for private ones `docker login ghcr.io` first.

## Sandbox tiers

The daemon auto-selects the strongest sandbox the container is allowed to run, so **one image
scales with granted privilege**:

| `sandbox` | Container needs | Notes |
|-----------|-----------------|-------|
| `process` | nothing | weakest isolation; no `security_opt` needed |
| `bubblewrap` (default) | unprivileged user namespaces (`seccomp=unconfined`, maybe `apparmor=unconfined`) | set in the compose files |
| `firecracker` | `/dev/kvm` + `/dev/net/tun`, `NET_ADMIN` (or `privileged`), and guest images | host must expose KVM (bare metal or nested virt); see the commented block in `docker-compose.ghcr.yml` |

For Firecracker, build the guest kernel + rootfs with `scripts/setup-firecracker.sh` and mount
them into `/var/lib/orkis/firecracker`. Without KVM the daemon falls back to bubblewrap, then
process — no image change.

## The CLI

The CLI is a client tool, not a service, so it is **not** containerized — it ships as a
native binary (the `orkis-cli-*` asset on each GitHub Release, or `dotnet publish
src/Orkis.Cli`) that runs anywhere with no runtime. Run it on the daemon host against the
socket, or from your workstation against the gateway:

```sh
orkis --socket http://host:7420 --token "$ORKIS_TOKEN" ps
```
