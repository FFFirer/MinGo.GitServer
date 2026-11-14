import { api, ApiError, result, session } from "./api/client";
import type { components } from "./api/schema";
import "./style.css";

type Repo = components["schemas"]["RepositoryInfo"];
type Tree = components["schemas"]["GitTree"];
type Blob = components["schemas"]["GitBlob"];
type Commit = components["schemas"]["GitCommit"];
type Ref = components["schemas"]["GitReference"];
type Tab = "files" | "commits" | "branches" | "tags";

function element<K extends keyof HTMLElementTagNameMap>(tag: K, content = "", className = "") {
  const node = document.createElement(tag);
  node.textContent = content;
  node.className = className;
  return node;
}

function input(label: string, type = "text", value = "", required = true) {
  const wrapper = element("label", label);
  const field = element("input");
  field.type = type;
  field.value = value;
  field.required = required;
  wrapper.append(field);
  return { wrapper, field };
}

class Forge extends HTMLElement {
  private user = "";
  private repos: Repo[] = [];
  private repo?: Repo;
  private branch = "";
  private tag?: string;
  private tab: Tab = "files";
  private directory = "";
  private tree?: Tree;
  private file?: { path: string; blob: Blob };
  private history: Commit[] = [];
  private commit?: { entry: Commit; diff: string };
  private references: Ref[] = [];
  private busy = false;
  private locked = new Set<HTMLButtonElement>();
  private notice = element("p", "", "notice");
  private dialog?: HTMLDialogElement;

  connectedCallback() {
    this.notice.setAttribute("role", "status");
    this.render();
    this.run(() => this.start());
  }

  private async start() {
    this.user = (await session()).username ?? "";
    if (this.user) {
      await this.list();
      const id = new URLSearchParams(location.hash.slice(1)).get("repo");
      const repo = this.repos.find(item => item.id === id);
      if (repo) await this.open(repo);
    } else {
      this.render();
    }
  }

  private run(action: () => Promise<void>) {
    if (this.busy) return;
    this.busy = true;
    this.notice.textContent = "";
    this.lockButtons(this);
    void action().catch(async (error: unknown) => {
      if (error instanceof ApiError && error.status === 401 && this.user) {
        this.user = "";
        this.repo = undefined;
        this.dialog?.close();
        await session().catch(() => undefined);
        this.render();
      }
      const target = this.dialog?.open ? this.dialog.querySelector<HTMLElement>("[role=alert]") ?? this.notice : this.notice;
      target.textContent = error instanceof Error ? error.message : "操作失败";
    }).finally(() => {
      this.locked.forEach(button => { button.disabled = false; });
      this.locked.clear();
      this.busy = false;
    });
  }

  private lockButtons(scope: ParentNode) {
    scope.querySelectorAll<HTMLButtonElement>("button:not(:disabled)").forEach(button => {
      this.locked.add(button);
      button.disabled = true;
    });
  }

  private button(label: string, action: () => void, className = "") {
    const button = element("button", label, className);
    button.type = "button";
    button.addEventListener("click", action);
    return button;
  }

  private async list() {
    this.repos = await result(api.GET("/api/v1/repos"));
    this.render();
  }

  private async open(repo: Repo) {
    repo = await result(api.GET("/api/v1/repos/{id}", { params: { path: { id: repo.id } } }));
    const tree = await result(api.GET("/api/v1/repos/{id}/tree", { params: { path: { id: repo.id } } }));
    this.repo = repo;
    this.branch = repo.defaultBranch;
    this.tag = undefined;
    this.tab = "files";
    this.directory = "";
    this.file = undefined;
    this.tree = tree;
    history.replaceState(null, "", `#repo=${repo.id}`);
    this.render();
  }

  private home() {
    this.repo = undefined;
    history.replaceState(null, "", location.pathname);
    this.render();
  }

  private get params() { return { id: this.repo!.id }; }
  private get revision() { return this.tag ? { tag: this.tag } : { branch: this.branch }; }

  private async browse(path = "", branch = this.branch, tag = this.tag, filePath?: string) {
    const query = tag ? { tag, path } : { branch, path };
    const tree = await result(api.GET("/api/v1/repos/{id}/tree", { params: { path: this.params, query } }));
    const file = filePath ? await this.loadFile(filePath, tree.head ?? undefined) : undefined;
    this.tab = "files";
    this.directory = path;
    this.branch = branch;
    this.tag = tag;
    this.tree = tree;
    this.file = file;
    this.render();
  }

  private async showFile(path: string) {
    this.file = await this.loadFile(path, this.tree?.head ?? undefined);
    this.render();
  }

