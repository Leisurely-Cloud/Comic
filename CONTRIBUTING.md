# 贡献指南

感谢你对本项目的关注！

## 开始参与

1. Fork 本仓库
2. 克隆你的 Fork：`git clone https://github.com/your-username/Comic.git`
3. 创建分支：`git checkout -b feature/your-feature`
4. 进行修改
5. 推送并发起 Pull Request

## 报告 Bug

请在 Issue 中包含：

- 问题的清晰描述
- 复现步骤
- 期望行为与实际行为
- 你的环境信息（Windows 版本、Python 版本、.NET 版本）

## 提交要求

- 每个 commit 聚焦单一变更，附清晰描述
- 遵循现有代码风格（见下方）
- 新功能尽量附带测试
- 行为变更时同步更新文档

## 代码规范

### 后端 (Python)

- 所有函数签名添加类型注解
- 每个模块顶部添加 `from __future__ import annotations`
- 文档字符串混用中文（面向用户）和英文（基础设施）
- 测试使用 `unittest.TestCase`（不使用 pytest）
- 面向用户的错误信息使用中文

### 前端 (C# / WinUI 3)

- 使用 CommunityToolkit.Mvvm 源生成器（`[ObservableProperty]`、`[RelayCommand]`）
- XAML 中仅使用 `x:Bind`（不使用 `{Binding}`）
- 最小化 code-behind — 逻辑放在 ViewModel 中
- 类默认 `sealed`，MVVM 需要时使用 `partial`
- 面向用户的字符串使用中文

## 添加新站点适配器

1. 创建 `app/backend/support/{站点名}.py`
2. 继承 `support/base.py` 中的 `BaseSiteAdapter` 并实现接口
3. 在 `app/backend/support/site_adapters.py` 的 `SITE_ADAPTERS` 字典中注册
4. 在 `app/backend/tests/` 中添加测试

参考现有适配器（`baozimh.py`、`mangacopy.py`、`manhuagui.py`）。

## 许可证

贡献即表示你的代码将遵循 [MIT 许可证](./LICENSE)。
