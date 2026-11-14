import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout } from "node:timers/promises";
import { fileURLToPath } from "node:url";

export const root = fileURLToPath(new URL("../", import.meta.url));
const project = join(root, "src", "Server");
export const password = "local-test-password";
export const temp = prefix => mkdtemp(join(tmpdir(), prefix));
export const clean = path => rm(path, { recursive: true, force: true });

export async function start(data, port = 5087, { env = {} } = {}) {
  const url = `http://127.0.0.1:${port}`;
  const configuration = process.env.MINGO_TEST_CONFIGURATION ?? "Debug";
  const child = spawn("dotnet", [join(project, "bin", configuration, "net10.0", "MinGo.GitServer.dll")], {
    cwd: project,
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", DOTNET_ENVIRONMENT: "Development",
      Logging__LogLevel__Microsoft: "Warning", "Logging__LogLevel__Microsoft.Hosting.Lifetime": "Information",
      ASPNETCORE_URLS: url, Vite__Server__AutoRun: "false",
      Forge__DataPath: data, Forge__AdminUser: "admin", Forge__AdminPassword: password, ...env },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let logs = "";
  child.stdout.on("data", chunk => { logs = (logs + chunk).slice(-16000); });
  child.stderr.on("data", chunk => { logs = (logs + chunk).slice(-16000); });
  child.on("error", error => { logs += error.message; });
  const exited = new Promise(resolve => child.once("close", resolve));
  async function stop() {
    child.kill();
    await exited;
  }
  try {
    for (let attempt = 0; attempt < 100 && child.exitCode === null; attempt++) {
      if (logs.includes("Now listening on:") &&
          await fetch(url + "/health", { signal: AbortSignal.timeout(1000) }).then(r => r.ok).catch(() => false))
        return { url, stop, logs: () => logs };
      await setTimeout(100);
    }
    throw new Error("Server did not start.\n" + logs);
  } catch (error) {
    await stop();
    throw error;
  }
}