  private async loadFile(path: string, sha?: string) {
    const blob = await result(api.GET("/api/v1/repos/{id}/blob", {
      params: { path: this.params, query: { path, sha } },
    }));
    return { path, blob };
  }

  private async showHistory(skip = 0) {
    const entries = await result(api.GET("/api/v1/repos/{id}/commits", {
      params: { path: this.params, query: { ...this.revision, skip, count: 25 } },
    }));
    this.history = skip ? this.history.concat(entries) : entries;
    this.tab = "commits";
    this.commit = undefined;
    this.render();
    if (entries.length < 25) this.querySelector<HTMLButtonElement>(".more")?.remove();
  }

  private async showCommit(sha: string) {
    const [entry, diff] = await Promise.all([
      result(api.GET("/api/v1/repos/{id}/commits/{sha}", { params: { path: { ...this.params, sha } } })),
      result(api.GET("/api/v1/repos/{id}/diff", { params: { path: this.params, query: { sha } } })),
    ]);
    this.commit = { entry, diff: diff.text };
    this.render();
  }

  private async showRefs(tab: "branches" | "tags") {
    const route = tab === "branches" ? "/api/v1/repos/{id}/branches" : "/api/v1/repos/{id}/tags";
    this.references = await result(api.GET(route, { params: { path: this.params } }));
    this.tab = tab;
    this.render();
  }

  private render() {
    const top = element("header", "", "top");
    const brand = this.button("MinGo / Git", () => this.home(), "brand");
    top.append(brand);
    if (this.user) {
      const account = element("div", "", "account");
      account.append(element("span", this.user), this.button("Git 令牌", () => this.tokenDialog()),
        this.button("退出", () => this.run(async () => {
          await result(api.DELETE("/api/v1/session"));
          this.user = "";
          this.home();
          await session();
        })));
      top.append(account);
    }
    const main = element("main");
    main.append(this.notice);
    if (!this.user) this.renderLogin(main);
    else if (!this.repo) this.renderList(main);
    else this.renderRepo(main);
    for (const child of Array.from(this.children)) {
      if (child !== this.dialog) child.remove();
    }
    this.prepend(top, main);
    if (this.busy) this.lockButtons(this);
  }

  private renderLogin(main: HTMLElement) {
    const form = element("form", "", "login panel");
    const user = input("用户名", "text", "admin");
    user.field.autocomplete = "username";
    const password = input("密码", "password");
    password.field.autocomplete = "current-password";
    const submit = element("button", "登录", "primary");
    form.append(element("h1", "登录 MinGo Git"), user.wrapper, password.wrapper, submit);
    form.addEventListener("submit", event => {
      event.preventDefault();
      this.run(async () => {
        await result(api.POST("/api/v1/session", { body: { username: user.field.value, password: password.field.value } }));
        await this.start();
      });
    });
    main.append(form);
  }

  private renderList(main: HTMLElement) {
    const header = element("div", "", "heading");
    header.append(element("h1", "仓库"), this.button("新建仓库", () => this.createRepo(), "primary"));
    const list = element("ul", "", "panel rows");
    for (const repo of this.repos) {
      const row = element("li");
      const button = this.button(repo.name, () => this.run(() => this.open(repo)), "repo-link");
      row.append(button, element("span", repo.defaultBranch, "mono muted"));
      list.append(row);
    }
    if (!this.repos.length) list.append(element("li", "暂无仓库", "muted"));
    main.append(header, list);
  }

  private renderRepo(main: HTMLElement) {
    const repo = this.repo!;
    const header = element("div", "", "heading");
    const title = element("div");
    title.append(this.button("所有仓库", () => this.home(), "quiet"), element("h1", repo.name));
    header.append(title, this.button("删除仓库", () => this.removeRepo(), "danger"));
    const clone = element("div", "", "clone");
    const address = new URL(repo.clonePath, location.origin).href;
    const url = element("code", address);
    clone.append(url, this.button("复制地址", () => this.run(async () => {
      if (navigator.clipboard?.writeText) {
        try {
          await navigator.clipboard.writeText(address);
          this.notice.textContent = "已复制 Git 地址";
          return;
        } catch {
          // Try copying the selected address if the clipboard API is blocked.
        }
      }

      const selection = window.getSelection();
      if (selection) {
        const range = document.createRange();
        range.selectNodeContents(url);
        selection.removeAllRanges();
        selection.addRange(range);
        try {
          // The clipboard API is unavailable on ordinary HTTP pages.
          if (document.execCommand("copy")) {
            selection.removeAllRanges();
            this.notice.textContent = "已复制 Git 地址";
            return;
          }
        } catch {
          // Keep the address selected for manual copying.
        }
      }
      this.notice.textContent = "无法自动复制，请手动复制 Git 地址";
    })));
    const nav = element("nav", "", "tabs");
    nav.setAttribute("aria-label", "仓库功能");
    const tabs: [Tab, string, () => Promise<void>][] = [
      ["files", "文件", () => this.browse()],
      ["commits", "提交", () => this.showHistory()],
      ["branches", "分支", () => this.showRefs("branches")],
      ["tags", "标签", () => this.showRefs("tags")],
    ];
    for (const [id, label, action] of tabs) {
      const button = this.button(label, () => this.run(action), id === this.tab ? "active" : "");
      if (id === this.tab) button.setAttribute("aria-current", "page");
      nav.append(button);
    }
    main.append(header, clone, nav);
    if (this.tab === "files") this.renderFiles(main);
    else if (this.tab === "commits") this.renderHistory(main);
    else this.renderRefs(main);
  }

