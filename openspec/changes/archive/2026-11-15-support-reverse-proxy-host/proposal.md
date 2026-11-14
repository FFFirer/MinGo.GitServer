## Why

MinGo.GitServer 已支持可信反向代理转发 HTTPS 协议，浏览器也会用当前页面的来源拼接克隆地址。当前部署示例仍要求容器配置 TLS 证书，且缺少经多个外部 Host 访问时的端到端验收。

## What Changes

- 明确由反向代理终止 TLS 的部署配置，并支持代理保留或转发外部 Host。
- 同一实例经不同外部 Host 访问时，页面的 Git 克隆地址使用当前请求的外部 Host 和非默认端口。
- 部署范围为站点根路径 `/`。

## Capabilities

### New Capabilities

- `reverse-proxy-host`：可信反向代理部署与按访问 Host 生成克隆地址。

### Modified Capabilities

无。

## Impact

涉及 ASP.NET Core 转发头配置、容器部署说明和反代集成验证。沿用 Web 页面与 API 之间的 `clonePath` 契约。
