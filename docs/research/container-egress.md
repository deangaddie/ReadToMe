# Blocking direct-IP egress from the Infra service containers on Docker Desktop + WSL2

Research for #402 (map #388), feeding #407 "Container egress per service". Docker Engine **29.8** inside Docker Desktop on WSL2. It covers the five services in `Infra/docker-compose.yml` as of commit `cd1ab0b1`.

**Method.** I read the repo's compose file, Dockerfiles, entrypoint, `audiocpp/server.json`, `llama/config/models.ini`, both similarity `app.py` files and `Infra/README.md`. The external sources are Docker's docs, Moby source (`master`), and the upstream READMEs and source at the pinned commits. **Nothing was run.** The research was read-only: no container, network, Docker setting or firewall rule was touched. Every behaviour claim below is therefore either quoted from a source or marked as inference. The [verification plan](#verification-plan-for-407) lists the checks #407 has to run before relying on any of it.

Sources:

- [D1] Docker docs, *Port publishing and mapping* (gateway modes, masquerade): https://docs.docker.com/engine/network/port-publishing/
- [D2] Docker docs, *Docker with iptables* (`DOCKER-USER`): https://docs.docker.com/engine/network/firewall-iptables/
- [D3] Compose file reference, *networks* (`internal`): https://docs.docker.com/reference/compose-file/networks/
- [D4] Docker Desktop, *Networking*: https://docs.docker.com/desktop/features/networking/
- [D5] Docker Desktop, *Windows permission requirements* (the VM): https://docs.docker.com/desktop/setup/install/windows-permission-requirements/
- [D6] Docker Desktop, *Air-gapped containers*: https://docs.docker.com/enterprise/security/hardened-desktop/air-gapped-containers/
- [D7] Settings Management `admin-settings.json` reference: https://docs.docker.com/enterprise/security/hardened-desktop/settings-management/configure-json-file/
- [D8] Enhanced Container Isolation overview: https://docs.docker.com/desktop/enterprise/hardened-desktop/enhanced-container-isolation/
- [M1] Moby `daemon/libnetwork/default_gateway.go`, `sandbox.go`, `endpoint.go`, `drivers/bridge/port_mapping_linux.go`, `drivers/bridge/bridge_linux.go`: https://github.com/moby/moby/tree/master/daemon/libnetwork
- [U1] llama.cpp server README at `7fe450e`: https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/tools/server/README.md
- [U2] whisper.cpp server README and `server.cpp` at `f24588a`: https://github.com/ggml-org/whisper.cpp/tree/f24588a272ae8e23280d9c220536437164e6ed28/examples/server
- [U3] audio.cpp README and server README (`main`, not a pinned commit): https://github.com/0xShug0/audio.cpp and https://github.com/0xShug0/audio.cpp/blob/main/app/server/README.md
- [U4] huggingface_hub environment variables: https://huggingface.co/docs/huggingface_hub/package_reference/environment_variables

---

## 1. Answer

**The cheapest option that works is an internal network per service plus one small TCP proxy container that publishes the ports.** Each service sits alone on its own `internal: true` network, so its container has no default route and nothing it sends reaches beyond that network. A single proxy container joins all five internal networks and one ordinary bridge. It publishes the same five `127.0.0.1` host ports and forwards each one to its service. The host app, the ContainerHealth dashboard and the Docker healthchecks see no change. The cost is one extra pinned image, one config file, and the caveats in §4.

The other options are each worse on one axis:

| Option | Blocks direct-IP egress? | Published ports work? | Survives restarts? | Cost / blocker |
|---|---|---|---|---|
| `internal: true` alone | Yes | **No** (§2) | Yes | Breaks the host's access to the services |
| `internal: true` **plus** a normal bridge on the same service | **No**: the default route and the port mappings both go to the normal bridge (§2) | Yes | Yes | Gives no protection |
| **Internal network + TCP proxy sidecar** (recommended) | Yes, for the five services. The proxy itself keeps egress | Yes, through the proxy | Yes: it is all in compose | One extra container and config file |
| `DOCKER-USER` rule inside the Docker Desktop VM | Yes | Yes | **Not documented, likely no** (§3.1) | The rule has to be re-applied on every start by a privileged container running in the host network namespace |
| Per-container netns holder with `iptables` OUTPUT rules | Yes | Yes (published on the holder) | Yes, while the holder lives | Five extra `NET_ADMIN` containers. Ports move to the holders, and recreating a holder means recreating its service |
| Docker Desktop *air-gapped containers* (`containersProxy`) | Yes (proxy/PAC reject, `transparentPorts: "*"`) | Not stated in the docs | Yes | **Docker Business** with enforced sign-in and Settings Management. It applies to every container on the machine |
| Windows Firewall outbound block on `com.docker.backend.exe` | Yes, for **every** container | Yes (inbound is a separate rule) | Yes | Also blocks image pulls, `docker build` and the warm-up downloads. Build-stage egress must stay allowed, so it is all-or-nothing |
| Enhanced Container Isolation | No: [D8] describes no egress filtering | n/a | n/a | Business tier, and it does not address egress |

The defence-in-depth extras below cost almost nothing. They are worth adding whichever option is chosen:

- `HF_HUB_OFFLINE=1` and `HF_HUB_DISABLE_TELEMETRY=1` on `minilm-l6` and `mpnet-base-v2`. [U4]: "If set, no HTTP calls will be made to the Hugging Face Hub". The variables are read at `huggingface_hub` import time, so they belong in the compose `environment:` block.
- llama.cpp's `--offline` flag in `Infra/llama/entrypoint.sh`. [U1]: "Offline mode: forces use of cache, prevents network access". It is listed at the pinned commit. Check that the flag is accepted in router (`--models-preset`) mode before adding it.

## 2. `internal: true` and published ports

Docker's prose does not say what `-p` does on an internal network. [D3] says only that `internal: true` "lets you create an externally isolated network", and [D1] says nothing about the combination. The answer is in Moby's source [M1]:

- Port mappings are programmed on a container's **gateway endpoint**. `endpoint.go` `programExternalConnectivity` passes the driver the IDs of the IPv4 and IPv6 gateway endpoints. The bridge driver's `addPortMappings` (`port_mapping_linux.go`) only creates bindings for the address families that endpoint is the gateway for.
- An internal network never provides the gateway. In `sandbox.go`, endpoint ordering (`Less`) puts non-internal endpoints first. `default_gateway.go` `getGatewayEndpoint` takes "the first endpoint with IPv4 connectivity", and `needDefaultGW` skips internal networks (`if ep.getNetwork().Internal() { continue }`). `hasExternalAccess` also returns false for `nw.Internal()`.
- **Consequence 1:** a container attached only to an internal network has no gateway endpoint, so no host port is bound. On Docker Desktop, the backend "listens on the specified host port" and forwards into the VM [D4]. It forwards the engine's bindings, so with no binding there is nothing to forward. *Inference from source. #407 must confirm it on Docker Desktop, see the [verification plan](#verification-plan-for-407).*
- **Consequence 2:** a container attached to an internal network **and** a normal bridge gets its default route and its port mappings from the normal bridge. The egress hole stays open, so dual-homing the service itself is useless. The proxy design works because the proxy is the only container that is dual-homed.
- Gateway mode `isolated` (`com.docker.network.bridge.gateway_mode_ipv4`) "can only be used when the network is also created with … `--internal`", and "No address is assigned to the bridge" [D1]. That makes an internal network stricter: the container can no longer reach the host's bridge address. It does not restore publishing. `routed` and `nat` both publish ports and both allow egress. **No gateway mode publishes ports while blocking container-initiated egress** [D1].
- Setting `com.docker.network.bridge.enable_ip_masquerade=false` is not documented as an egress block [D1]. Packets would leave with the container's own address, and whether they come back depends on routing inside the VM. Don't rely on it.

## 3. The alternatives in more detail

### 3.1 `DOCKER-USER` inside the Docker Desktop VM

`DOCKER-USER` is "a placeholder for user-defined rules that will be processed before rules in the `DOCKER-FORWARD` and `DOCKER` chains" [D2]. A rule there that drops forwarded traffic from the five subnets to anything except established connections would work technically. It fails on persistence and on what it takes to apply:

- [D2] does not say whether Docker keeps user rules in `DOCKER-USER` across daemon restarts.
- [D5]: the Linux VM is "managed by Docker. It is immutable so you can't extend it or change the installed software." [D6] says Docker Desktop rewrites `daemon.json` "at VM startup". No Docker Desktop page documents a supported way to add persistent firewall rules to the VM. **Inference:** a hand-added rule is lost when Docker Desktop restarts, WSL shuts down, or Docker Desktop upgrades.
- To apply it on each start, a container needs `network_mode: host` with `NET_ADMIN`, or `--privileged` with `nsenter` into the VM. That is a stronger capability than anything in the stack today, and adding one is a strange step for a supply-chain hardening effort. Enhanced Container Isolation, if it were ever enabled, blocks exactly that.
- The rules would have to match compose-assigned subnets, which change when networks are recreated, unless the compose file pins `ipam` subnets.

Verdict: fragile, and its failure is silent. A lost rule just means the egress block is gone, with no error.

### 3.2 Per-container network-namespace holder

A minimal "holder" container per service owns the network namespace and publishes the port. It runs with `cap_add: [NET_ADMIN]`, applies `iptables -P OUTPUT DROP` plus `-A OUTPUT -o lo -j ACCEPT` and `-A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT`, then sleeps. The service joins with `network_mode: service:<holder>`. Replies to inbound connections on the published port are ESTABLISHED, so the host can still reach the service, and nothing the service starts itself gets out. Restarting the service keeps the rules, because the namespace belongs to the holder. Recreating the holder means recreating the service. Sharing one holder across services would merge their `localhost` and lose today's per-service isolation, so it takes **five** holders, each with `NET_ADMIN`, and an image with `iptables`. It works, but it has more moving parts than the proxy.

### 3.3 Docker Desktop settings

- **Air-gapped containers** [D6][D7] are the only Docker Desktop setting that restricts container egress. `containersProxy` with `"transparentPorts": "*"` and an `embeddedPac` returning `PROXY reject.docker.internal:1234` blocks container traffic. It needs "A Docker Business subscription", enforced sign-in, and `admin-settings.json` Settings Management. It is machine-wide, the docs say nothing about inbound or published ports, and they warn that "advanced users might bypass restrictions". Not a fit for a personal-tier dev box.
- **Host firewall on the backend.** [D4]: "All outbound container network traffic originates from the `com.docker.backend` process", and it suggests applying rules to `com.docker.backend.exe` on Windows. Blocking its outbound traffic would cut egress for every container, but also for pulls, builds (`git fetch`, `apt`, `pip`) and `docker-compose.warmup.yml`. Toggling it around builds is manual and easy to forget. It might fit a later *tripwire* idea (the map's "Not yet specified" note), but it is not a per-service control.
- **Enhanced Container Isolation** [D8] covers container-to-host escape, not network egress.

## 4. The recommended shape (for #407 to implement)

This is a sketch, not tested:

```yaml
services:
  llama:
    networks: [llama]          # ports: removed, the proxy publishes 8080
  # …same for audiocpp, whisper, minilm-l6, mpnet-base-v2

  egress-proxy:                # name is a placeholder
    <<: *hardened
    image: nginxinc/nginx-unprivileged:<version>@sha256:<digest>   # or haproxy, pinned by digest
    networks: [edge, llama, audiocpp, whisper, minilm-l6, mpnet-base-v2]
    ports:
      - "127.0.0.1:8080:8080"
      - "127.0.0.1:8004:8004"
      - "127.0.0.1:9000:9000"
      - "127.0.0.1:8200:8200"
      - "127.0.0.1:8201:8201"
    volumes:
      - ./proxy/nginx.conf:/etc/nginx/nginx.conf:ro
    tmpfs: [/tmp, /var/cache/nginx, /var/run]

networks:
  edge:                        # the only non-internal network
  llama:         { internal: true }
  audiocpp:      { internal: true }
  whisper:       { internal: true }
  minilm-l6:     { internal: true }
  mpnet-base-v2: { internal: true }
```

The nginx `stream {}` block uses one `server { listen <port>; proxy_pass $upstream; }` per service, with `resolver 127.0.0.11` and a variable upstream.

Caveats the implementation must handle:

1. **Upstreams come and go.** Only one GPU container runs at a time, so `llama` and `audiocpp` are never both up, and any service may be stopped. The proxy must resolve upstreams **per connection**: Docker's embedded DNS at `127.0.0.11` plus a variable in `proxy_pass`. It must not resolve them at startup, where nginx fails with "host not found in upstream". Check that Docker's embedded resolver still answers service names when the proxy has `dns: 127.0.0.1` (from `*hardened`). `dns:` sets the *external* upstream servers for the embedded resolver, but confirm it.
2. **Long, silent connections.** A llama model switch through autoload can take minutes with no bytes sent, against the app's 300 s budget. audio.cpp has `busy_timeout_ms: 300000` and model switches of 9 to 31 s. Set the stream `proxy_timeout` well above 300 s (for example 15 min).
3. **Compose changes the app sees: none.** Ports, host addresses and protocols stay the same. The Docker `HEALTHCHECK`s run inside each container against `localhost`, so they are unaffected. Container-name probes (`docker logs`, `docker compose ps`) are unaffected too.
4. **Per-service isolation weakens slightly.** Each service network now has two members, the service and the proxy. A compromised service could open a connection to the proxy's listener for *another* service and reach it that way. That is the same reach the host already has, and the services expose no secrets. Accept it, or bind each `listen` only to the `edge` address. The second is harder to do statically.
5. **The proxy keeps egress.** It is on `edge`. Keep it a minimal, digest-pinned, first-party image with the same hardening as the rest, and add it to `scripts/scan-images.ps1` and `refresh-pins.ps1`.
6. **Warm-up stays as it is.** `docker-compose.warmup.yml` is a separate project on ordinary networks with no `dns` override. It is the sanctioned egress path for model downloads, and it must not join the internal networks.
7. **Build-stage egress is unaffected.** `internal` applies to the networks that running containers join, not to `docker build`.

## 5. Runtime egress per service

None of the five services needs runtime egress. Every model is mounted read-only from the host and loaded from a local path, and every download path is a separate, explicit step: the warm-up compose file, or `huggingface-cli` on the host.

| Service | Model source at runtime | Download or phone-home path in the code | Needs runtime egress? |
|---|---|---|---|
| `llama` | `/models` (ro bind). `models.ini` presets use only local `model = /models/…gguf` paths | The binary *can* download: `-hf/--hf-repo`, `-mu/--model-url`, `-dr/--docker-repo` [U1], and it is linked against OpenSSL. `entrypoint.sh` passes none of them. Router mode is configured only by the local preset file. The router's `POST /models` download behaviour was not verified verbatim. No telemetry is described [U1]. | **No.** Defence in depth: `--offline` |
| `audiocpp` | `/models` (ro bind). `server.json` uses only local `path` entries, `lazy_load` | [U3]: "A config does not download model weights". Downloads exist only in a build with `AUDIOCPP_BUILD_NATIVE_MODEL_MANAGER=ON` *and* `--ui-management`. The compose `command` is `server --config /app/server.json`. "Normal server builds do not build or link that HTTP/TLS dependency" [U3]. Whether the `full-cuda12` image is such a build was not checked. No telemetry is described. | **No.** It is a third-party image with GPU access, so it is the strongest case for the block |
| `whisper` | One file, `/models/ggml-base.en.bin` (ro bind) | `server.cpp` includes no HTTP client (only `httplib.h` as a server). `system()` runs only local `ffmpeg` for `--convert`, which is not passed. The model is fetched by the warm-up compose file only [U2]. | **No** |
| `minilm-l6` | `/cache` (ro bind, filled by warm-up). `SentenceTransformer(..., revision=<sha>, local_files_only=True)` | `local_files_only=True` stops Hub downloads. It is not verified in sentence-transformers source that *no* HTTP call is made. `HF_HUB_OFFLINE=1` guarantees it for `huggingface_hub` [U4]. Telemetry is described for `transformers`/`datasets`/`gradio` and is disabled by `HF_HUB_DISABLE_TELEMETRY=1` [U4]. `pip` is uninstalled from the image. | **No.** Add `HF_HUB_OFFLINE=1`, `HF_HUB_DISABLE_TELEMETRY=1` |
| `mpnet-base-v2` | Same as `minilm-l6` (different repo and revision) | Same | **No.** Same env vars |

The current DNS block (`dns: 127.0.0.1`) already breaks every one of those code paths by name, because each targets a hostname. The direct-IP block matters only against a payload with a hard-coded address, which is the case #388 names.

## Verification plan (for #407)

This research ran nothing. Before #407 relies on the findings, it should run these checks once on Docker Desktop. They are cheap, and steps 1 to 3 can use a throwaway compose project:

1. **Internal-only blocks publishing on Docker Desktop.** Run a pinned web-server container on an `internal: true` network with `ports: ["127.0.0.1:<p>:80"]`. Expect `curl http://127.0.0.1:<p>/` from Windows to fail, and `docker port` to show no binding (or the engine to reject the mapping).
2. **The proxy path works.** With the proxy design, `curl` each published port from Windows. Stop `llama` and start `audiocpp`, and check that the proxy keeps serving the service that is up without a restart.
3. **Egress is gone.** From inside each service, try a connection to a public IP literal: `docker exec read2me-whisper curl -m 5 http://<public-ip>/`, or a Python `socket.create_connection` in the similarity images. Expect "Network is unreachable" (no route), not a timeout. Repeat after a Docker Desktop restart and after `docker compose down && up`.
4. **No regression.** Run the ContainerHealth dashboard checks and one app-driven TTS, transcription and similarity round trip. Do one llama model switch that takes longer than the proxy's default timeout.