  private renderFiles(main: HTMLElement) {
    if (!this.repo!.isInitialized && !this.tag && !this.tree?.head) {
      const panel = element("section", "", "panel directory");
      const title = element("div", "", "bar");
      title.append(element("strong", "等待首次推送"),
        this.button("新建文件", () => this.editFile(false, null), "primary"));
      const address = new URL(this.repo!.clonePath, location.origin).href;
      panel.append(title, element("p", "在已有的本地仓库中运行以下命令，推送当前分支。", "empty"),
        element("pre", `git remote add origin ${address}\ngit push -u origin HEAD`, "empty mono"));
      main.append(panel);
      return;
    }
    const layout = element("div", "", `files ${this.file ? "with-file" : ""}`);
    const sidebar = element("section", "", "panel directory");
    const title = element("div", "", "bar");
    title.append(element("strong", this.tag ?? this.branch, "mono"));
    if (!this.tag) title.append(this.button("新建文件", () => this.editFile(false, null), "primary"));
    sidebar.append(title);
    const breadcrumbs = element("div", "", "crumbs");
    breadcrumbs.append(this.button("根目录", () => this.run(() => this.browse()), "quiet"));
    let prefix = "";
    for (const segment of this.directory.split("/").filter(Boolean)) {
      prefix = prefix ? `${prefix}/${segment}` : segment;
      const target = prefix;
      breadcrumbs.append(this.button(segment, () => this.run(() => this.browse(target)), "quiet"));
    }
    sidebar.append(breadcrumbs);
    const rows = element("ul", "", "rows");
    for (const entry of this.tree?.entries ?? []) {
      const row = element("li");
      const path = this.directory ? `${this.directory}/${entry.name}` : entry.name;
      const label = entry.kind === "tree" ? `${entry.name}/` : entry.name;
      if (entry.kind === "commit") row.append(element("code", label + " · 子模块"));
      else row.append(this.button(label,
        () => this.run(() => entry.kind === "tree" ? this.browse(path) : this.showFile(path)), "path"));
      rows.append(row);
    }
    if (!rows.children.length) rows.append(element("li", "目录为空", "muted"));
    sidebar.append(rows);
    layout.append(sidebar);
    const viewer = element("section", "", "panel viewer");
    if (this.file) {
      const { path, blob } = this.file;
      const bar = element("div", "", "bar");
      bar.append(this.button("返回目录", () => { this.file = undefined; this.render(); }, "back"),
        element("strong", path, "mono"));
      const mode = this.tree?.entries.find(entry => entry.name === path.split("/").at(-1))?.mode;
      if (!this.tag && (mode === "100644" || mode === "100755")) {
        const actions = element("div", "", "actions file-actions");
        if (blob.content != null) actions.append(this.button("编辑文件", () => this.editFile(), "primary"));
        actions.append(this.button("删除文件", () => this.editFile(true), "danger"));
        bar.append(actions);
      }
      viewer.append(bar, element("pre", blob.content ?? "此文件无法作为 UTF-8 文本预览"));
    } else {
      viewer.append(element("p", "选择文件查看内容", "empty muted"));
    }
    layout.append(viewer);
    main.append(layout);
  }

