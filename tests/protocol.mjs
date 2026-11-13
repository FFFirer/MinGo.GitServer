import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { randomBytes } from "node:crypto";
import { cp, mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { promisify } from "node:util";
import { clean, password, root, start, temp } from "./server.mjs";

const exec = promisify(execFile);
const directory = await temp("mingo-protocol-");
let server;
let cookies = new Map();
let csrf = "";
let token = "";

function git(cwd, ...args) {
  const env = { ...process.env, GIT_TERMINAL_PROMPT: "0" };
  if (token) {
    env.GIT_CONFIG_COUNT = "1";
    env.GIT_CONFIG_KEY_0 = "http.extraHeader";
    env.GIT_CONFIG_VALUE_0 = "Authorization: Basic " + Buffer.from("admin:" + token).toString("base64");
  }
  return exec("git", ["-c", "core.autocrlf=false", "-c", "credential.helper=", "-c", "protocol.version=2", ...args], { cwd, env });
}

async function request(path, method = "GET", body, status = 200, authenticated = true) {
  const headers = {};
  if (authenticated) {
    headers.Cookie = [...cookies.values()].join("; ");
    if (csrf) headers["X-CSRF-Token"] = csrf;
  }
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(server.url + path, { method, headers, body: JSON.stringify(body) });
  for (const cookie of response.headers.getSetCookie()) {
    const pair = cookie.split(";")[0];
    cookies.set(pair.split("=")[0], pair);
  }
  const text = await response.text();
  assert.equal(response.status, status, `${method} ${path} returned ${response.status}: ${text}`);
  return text ? JSON.parse(text) : undefined;
}

async function login() {
  csrf = (await request("/api/v1/session")).csrfToken;
  await request("/api/v1/session", "POST", { username: "admin", password }, 204);
  csrf = (await request("/api/v1/session")).csrfToken;
}

try {
  const data = join(directory, "data");
  server = await start(data);
  if (process.argv.includes("--generate-openapi")) {
    const spec = await request("/openapi/v1.json");
    assert.match(spec.openapi, /^3\.1\./);
    await writeFile(join(root, "specs", "openapi.json"), JSON.stringify(spec, null, 2) + "\n");
  } else {
    await request("/api/v1/repos", "GET", undefined, 401, false);
    await request("/api/v1/session", "POST", { username: "admin", password }, 400, false);
    await login();
    await request("/api/v1/repos", "POST", { name: "../escape" }, 422);
    const repo = await request("/api/v1/repos", "POST", { name: "protocol", defaultBranch: "main" }, 201);
    await request("/api/v1/repos", "POST", { name: "protocol" }, 409);
    const base = "/api/v1/repos/" + repo.id;
    const remote = server.url + repo.clonePath;
    for (const service of ["git-upload-pack", "git-receive-pack"])
      assert.equal((await fetch(remote + "/info/refs?service=" + service)).status, 401);
    assert.equal((await fetch(remote + "/git-receive-pack", {
      method: "POST", headers: { "Content-Type": "application/x-git-receive-pack-request" }, body: "0000",
    })).status, 401);
    assert.equal((await fetch(remote + "/info/refs?service=git-other")).status, 404);
    assert.equal((await fetch(remote + "/HEAD")).status, 404);
    const oldToken = (await request("/api/v1/git-token", "POST")).token;
    token = (await request("/api/v1/git-token", "POST")).token;
    const invalid = Buffer.from("admin:" + oldToken).toString("base64");
    assert.equal((await fetch(remote + "/info/refs?service=git-upload-pack", {
      headers: { Authorization: "Basic " + invalid },
    })).status, 401);

    await git(directory, "clone", remote, "writer");
    const writer = join(directory, "writer");
    await mkdir(join(writer, "目录"));
    await writeFile(join(writer, "目录", "notes.txt"), "from git\n");
    const pack = randomBytes(8 * 1024 * 1024);
    await writeFile(join(writer, "pack.bin"), pack);
    await git(writer, "add", ".");
    await git(writer, "-c", "user.name=Git test", "-c", "user.email=test@localhost", "commit", "-m", "Initial Git push");
    await git(writer, "push", "-u", "origin", "main");
    await git(directory, "clone", remote, "reader");
    const reader = join(directory, "reader");
    assert.deepEqual(await readFile(join(reader, "pack.bin")), pack);
    await request(base + "/blob?path=pack.bin", "GET", undefined, 413);
    const gitHead = (await git(writer, "rev-parse", "HEAD")).stdout.trim();
    assert.equal((await request(base + "/tree?path=" + encodeURIComponent("目录"))).entries[0].name, "notes.txt");
    await request(base + "/blob?path=../forge.db", "GET", undefined, 422);
    const change = { branch: "main", path: "目录/notes.txt", expectedHead: gitHead, content: "from web\n", message: "Web edit" };
    const outcomes = await Promise.all([1, 2].map(() => fetch(server.url + base + "/files", {
      method: "PUT",
      headers: { "Content-Type": "application/json", Cookie: [...cookies.values()].join("; "), "X-CSRF-Token": csrf },
      body: JSON.stringify(change),
    })));
    assert.deepEqual(outcomes.map(response => response.status).sort(), [200, 409]);
    const head = (await outcomes.find(response => response.ok).json()).sha;
    assert.equal((await request(base + "/blob?path=" + encodeURIComponent(change.path))).content, "from web\n");
    assert.deepEqual((await request(base + "/commits")).map(commit => commit.sha), [head, gitHead]);
    assert.equal((await request(base + "/commits/" + head)).parents, gitHead);
    assert.match((await request(base + "/diff?sha=" + head)).text, /\+from web/);
    for (const [kind, name] of [["branches", "feature/nested"], ["tags", "release/v1"]]) {
      await request(base + "/" + kind, "POST", { name, sha: head });
      assert.ok((await request(base + "/" + kind)).some(ref => ref.name === name));
      if (kind === "tags") assert.equal((await request(base + "/tree?tag=release%2Fv1")).head, head);
      await request(base + "/" + kind + "?name=" + encodeURIComponent(name), "DELETE", undefined, 204);
    }
    await request(base + "/branches?name=main", "DELETE", undefined, 409);
    await assert.rejects(git(writer, "push", "origin", ":main"));
    await git(reader, "fetch");
    await git(reader, "pull", "--ff-only");
    assert.equal(await readFile(join(reader, "目录", "notes.txt"), "utf8"), "from web\n");
    const deleted = await request(base + "/files", "DELETE", { ...change, expectedHead: head, content: null, message: "Delete file" });
    await request(base + "/blob?path=" + encodeURIComponent(change.path), "GET", undefined, 404);

    await git(reader, "pull", "--ff-only");
    await writeFile(join(reader, "git-race.txt"), "Git competitor\n");
    await git(reader, "add", "git-race.txt");
    await git(reader, "-c", "user.name=Git test", "-c", "user.email=test@localhost", "commit", "-m", "Git competitor");
    const contender = (await git(reader, "rev-parse", "HEAD")).stdout.trim();
    const [push, web] = await Promise.allSettled([
      git(reader, "push", "origin", "main"),
      fetch(server.url + base + "/files", {
        method: "PUT",
        headers: { "Content-Type": "application/json", Cookie: [...cookies.values()].join("; "), "X-CSRF-Token": csrf },
        body: JSON.stringify({ ...change, path: "web-race.txt", expectedHead: deleted.sha }),
      }),
    ]);
    assert.equal(web.status, "fulfilled");
    assert.equal(web.value.status, push.status === "fulfilled" ? 409 : 200);
    const winningHead = push.status === "fulfilled" ? contender : (await web.value.json()).sha;
    assert.equal((await request(base + "/tree")).head, winningHead);

    const empty = await request("/api/v1/repos", "POST", { name: "empty" }, 201);
    await request("/api/v1/repos/" + empty.id + "/files", "PUT", { ...change, expectedHead: null, path: "README.md" });
    await request("/api/v1/repos/" + empty.id, "DELETE", undefined, 204);
    assert.ok((await readdir(join(data, "trash"))).includes(empty.id + ".git"));

    await server.stop();
    server = undefined;
    const restored = join(directory, "restored");
    await cp(data, restored, { recursive: true });
    server = await start(restored);
    cookies = new Map();
    csrf = "";
    await login();
    assert.deepEqual((await request("/api/v1/repos")).map(r => r.name), ["protocol"]);
    await git(directory, "clone", server.url + repo.clonePath, "restored-reader");
    console.log("PASS: private clone/push/fetch/pull, 8 MiB pack, refs, history, path validation, CAS, deletion and offline restore.");
  }
} catch (error) {
  if (server) console.error(server.logs());
  throw error;
} finally {
  if (server) await server.stop();
  await clean(directory);
}
