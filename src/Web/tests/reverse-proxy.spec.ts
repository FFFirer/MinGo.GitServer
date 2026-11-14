import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { request } from "node:http";
import { createServer, type Server } from "node:https";
import { join } from "node:path";
import { promisify } from "node:util";
import { chromium, expect, test } from "@playwright/test";
import { clean, password, start, temp } from "../../../tests/server.mjs";

const exec = promisify(execFile);

test("HTTPS 反向代理按入口 Host 复制并克隆同一仓库", async () => {
  const directory = await temp("mingo-proxy-");
  const hosts = ["git.example.test", "git.internal.example.test"];
  const proxies: Server[] = [];
  let backend: Awaited<ReturnType<typeof start>> | undefined;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
  try {
    const keyPath = join(directory, "key.pem");
    const certPath = join(directory, "cert.pem");
    const pfxPath = join(directory, "cert.pfx");
    await exec("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
      "-subj", `/CN=${hosts[0]}`, "-addext", `subjectAltName=${[...hosts.map(host => `DNS:${host}`), "IP:127.0.0.1"].join(",")}`,
      "-keyout", keyPath, "-out", certPath]);
    await exec("openssl", ["pkcs12", "-export", "-inkey", keyPath, "-in", certPath,
      "-out", pfxPath, "-passout", "pass:proxy-test"]);
    const direct = "https://127.0.0.1:5090";
    backend = await start(join(directory, "data"), 5089, {
      env: {
        ASPNETCORE_ENVIRONMENT: "Production", DOTNET_ENVIRONMENT: "Production",
        ASPNETCORE_URLS: `http://127.0.0.1:5089;${direct}`,
        ASPNETCORE_Kestrel__Certificates__Default__Path: pfxPath,
        ASPNETCORE_Kestrel__Certificates__Default__Password: "proxy-test",
        Forge__TrustedProxy: "127.0.0.1",
        Forge__ForwardedAllowedHosts__0: hosts[0], Forge__ForwardedAllowedHosts__1: hosts[1],
        "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics": "Information",
      },
    });
    const tls = { key: await readFile(keyPath), cert: await readFile(certPath) };
    const origins: string[] = [];
    for (const [index, host] of hosts.entries()) {
      const upstreamUrl = backend.url;
      const proxy = createServer(tls, (incoming, outgoing) => {
        const authority = new URL(origins[index]).host;
        if (incoming.headers.host !== authority) {
          outgoing.writeHead(421).end();
          return;
        }
        const headers: typeof incoming.headers = { ...incoming.headers,
          host: index === 0 ? authority : "forge:8080",
          "x-forwarded-proto": "https", "x-forwarded-for": incoming.socket.remoteAddress,
        };
        delete headers["x-forwarded-host"];
        if (index === 1) headers["x-forwarded-host"] = authority;
        const upstream = request(upstreamUrl + incoming.url, { method: incoming.method, headers }, response => {
          outgoing.writeHead(response.statusCode!, response.headers);
          response.pipe(outgoing);
        });
        upstream.on("error", () => outgoing.destroy());
        incoming.pipe(upstream);
      });
      proxies.push(proxy);
      await new Promise<void>((resolve, reject) => {
        proxy.once("error", reject);
        proxy.listen(0, "127.0.0.1", resolve);
      });
      const address = proxy.address();
      if (!address || typeof address === "string") throw new Error("Proxy has no TCP address");
      origins.push(`https://${host}:${address.port}`);
    }

    browser = await chromium.launch({ channel: process.env.MINGO_TEST_BROWSER_CHANNEL ?? "chromium",
      args: [`--host-resolver-rules=${hosts.map(host => `MAP ${host} 127.0.0.1`).join(",")}`, "--no-proxy-server"] });
    let token = "";
    for (const [index, origin] of [...origins, direct].entries()) {
      const context = await browser.newContext({ ignoreHTTPSErrors: true, permissions: ["clipboard-read", "clipboard-write"] });
      const page = await context.newPage();
      await page.goto(origin);
      await page.getByLabel("密码", { exact: true }).fill(password);
      await page.getByRole("button", { name: "登录", exact: true }).click();
      if (index === 0) {
        await page.getByRole("button", { name: "新建仓库" }).click();
        await page.getByLabel("仓库名称").fill("proxy-demo");
        const created = page.waitForResponse(response => response.status() === 201 && response.url().endsWith("/api/v1/repos"));
        await page.getByRole("button", { name: "创建", exact: true }).click();
        const response = await created;
        const repo = await response.json();
        expect(repo.clonePath).toBe("/git/admin/proxy-demo.git");
        expect(response.headers().location).toBe(`/api/v1/repos/${repo.id}`);
        await page.getByRole("button", { name: "Git 令牌" }).click();
        await page.getByRole("button", { name: "生成令牌" }).click();
        const field = page.getByLabel("令牌", { exact: true });
        await expect(field).toHaveValue(/^[a-f0-9]{64}$/);
        token = await field.inputValue();
        await page.getByRole("button", { name: "取消" }).click();
      } else {
        await page.getByRole("button", { name: "proxy-demo" }).click();
      }
      await page.getByRole("button", { name: "复制地址" }).click();
      await expect(page.getByText("已复制 Git 地址", { exact: true })).toBeVisible();
      const cloneUrl = await page.evaluate(() => navigator.clipboard.readText());
      expect(cloneUrl).toBe(`${origin}/git/admin/proxy-demo.git`);
      const url = new URL(cloneUrl);
      // The test endpoints use an ephemeral self-signed certificate.
      const tlsBackend = process.platform === "win32" ? ["-c", "http.sslBackend=schannel"] : [];
      await exec("git", ["-c", "credential.helper=", "-c", "http.proxy=", "-c", "http.sslVerify=false",
        ...tlsBackend, "-c", `http.curloptResolve=${url.host}:127.0.0.1`,
        "clone", cloneUrl, join(directory, `clone-${index}`)], {
        env: { ...process.env, GIT_TERMINAL_PROMPT: "0", GIT_CONFIG_COUNT: "1", GIT_CONFIG_KEY_0: "http.extraHeader",
          GIT_CONFIG_VALUE_0: "Authorization: Basic " + Buffer.from(`admin:${token}`).toString("base64") },
      });
      await expect.poll(() => backend?.logs()).toContain(`GET ${cloneUrl}/info/refs`);
      await context.close();
    }
    const denied = await fetch(backend.url + "/health?host=denied", { headers: {
      "X-Forwarded-Host": "forged.example.test", "X-Forwarded-Proto": "https", "X-Forwarded-For": "192.0.2.1",
    } });
    expect(denied.ok).toBe(true);
    await expect.poll(() => backend?.logs()).toContain("https://127.0.0.1:5089/health?host=denied");
    const oneHop = await fetch(backend.url + "/health?hop=1", { headers: {
      "X-Forwarded-Host": `${hosts[0]}, ${hosts[1]}:8443`, "X-Forwarded-Proto": "http, https",
      "X-Forwarded-For": "192.0.2.1, 127.0.0.1",
    } });
    expect(oneHop.ok).toBe(true);
    await expect.poll(() => backend?.logs()).toContain(`https://${hosts[1]}:8443/health?hop=1`);

    const untrusted = await start(join(directory, "untrusted"), 5091, { env: {
      ASPNETCORE_ENVIRONMENT: "Production", DOTNET_ENVIRONMENT: "Production",
      Forge__TrustedProxy: "192.0.2.10", Forge__ForwardedAllowedHosts__0: hosts[0],
      "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics": "Information",
    } });
    try {
      const response = await fetch(untrusted.url + "/api/v1/session", { headers: {
        "X-Forwarded-Host": hosts[0], "X-Forwarded-Proto": "https", "X-Forwarded-For": "192.0.2.1",
      } });
      expect(response.status).toBe(400);
      expect((await response.json()).detail).toBe("HTTPS is required.");
      await expect.poll(() => untrusted.logs()).toMatch(/Request finished .* GET http:\/\/127\.0\.0\.1:5091\/api\/v1\/session/);
    } finally {
      await untrusted.stop();
    }
  } finally {
    await browser?.close();
    await Promise.all(proxies.map(proxy => new Promise<void>(resolve => proxy.close(() => resolve()))));
    await backend?.stop();
    await clean(directory);
  }
});
