import { spawn } from "node:child_process";
import { copyFile, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { delimiter, join, resolve } from "node:path";
import { expect, test } from "@playwright/test";

const root = resolve(import.meta.dirname, "..");

async function run(command: string, args: readonly string[], env: NodeJS.ProcessEnv = process.env): Promise<{ code: number; output: string }> {
  return await new Promise((resolveResult) => {
    const child = spawn(command, args, { cwd: root, env, shell: false });
    let output = "";
    child.stdout.on("data", (chunk: Buffer) => { output += chunk.toString(); });
    child.stderr.on("data", (chunk: Buffer) => { output += chunk.toString(); });
    child.on("exit", (code) => resolveResult({ code: code ?? -1, output }));
  });
}

async function makeToolchain(directory: string, npmBody?: string): Promise<NodeJS.ProcessEnv> {
  const tools = join(directory, "tools");
  await mkdir(tools, { recursive: true });
  await writeFile(join(tools, "node.cmd"), "@echo off\r\nif \"%~1\"==\"-p\" echo 24.18.0\r\nexit /b 0\r\n");
  if (npmBody !== undefined) await writeFile(join(tools, "npm.cmd"), npmBody);
  return { ...process.env, PATH: `${tools}${delimiter}${process.env.SystemRoot}\\System32`, CHD_NO_PAUSE: "1" };
}

test("start rejects missing Node with an actionable non-zero failure", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  try {
    const result = await run("cmd.exe", ["/d", "/c", join(root, "start-dashboard.cmd")], {
      ...process.env,
      PATH: `${process.env.SystemRoot}\\System32`,
      CHD_NO_PAUSE: "1"
    });
    expect(result.code).not.toBe(0);
    expect(result.output).toContain("Node.js was not found");
    expect(result.output).toContain("Node 24 LTS");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("start rejects an unsupported Node version and preserves failure", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  try {
    const tools = join(directory, "tools");
    await mkdir(tools);
    await writeFile(join(tools, "node.cmd"), "@echo off\r\nif \"%~1\"==\"-p\" echo 24.17.9\r\nif \"%~1\"==\"-e\" exit /b 1\r\n");
    const result = await run("cmd.exe", ["/d", "/c", join(root, "start-dashboard.cmd")], {
      ...process.env,
      PATH: `${tools}${delimiter}${process.env.SystemRoot}\\System32`,
      CHD_NO_PAUSE: "1"
    });
    expect(result.code).not.toBe(0);
    expect(result.output).toContain("Unsupported Node 24.17.9");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("start rejects missing npm after accepting Node", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  try {
    const env = await makeToolchain(directory);
    const result = await run("cmd.exe", ["/d", "/c", join(root, "start-dashboard.cmd")], env);
    expect(result.code).not.toBe(0);
    expect(result.output).toContain("npm was not found");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("start rejects an unsupported npm version", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  try {
    const tools = join(directory, "tools");
    await mkdir(tools);
    await writeFile(join(tools, "node.cmd"), "@echo off\r\nif \"%~1\"==\"-p\" echo 24.18.0\r\nif \"%~3\"==\"11.15.9\" exit /b 1\r\nexit /b 0\r\n");
    await writeFile(join(tools, "npm.cmd"), "@echo off\r\nif \"%~1\"==\"--version\" echo 11.15.9\r\nexit /b 0\r\n");
    const result = await run("cmd.exe", ["/d", "/c", join(root, "start-dashboard.cmd")], {
      ...process.env,
      PATH: `${tools}${delimiter}${process.env.SystemRoot}\\System32`,
      CHD_NO_PAUSE: "1"
    });
    expect(result.code).not.toBe(0);
    expect(result.output).toContain("Unsupported npm 11.15.9");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("start reports a missing lockfile and missing local executables", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  try {
    await copyFile(join(root, "start-dashboard.cmd"), join(directory, "start-dashboard.cmd"));
    const npmBody = "@echo off\r\nif \"%~1\"==\"--version\" echo 11.16.0\r\nexit /b 0\r\n";
    const env = await makeToolchain(directory, npmBody);

    const missingLock = await run("cmd.exe", ["/d", "/c", join(directory, "start-dashboard.cmd")], env);
    expect(missingLock.code).not.toBe(0);
    expect(missingLock.output).toContain("package-lock.json is missing");

    await writeFile(join(directory, "package-lock.json"), "{}");
    const missingVite = await run("cmd.exe", ["/d", "/c", join(directory, "start-dashboard.cmd")], env);
    expect(missingVite.code).not.toBe(0);
    expect(missingVite.output).toContain("Local Vite is missing");

    await mkdir(join(directory, "node_modules", ".bin"), { recursive: true });
    await writeFile(join(directory, "node_modules", ".bin", "vite.cmd"), "@exit /b 0\r\n");
    const missingTypeScript = await run("cmd.exe", ["/d", "/c", join(directory, "start-dashboard.cmd")], env);
    expect(missingTypeScript.code).not.toBe(0);
    expect(missingTypeScript.output).toContain("Local TypeScript is missing");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("ordinary start invokes only npm start and preserves its exit code", async () => {
  const directory = await mkdtemp(join(tmpdir(), "chd-runtime-"));
  const log = join(directory, "npm.log");
  try {
    const npmBody = `@echo off\r\nif \"%~1\"==\"--version\" (echo 11.16.0& exit /b 0)\r\necho %*>>\"${log}\"\r\nif \"%~1\"==\"start\" exit /b 37\r\nexit /b 99\r\n`;
    const env = await makeToolchain(directory, npmBody);
    const result = await run("cmd.exe", ["/d", "/c", join(root, "start-dashboard.cmd")], env);
    expect(result.code).toBe(37);
    expect(await readFile(log, "utf8")).toBe("start\r\n");
    expect(result.output).toContain("dashboard stopped with exit code 37");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("Vite rejects an invalid target and names its service and key", async () => {
  const executable = join(root, "node_modules", "vite", "bin", "vite.js");
  const result = await run(process.execPath, [executable], { ...process.env, CHD_NO_OPEN: "1", CHD_TARGET_LLAMA: "http://user:secret@127.0.0.1:8080/path" });
  expect(result.code).not.toBe(0);
  expect(result.output).toContain("llama");
  expect(result.output).toContain("CHD_TARGET_LLAMA");
  expect(result.output).not.toContain("secret");
});

test("Vite fails rather than hopping when the dashboard port is occupied", async () => {
  const executable = join(root, "node_modules", "vite", "bin", "vite.js");
  const result = await run(process.execPath, [executable], { ...process.env, CHD_NO_OPEN: "1" });
  expect(result.code).not.toBe(0);
  expect(result.output).toContain("Port 5173 is already in use");
});

test("package scripts keep the deterministic check order", async () => {
  const packageJson = JSON.parse(await readFile(join(root, "package.json"), "utf8")) as { scripts: Record<string, string> };
  expect(packageJson.scripts.check).toBe("npm run typecheck && npm run test:unit && npm run build && npm run test:browser && npm run test:a11y");
  expect(packageJson.scripts.start).toBe("vite");
  expect(packageJson.scripts).not.toHaveProperty("preview");
  const setup = await readFile(join(root, "setup-dashboard.cmd"), "utf8");
  expect(setup).toContain("call npm ci");
  expect(setup).toContain("playwright.cmd install chromium");
  expect(setup).toContain("call npm run check");
});

test("the strict reporter makes a skipped test fail the gate", async () => {
  const directory = await mkdtemp(join(root, "tests", ".strict-reporter-"));
  try {
    const reporterPath = join(root, "tests", "strict-reporter.mjs");
    await writeFile(join(directory, "playwright.config.mjs"), `export default { testDir: ${JSON.stringify(directory)}, reporter: [[${JSON.stringify(reporterPath)}]] };\n`);
    await writeFile(join(directory, "skipped.spec.mjs"), "import { test } from '@playwright/test'; test.skip('disabled coverage', () => {});\n");
    const result = await run(process.execPath, [join(root, "node_modules", "@playwright", "test", "cli.js"), "test", `--config=${join(directory, "playwright.config.mjs")}`]);
    expect(result.code).not.toBe(0);
    expect(result.output).toContain("Unexpected skipped/fixme tests");
    expect(result.output).toContain("disabled coverage");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