  private renderHistory(main: HTMLElement) {
    const list = element("ul", "", "panel rows");
    if (!this.history.length) list.append(element("li", "暂无提交", "muted"));
    for (const commit of this.history) {
      const row = element("li");
      row.append(this.button(commit.message.split("\n")[0] || "无提交说明",
        () => this.run(() => this.showCommit(commit.sha)), "path"),
        element("code", commit.sha.slice(0, 7), "muted"));
      list.append(row);
    }
    if (this.commit) {
      const { entry, diff } = this.commit;
      const details = element("section", "", "panel commit");
      details.append(this.button("返回提交列表", () => { this.commit = undefined; this.render(); }),
        element("h2", entry.message.split("\n")[0]), element("p", `${entry.author} · ${entry.authoredAt}`, "muted"),
        element("code", entry.sha), element("pre", entry.message), element("pre", diff || "没有文件差异", "diff"));
      main.append(details);
      return;
    }
    main.append(list);
    if (this.history.length >= 25)
      main.append(this.button("更多提交", () => this.run(() => this.showHistory(this.history.length)), "more"));
  }

  private renderRefs(main: HTMLElement) {
    const tab = this.tab === "branches" ? "branches" : "tags";
    const bar = element("div", "", "heading");
    bar.append(element("h2", tab === "branches" ? "分支" : "标签"),
      this.button(tab === "branches" ? "新建分支" : "新建标签", () => this.createRef(tab), "primary"));
    const list = element("ul", "", "panel rows");
    for (const ref of this.references) {
      const row = element("li");
      row.append(this.button(ref.name, () => this.run(() =>
        tab === "branches" ? this.browse("", ref.name, "") : this.browse("", this.branch, ref.name)), "path"),
        element("code", ref.sha.slice(0, 7), "muted"));
      if (tab !== "branches" || ref.name !== this.repo!.defaultBranch)
        row.append(this.button("删除", () => this.removeRef(tab, ref), "danger"));
      list.append(row);
    }
    if (!this.references.length) list.append(element("li", "暂无引用", "muted"));
    main.append(bar, list);
  }

  private modal(title: string, submitText: string, submit: () => Promise<void>, full = false) {
    const dialog = element("dialog", "", full ? "full" : "");
    const form = element("form");
    const body = element("div", "", "fields");
    const alert = element("p", "", "notice");
    alert.setAttribute("role", "alert");
    const actions = element("div", "", "actions");
    const save = element("button", submitText, "primary");
    actions.append(this.button("取消", () => dialog.close()), save);
    form.append(element("h2", title), body, alert, actions);
    form.addEventListener("submit", event => { event.preventDefault(); this.run(submit); });
    dialog.addEventListener("cancel", event => { if (this.busy) event.preventDefault(); });
    dialog.addEventListener("close", () => { dialog.remove(); if (this.dialog === dialog) this.dialog = undefined; });
    dialog.append(form);
    this.append(dialog);
    this.dialog = dialog;
    return { dialog, body, save };
  }

  private createRepo() {
    const name = input("仓库名称");
    name.field.pattern = "[a-z0-9][a-z0-9_\\-]{0,62}";
    name.field.spellcheck = false;
    const nameRules = element("ul", "", "name-rules");
    nameRules.id = "repository-name-rules";
    nameRules.setAttribute("aria-live", "polite");
    name.field.setAttribute("aria-describedby", nameRules.id);
    const rules = [
      { label: "长度为 1–63 个字符", test: (value: string) => value.length >= 1 && value.length <= 63 },
      { label: "以小写英文字母或数字开头", test: (value: string) => /^[a-z0-9]/.test(value) },
      { label: "仅含小写英文字母、数字、下划线或连字符", test: (value: string) => !/[^a-z0-9_-]/.test(value) },
    ].map(rule => ({ ...rule, row: element("li", rule.label) }));
    nameRules.append(...rules.map(rule => rule.row));
    const updateNameRules = () => {
      const value = name.field.value;
      name.field.setAttribute("aria-invalid", String(!name.field.validity.valid));
      for (const { label, test, row } of rules) {
        const valid = test(value);
        if (row.dataset.valid === String(valid)) continue;
        row.dataset.valid = String(valid);
        row.textContent = `${valid ? "✓ 通过" : "✕ 未通过"} · ${label}`;
      }
    };
    name.field.addEventListener("input", updateNameRules);
    name.field.addEventListener("invalid", updateNameRules);
    const branch = input("默认分支", "text", "main");
    const initialize = input("用 README 初始化", "checkbox", "", false);
    initialize.wrapper.classList.add("checkbox");
    const modal = this.modal("新建仓库", "创建", async () => {
      const repo = await result(api.POST("/api/v1/repos", { body: {
        name: name.field.value, defaultBranch: branch.field.value, initialize: initialize.field.checked,
      } }));
      this.repos.push(repo);
      this.repos.sort((a, b) => a.name.localeCompare(b.name));
      modal.dialog.close();
      await this.open(repo);
    });
    const nameGroup = element("div");
    nameGroup.append(name.wrapper, nameRules);
    modal.body.append(nameGroup, branch.wrapper, initialize.wrapper);
    modal.dialog.showModal();
  }

