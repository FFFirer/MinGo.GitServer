## Context

`GitRepository.Create` 创建裸库，把初始分支名同时写入 Git `HEAD` 和 SQLite。它不创建提交。`GitTransport` 将推送交给 `git http-backend`。前端从仓库元数据读取 `defaultBranch`，因此首次只推送 `master` 时，预设的 `main` 仍会被选中。

Gitea 的创建流程提供 `AutoInit`，可在创建阶段生成 README 提交。它也会在空仓库收到新分支时同步分支数据。GitLab 提供 `initialize_with_readme`，并支持用已有本地仓库通过 Git 推送导入代码。参考 [Gitea 创建逻辑](https://github.com/go-gitea/gitea/blob/main/services/repository/create.go)、[Gitea 推送逻辑](https://github.com/go-gitea/gitea/blob/main/services/repository/push.go)、[GitLab 默认分支文档](https://docs.gitlab.com/user/project/repository/branches/default/) 和 [GitLab 推送创建项目文档](https://docs.gitlab.com/topics/git/project/)。

## Goals / Non-Goals

**Goals**

- 让用户选择用 README 创建首个提交，或将已有本地历史直接推送到空仓库。
- 让 Git `HEAD`、仓库 API 和网页使用同一个默认分支。

**Non-Goals**

- README 模板、许可证、`.gitignore` 及仓库创建后的默认分支切换。
- 自动改写升级前已有仓库的默认分支。

## Decisions

1. 创建 API 接受可选布尔字段 `initialize`，缺省为 `false`。保留 `defaultBranch` 的现有校验和缺省值 `main`。立即初始化时沿用仓库现有 Git 提交机制，在指定分支生成一条包含最小 README 的提交。创建失败沿用仓库隔离清理流程，成功后才发布仓库记录。
2. 对新建空仓库持久化待初始化状态。初始分支名是首次推送指引和多分支择优时的首选。以 Git 实际接受并可解析为提交的 `refs/heads/*` 为依据。首选分支在本次推送中创建时选它，其余情况按完整引用名的序数排序取首项。拒绝的引用、删除、空操作与仅标签推送不完成初始化。选定后，后续推送不会改变默认分支。
3. 复用仓库级协调机制，将待初始化仓库的引用写入、分支选择与删除互斥。保留 Web 新建文件入口，经现有写文件流程成功创建首个分支时同样完成初始化。推送最终响应结束前完成默认分支同步。Git 引用已写入但元数据更新中断时，重启或下次访问应在同一协调机制下恢复。恢复时优先使用已指向有效分支的 Git `HEAD`，否则按同一选择规则从已写入的分支恢复。只有 Git `HEAD` 和 SQLite 默认分支同步后才清除待初始化状态。
4. API 返回 `isInitialized`。待初始化仓库的文件首页展示克隆地址、已有仓库首次推送的指引和网页新建文件入口。标签页仍可访问已推送的标签。用户刷新或重新打开仓库后，使用 API 返回的实际默认分支加载文件和提交。

## Risks / Trade-offs

- Git 引用与 SQLite 横跨两个存储层，中断后可能需要重试同步。持久化待初始化状态并在后续访问前恢复，可避免分支已存在但首页长期指向空目录。
- 多分支首推时，Git 服务无法知道用户本地当前检出的分支。首选名称和序数排序提供可重复验证的结果。
