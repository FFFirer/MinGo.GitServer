# MinGo.GitServer

使用 ASP.NET Core 10 和 Git Smart HTTP 的私有 Git 托管服务。支持单管理员、仓库管理、分支与标签、提交历史与差异，以及手机浏览器中的单文件提交。

## 本地运行

需要 .NET 10 SDK、带 `git-http-backend` 的 Git、Node.js 22.15 或以上版本。以下命令在仓库根目录使用 PowerShell。

```powershell
npm.cmd --prefix src/Web ci
dotnet user-secrets set "Forge:AdminPassword" "<至少 12 个字符的管理员密码>" --project src/Server
dotnet run --project src/Server
```

打开 `http://127.0.0.1:5080`，默认用户为 `admin`。启动配置使用 `Development`。首次访问网页时，Vite.AspNetCore 自动从 `src/Web` 启动 Vite（内部端口 5173），并通过 5080 提供网页和热更新。API、Git 和健康检查仍由 ASP.NET Core 处理。

User Secrets 存在本机用户目录。开发环境中，同名配置按命令行参数、环境变量、User Secrets、环境专用 JSON、通用 JSON 的顺序取值。使用 PowerShell 环境变量时，在启动应用的同一终端设置 `$env:Forge__AdminPassword = '<密码>'`，双下划线对应配置键中的冒号。首次启动把密码哈希写入 SQLite，之后可移除初始化密码。已有数据目录的管理员密码由 SQLite 决定。

在网页创建仓库并生成 Git 令牌。复制克隆地址，Git 提示凭据时使用管理员用户名和该令牌。新令牌立即吊销旧令牌，有效期 90 天。网页提交后使用 `git pull --ff-only` 拉取更新。

## 验证与生成客户端

```powershell
dotnet build MinGo.GitServer.slnx
node tests/protocol.mjs
node tests/protocol.mjs --generate-openapi
npm.cmd --prefix src/Web run generate
npm.cmd --prefix src/Web run build
Push-Location src/Web
npx.cmd playwright install chromium
npm.cmd run test:e2e
Pop-Location
```

协议测试启动独立的临时应用，覆盖真实 Git 传输、API、并发提交和离线恢复。浏览器测试使用独立临时数据和预构建的静态前端，因此需要提前构建前后端。OpenAPI 开发地址为 `/openapi/v1.json`，生产环境需要登录。

反代浏览器测试还需要 `openssl`（用于生成临时 HTTPS 证书），并使用本机回环端口 5089–5091。可通过 `MINGO_TEST_CONFIGURATION=Release` 选择 Release 构建，通过 `MINGO_TEST_BROWSER_CHANNEL=msedge` 使用已安装的 Microsoft Edge。

## Release 构建

先运行 `npm.cmd --prefix src/Web ci` 和 `npm.cmd --prefix src/Web run build`，再运行 `dotnet publish src/Server/MinGo.GitServer.csproj -c Release -o <发布目录>`。Vite 将前端产物写入 `src/Server/wwwroot`，发布时一起打包，由 ASP.NET Core 提供静态文件。容器构建在镜像内完成这些步骤，生产运行使用部署环境变量。

## Docker Compose

根目录的 `docker-compose.yaml` 使用已发布的 GHCR 镜像。准备浏览器和 Git 客户端信任的 PFX 证书，并确保容器内的 `app` 用户可读取该文件。在仓库根目录创建 `.env`，替换以下示例值。

```dotenv
MINGO_TLS_PFX=./certs/forge.pfx
MINGO_TLS_PASSWORD='替换为 PFX 密码'
MINGO_ADMIN_PASSWORD='替换为至少 12 个字符的管理员密码'
```

私有镜像需先用有包读取权限的账号执行 `docker login ghcr.io`。

```sh
docker compose pull
docker compose up -d
docker compose ps
```

使用证书对应的主机名访问 HTTPS 端口 8443，默认用户是 `admin`。默认只监听宿主机回环地址，可用 `MINGO_BIND_ADDRESS` 和 `MINGO_HTTPS_PORT` 调整。使用 `MINGO_IMAGE_TAG` 可固定版本。SQLite 和 Git 仓库保存在 `forge-data` 卷。首次启动成功后，可从 `.env` 移除管理员初始化密码并重新执行 `docker compose up -d`。

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
