## ADDED Requirements

### Requirement: 通过可信反向代理访问

应用 SHALL 支持反向代理在站点根路径终止 HTTPS 并通过 HTTP 转发请求。应用 SHALL 仅接受已配置的直接代理提供的一跳转发头。外部 Host SHALL 包含请求中的非默认端口。

#### Scenario: 代理保留外部 Host

- **WHEN** 可信代理保留外部 `Host` 并转发 HTTPS 协议
- **THEN** 应用将请求视为 HTTPS，并以保留的 `Host` 处理请求

#### Scenario: 代理改写上游 Host

- **WHEN** 可信代理使用内部 `Host` 连接上游，并覆写 `X-Forwarded-Host` 为允许的外部 Host
- **THEN** 应用使用转发的外部 Host，包括其非默认端口

#### Scenario: 未受信的转发头

- **WHEN** 未受信来源发送 `X-Forwarded-Host` 或 `X-Forwarded-Proto`，或可信代理提供白名单以外的转发 Host
- **THEN** 被拒绝的转发值不参与确定请求的 Host 或协议

### Requirement: 克隆地址使用访问 Host

仓库页面生成的完整 Git 克隆地址 SHALL 使用当前页面访问的 Host，包括非默认端口。API SHALL 继续提供 `clonePath`。

#### Scenario: 同一仓库经两个入口访问

- **WHEN** 管理员分别通过 `https://git.example.com/` 和 `https://git.internal.example.com:8443/` 登录并复制同一仓库的克隆地址
- **THEN** 两个地址分别包含 `git.example.com` 和 `git.internal.example.com:8443`，并且均可通过现有 Git 认证完成克隆

#### Scenario: 直接通过 HTTPS 访问

- **WHEN** 管理员通过直连 HTTPS 打开仓库并复制克隆地址
- **THEN** 克隆地址使用该直连入口的 Host 和端口
