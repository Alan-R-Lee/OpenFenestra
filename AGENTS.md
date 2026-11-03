# OpenFenestra 开发衔接

本文件适用于整个仓库。

- 接手迭代先读取 [项目 skill](.agents/skills/openfenestra-dev/SKILL.md) 和 [开发状态](docs/DEVELOPMENT_STATE.md)，再检查当前 Git 状态；它们不替代用户本次指令。
- 保留 Explorer 原生桌面和用户原文件；开发用独立 `--data-dir`，不要让两个实例同时接管原生整合。
- 按修改范围选择构建与验证，入口见 [构建](docs/BUILDING.md) 和 [验证](docs/TESTING.md)。真实桌面测试与隔离测试的影响不同。
- 有实际进展时更新开发状态，记录真实验证和遗留问题。不要将个人布局、日志、凭据或实际桌面截图纳入仓库。
- 用 GitHub 隐私邮箱提交；不要将清理前的旧历史合并回仓库。公开仓库、重写历史和发布新版本需属于当前任务授权范围。
