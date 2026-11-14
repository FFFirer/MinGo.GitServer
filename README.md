# MinGo.GitServer

使用 ASP.NET Core 10 和 Git Smart HTTP 的私有 Git 托管服务。支持单管理员、仓库管理、分支与标签、提交历史与差异，以及手机浏览器中的单文件提交。

## 本地运行

需要 .NET 10 SDK、带 `git-http-backend` 的 Git、Node.js 22.15 或以上版本。以下命令在仓库根目录使用 PowerShell。

```powershell
npm.cmd --prefix src/Web ci
npm.cmd --prefix src/Web run build
dotnet build MinGo.GitServer.slnx
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Forge__AdminPassword = '<至少 12 个字符的管理员密码>'
Push-Location src/Server
dotnet bin/Debug/net10.0/MinGo.GitServer.dll --urls http://127.0.0.1:5080
Pop-Location
```

打开 `http://127.0.0.1:5080`，默认用户为 `admin`。首次启动把密码哈希写入 SQLite。重新启动时可移除初始化密码。开发前端时运行 `npm.cmd --prefix src/Web run dev`，Vite 将请求代理到 5080。

在网页创建仓库并生成 Git 令牌。复制克隆地址，Git 提示凭据时使用管理员用户名和该令牌。新令牌立即吊销旧令牌，有效期 90 天。网页提交后使用 `git pull --ff-only` 拉取更新。

## 验证与生成客户端

```powershell
node tests/protocol.mjs
node tests/protocol.mjs --generate-openapi
npm.cmd --prefix src/Web run generate
npm.cmd --prefix src/Web run build
Push-Location src/Web
npx.cmd playwright install chromium
npm.cmd run test:e2e
Pop-Location
```

协议测试启动独立的临时应用，覆盖真实 Git 传输、API、并发提交和离线恢复。浏览器测试使用独立临时数据并要求提前构建前后端。OpenAPI 开发地址为 `/openapi/v1.json`，生产环境需要登录。

反代浏览器测试还需要 `openssl`（用于生成临时 HTTPS 证书），并使用本机回环端口 5089–5091。可通过 `MINGO_TEST_CONFIGURATION=Release` 选择 Release 构建，通过 `MINGO_TEST_BROWSER_CHANNEL=msedge` 使用已安装的 Microsoft Edge。

## Rootless Podman

GitHub Actions 在 PR 中验证镜像构建。推送到默认分支（`main` 或 `master`）时发布 `latest`，推送 `v1.0.0` 一类的版本标签时发布同名镜像标签。每次发布还带有 `sha-<完整提交 SHA>`。镜像地址为 `ghcr.io/<owner>/<repo>`，路径统一小写。工作流使用仓库的 `GITHUB_TOKEN`。拉取私有镜像时需先登录 GHCR。

```sh
podman pull ghcr.io/<owner>/<repo>:latest
```

准备受 Git 客户端信任的 PFX 证书，并确保容器内应用用户可以读取证书。设置环境变量 `MINGO_TLS_PFX` 为证书绝对路径，`MINGO_TLS_PASSWORD` 为证书密码，首次启动时设置 `MINGO_ADMIN_PASSWORD`。默认发布到 `127.0.0.1:8443`，远端访问时设置 `MINGO_BIND_ADDRESS` 为计划监听的宿主地址。

```sh
podman compose -f deploy/compose.yaml up --build -d
podman compose -f deploy/compose.yaml ps
```

## HTTPS 反向代理

`deploy/compose.proxy.yaml` 在 Nginx 终止 HTTPS，并通过专用容器网络将站点根路径 `/` 的 Web、API 和 Git 请求转给应用的 HTTP 端口。只有 Nginx 的端口发布到宿主机。将 `deploy/nginx.conf` 和 Compose 中的示例域名替换为实际域名，并准备覆盖这些域名的证书。设置 `MINGO_PROXY_CERT_DIR` 为包含 `fullchain.pem` 和 `privkey.pem` 的目录路径，首次启动时设置 `MINGO_ADMIN_PASSWORD`。

```sh
docker compose -f deploy/compose.proxy.yaml up --build -d
```

使用 `https://git.example.com/` 或 `https://git.internal.example.com:8443/` 打开页面时，复制的 Git 克隆地址会使用对应的访问 Host 和端口。示例使用 `172.30.240.2` 作为代理固定地址。如果网段冲突，须同步调整 Compose 的代理地址、子网和 `Forge__TrustedProxy`。

使用已有代理时，配置 `Forge__TrustedProxy` 为应用实际看到的直接代理 IP，并在代理校验外部 Host 后覆写 `X-Forwarded-Proto` 和 `X-Forwarded-For`。代理可以保留原始 `Host`，或覆写 `X-Forwarded-Host` 为校验过的完整外部 Host（包括非默认端口）。保留原始 `Host` 时应清除客户端提供的 `X-Forwarded-Host`。应用仅处理受信代理提供的一跳转发头。可用 `Forge__ForwardedAllowedHosts__0`、`Forge__ForwardedAllowedHosts__1` 等限制转发域名，值仅填写域名。代理需允许 Git 流式请求和 256 MiB 请求体。

## 备份与恢复

停止应用后，对整个 `/data` 命名卷做快照。Podman 可用 `podman volume export <实际卷名> --output forge-backup.tar`。用 `podman volume import <新建空卷名> forge-backup.tar` 恢复，保留应用用户的读写权限，再让 Compose 挂载恢复后的卷。TLS 证书和部署配置随环境单独备份。启动后验证管理员登录、仓库列表及 Git clone。

已删除仓库保存在数据卷的 `trash`。确认备份可恢复后，可在停机维护时清理该目录。单个数据卷由一个实例独占。
