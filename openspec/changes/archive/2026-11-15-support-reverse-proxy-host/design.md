## Context

服务通过 `Forge__TrustedProxy` 信任一个直接连接的代理 IP，现有中间件处理 `X-Forwarded-For` 和 `X-Forwarded-Proto`。Web 页面用 `location.origin` 解析 `clonePath`，创建仓库接口的 `Location` 由客户端基于当前访问来源解析。

## Goals / Non-Goals

**Goals**

- 代理终止 HTTPS、通过 HTTP 连接上游时，登录、仓库页面和 Git Smart HTTP 可用。
- 外部 Host 按请求确定，保留非默认端口。
- 反代和已有的直连 HTTPS 部署都可使用。

**Non-Goals**

- 子路径部署、路径前缀转发，以及新增绝对克隆地址 API 字段。

## Decisions

1. 页面继续用 `location.origin` 解析 `clonePath`。同一实例的域名和端口由当前页面确定，API 继续返回路径。
2. 代理可直接保留客户端访问的 `Host`。上游 `Host` 被改写时，在现有转发头中间件中接收 `X-Forwarded-Host`，将其应用到 `Request.Host`。沿用 `Forge__TrustedProxy` 和一跳转发限制。由入口代理验证外部 Host 并覆写转发头，应用的转发 Host 白名单可配置，匹配域名时忽略端口，构造请求 Host 时保留端口。
3. 提供由代理管理证书的部署示例。应用的 HTTP 监听只暴露给所在代理可达的网络，`Forge__TrustedProxy` 配置为应用实际看到的代理 IP。已有 HTTPS 部署继续使用其证书和监听配置。

## Risks / Trade-offs

- 容器网络可能改变应用看到的代理源 IP。部署说明应明确如何选择稳定地址和确认实际来源，并验证 HTTPS 检查能通过。
- 代理保留或写入 Host 时可能丢失非默认端口。示例应使用带端口的完整外部 Host，并在真实代理验收中覆盖该情况。