  private removeRepo() {
    const modal = this.modal(`删除 ${this.repo!.name}？`, "删除仓库", async () => {
      await result(api.DELETE("/api/v1/repos/{id}", { params: { path: this.params } }));
      modal.dialog.close();
      this.home();
      await this.list();
    });
    modal.body.append(element("p", "删除后将无法通过网页或 Git 访问。"));
    modal.dialog.showModal();
  }

  private createRef(tab: "branches" | "tags") {
    const name = input("名称");
    const sha = input("提交 SHA", "text", this.tree?.head ?? "");
    const route = tab === "branches" ? "/api/v1/repos/{id}/branches" : "/api/v1/repos/{id}/tags";
    const modal = this.modal(tab === "branches" ? "新建分支" : "新建标签", "创建", async () => {
      await result(api.POST(route, { params: { path: this.params }, body: { name: name.field.value, sha: sha.field.value } }));
      modal.dialog.close();
      await this.showRefs(tab);
    });
    modal.body.append(name.wrapper, sha.wrapper);
    modal.dialog.showModal();
  }

  private removeRef(tab: "branches" | "tags", ref: Ref) {
    const route = tab === "branches" ? "/api/v1/repos/{id}/branches" : "/api/v1/repos/{id}/tags";
    const modal = this.modal(`删除 ${ref.name}？`, "删除引用", async () => {
      await result(api.DELETE(route, { params: { path: this.params, query: { name: ref.name } } }));
      if (ref.name === this.tag) this.tag = undefined;
      if (tab === "branches" && ref.name === this.branch) this.branch = this.repo!.defaultBranch;
      modal.dialog.close();
      await this.showRefs(tab);
    });
    modal.dialog.showModal();
  }

  private editFile(remove = false, existing: { path: string; blob: Blob } | null | undefined = this.file) {
    const editing = existing != null;
    const path = input("文件路径", "text", editing ? existing!.path : this.directory ? this.directory + "/" : "");
    path.field.readOnly = editing;
    const message = input("提交说明");
    message.field.maxLength = 4096;
    const expectedHead = editing ? existing!.blob.head : this.tree?.head ?? null;
    const editorHost = element("div", "", "editor");
    let editor: import("codemirror").EditorView | undefined;
    const modal = this.modal(remove ? "删除文件" : editing ? "编辑文件" : "新建文件", "提交", async () => {
      const body = { path: path.field.value, message: message.field.value, expectedHead,
        branch: this.branch, content: remove ? null : editor!.state.doc.toString() };
      const options = { params: { path: this.params }, body };
      await result(remove ? api.DELETE("/api/v1/repos/{id}/files", options) : api.PUT("/api/v1/repos/{id}/files", options));
      if (remove) {
        try { await this.browse(this.directory); }
        catch (error) {
          if (!(error instanceof ApiError) || error.status !== 404) throw error;
          await this.browse();
        }
      } else {
        await this.browse(path.field.value.split("/").slice(0, -1).join("/"),
          this.branch, this.tag, path.field.value);
      }
      modal.dialog.close();
    }, !remove);
    modal.body.append(path.wrapper);
    if (!remove) {
      modal.body.append(editorHost);
      modal.save.disabled = true;
      void import("codemirror").then(({ EditorView, basicSetup }) => {
        if (!modal.dialog.open) return;
        editor = new EditorView({
          doc: editing ? existing?.blob.content ?? "" : "",
          extensions: [basicSetup, EditorView.contentAttributes.of({ "aria-label": "文件内容" })],
          parent: editorHost,
        });
        modal.save.disabled = false;
      }).catch(() => { editorHost.textContent = "编辑器加载失败"; });
      modal.dialog.addEventListener("close", () => editor?.destroy());
    }
    modal.body.append(message.wrapper);
    modal.dialog.showModal();
  }

  private tokenDialog() {
    const modal = this.modal("Git 访问令牌", "生成令牌", async () => {
      const token = await result(api.POST("/api/v1/git-token"));
      const copy = input("令牌", "text", token.token);
      copy.field.readOnly = true;
      copy.field.addEventListener("focus", () => copy.field.select());
      modal.body.replaceChildren(copy.wrapper, element("p", `用户名使用 ${this.user}，密码使用此令牌。`),
        element("p", `有效期至 ${new Date(token.expiresAt).toLocaleString()}。关闭后无法再次查看。`));
      modal.save.remove();
    });
    modal.body.append(element("p", "生成后，之前的 Git 令牌立即失效。"));
    modal.dialog.showModal();
  }
}

customElements.define("mingo-forge", Forge);
