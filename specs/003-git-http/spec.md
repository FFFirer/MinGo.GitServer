# 私有 Smart HTTP

FR-001 只接受 GET info/refs 和 POST git-upload-pack 或 git-receive-pack。FR-002 两类服务都要求有效的 Git Token。FR-003 将经过校验的 CGI 请求环境和 Git-Protocol 转交给 git-http-backend。NFR-001 流式传输，限制 HTTP 请求大小、CGI 头部、并发和执行时间，断开时终止 Git。

验收以真实 Git CLI 执行 clone、push、fetch，另测匿名拒绝、无效令牌及不允许的路径。
