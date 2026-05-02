# Contributing to Comic Downloader / 贡献指南

Thank you for your interest in contributing! / 感谢你对本项目的关注！

## Getting Started / 开始参与

1. Fork this repository
2. Clone your fork: `git clone https://github.com/your-username/Comic.git`
3. Create a branch: `git checkout -b feature/your-feature`
4. Make your changes
5. Push and open a Pull Request

## Reporting Bugs / 报告 Bug

Please open an issue with:

- A clear description of the problem
- Steps to reproduce
- Expected vs actual behavior
- Your environment (Windows version, Python version, .NET version)

请在 Issue 中包含：

- 问题的清晰描述
- 复现步骤
- 期望行为与实际行为
- 你的环境信息（Windows 版本、Python 版本、.NET 版本）

## Submitting Changes / 提交变更

- Keep commits focused and well-described
- Follow existing code style (see below)
- Add tests for new functionality when practical
- Update documentation if behavior changes

提交要求：

- 每个 commit 聚焦单一变更，附清晰描述
- 遵循现有代码风格（见下方）
- 新功能尽量附带测试
- 行为变更时同步更新文档

## Code Style / 代码规范

### Backend (Python)

- Type hints on all function signatures
- `from __future__ import annotations` at the top of every module
- Docstrings mix Chinese (user-facing) and English (infrastructure)
- Tests use `unittest.TestCase` (no pytest)
- Error messages for users are in Chinese

### Frontend (C# / WinUI 3)

- CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`)
- `x:Bind` only in XAML (never `{Binding}`)
- Minimal code-behind — logic goes in ViewModels
- Classes are `sealed` unless `partial` for MVVM
- User-facing strings in Chinese

## Adding a New Site Adapter / 添加新站点适配器

1. Create `app/backend/support/{site_name}.py`
2. Implement `BaseSiteAdapter` from `support/base.py`
3. Register in `app/backend/support/site_adapters.py` (`SITE_ADAPTERS` dict)
4. Add tests in `app/backend/tests/`

See existing adapters (`baozimh.py`, `mangacopy.py`, `manhuagui.py`) for reference.

## License / 许可证

By contributing, you agree that your contributions will be licensed under the MIT License.

贡献即表示你的代码将遵循 MIT 许可证。
