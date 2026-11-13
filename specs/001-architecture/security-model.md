# 认证与仓库隔离

单管理员拥有所有私有仓库。Web 使用 Cookie 和 ASP.NET Core 防伪令牌。Git 使用 Basic 传送管理员用户名和独立随机 Token，SQLite 只保存 Token 摘要及过期时间。创建新 Token 同时吊销旧 Token。生产入口须由可信反向代理终止 HTTPS。

所有仓库物理路径由 SQLite 中的 UUID 生成。URL 名称只用于查元数据，禁止用作物理路径。拒绝 dumb HTTP。仓库删除先写墓碑，再移到同一数据卷的隔离目录。启动时恢复未完成的移动。单实例独占数据目录。

网页编辑使用独立临时 index 和 update-ref 的旧值条件。默认分支的删除规则同样适用于 Git push。
