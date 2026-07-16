import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import { type ProxyOptions, type UserConfig } from "vite";

interface ServiceProxy {
  readonly id: string;
  readonly prefix: string;
  readonly envKey: string;
  readonly defaultTarget: string;
}

const serviceProxies: readonly ServiceProxy[] = [
  { id: "chatterbox-turbo", prefix: "/proxy/chatterbox-turbo", envKey: "CHD_TARGET_CHATTERBOX_TURBO", defaultTarget: "http://127.0.0.1:8001" },
  { id: "qwen3-tts-base", prefix: "/proxy/qwen3-tts-base", envKey: "CHD_TARGET_QWEN3_TTS_BASE", defaultTarget: "http://127.0.0.1:8101" },
  { id: "chatterbox", prefix: "/proxy/chatterbox", envKey: "CHD_TARGET_CHATTERBOX", defaultTarget: "http://127.0.0.1:8000" },
  { id: "qwen3-tts", prefix: "/proxy/qwen3-tts", envKey: "CHD_TARGET_QWEN3_TTS", defaultTarget: "http://127.0.0.1:8100" },
  { id: "mpnet-base-v2", prefix: "/proxy/mpnet-base-v2", envKey: "CHD_TARGET_MPNET_BASE_V2", defaultTarget: "http://127.0.0.1:8201" },
  { id: "minilm-l6", prefix: "/proxy/minilm-l6", envKey: "CHD_TARGET_MINILM_L6", defaultTarget: "http://127.0.0.1:8200" },
  { id: "voxcpm2", prefix: "/proxy/voxcpm2", envKey: "CHD_TARGET_VOXCPM2", defaultTarget: "http://127.0.0.1:8003" },
  { id: "whisper", prefix: "/proxy/whisper", envKey: "CHD_TARGET_WHISPER", defaultTarget: "http://127.0.0.1:9000" },
  { id: "llama", prefix: "/proxy/llama", envKey: "CHD_TARGET_LLAMA", defaultTarget: "http://127.0.0.1:8080" }
];

function validateTarget(service: ServiceProxy, value: string): string {
  let target: URL;
  try {
    target = new URL(value);
  } catch {
    throw new Error(`Invalid proxy target for ${service.id} (${service.envKey}): expected a complete http: or https: origin.`);
  }

  if ((target.protocol !== "http:" && target.protocol !== "https:") || target.username || target.password || target.pathname !== "/" || target.search || target.hash) {
    throw new Error(`Invalid proxy target for ${service.id} (${service.envKey}): expected an origin without credentials, path, query, or fragment.`);
  }

  return target.origin;
}

function proxyFor(service: ServiceProxy, target: string): ProxyOptions {
  return {
    target,
    changeOrigin: true,
    rewrite: (path) => path.slice(service.prefix.length) || "/",
    configure(proxy) {
      proxy.on("error", (error, _request, response) => {
        if (!("writeHead" in response) || response.headersSent) {
          return;
        }

        const code = "code" in error && typeof error.code === "string" ? error.code : "connection failed";
        const body = JSON.stringify({
          kind: "proxy-unavailable",
          service: service.id,
          message: `The proxy could not connect to ${service.id} (${code.slice(0, 80)}).`
        });
        response.writeHead(502, {
          "content-type": "application/json; charset=utf-8",
          "content-length": Buffer.byteLength(body),
          "cache-control": "no-store"
        });
        response.end(body);
      });
    }
  };
}

function loadTargetOverrides(): Readonly<Record<string, string>> {
  const envPath = resolve(process.cwd(), ".env.local");
  const loaded: Record<string, string> = {};
  for (const line of existsSync(envPath) ? readFileSync(envPath, "utf8").split(/\r?\n/u) : []) {
    const match = /^(?:\s*export\s+)?\s*(CHD_TARGET_[A-Z0-9_]+)\s*=\s*(.*?)\s*$/u.exec(line);
    if (!match?.[1] || match[2] === undefined) continue;
    const quoted = /^(?:"([^"]*)"|'([^']*)')$/u.exec(match[2]);
    loaded[match[1]] = quoted ? (quoted[1] ?? quoted[2] ?? "") : match[2].replace(/\s+#.*$/u, "").trim();
  }

  return Object.fromEntries(serviceProxies.flatMap((service) => {
    const processValue = process.env[service.envKey];
    if (processValue !== undefined) return [[service.envKey, processValue]];
    return loaded[service.envKey] !== undefined
      ? [[service.envKey, loaded[service.envKey]]]
      : [];
  }));
}

export default (): UserConfig => {
  const env = loadTargetOverrides();
  const proxy = Object.fromEntries(serviceProxies.map((service) => {
    const target = validateTarget(service, env[service.envKey] ?? service.defaultTarget);
    return [`^${service.prefix}(?:/|$)`, proxyFor(service, target)];
  }));

  return {
    server: {
      host: "127.0.0.1",
      port: 5173,
      strictPort: true,
      open: process.env.CHD_NO_OPEN !== "1",
      cors: false,
      allowedHosts: [],
      fs: { strict: true },
      proxy
    },
    build: {
      rollupOptions: {
        input: {
          overview: "index.html",
          detail: "detail.html"
        }
      }
    }
  };
};
