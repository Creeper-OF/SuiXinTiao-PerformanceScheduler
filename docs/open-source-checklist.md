# 公开仓库维护清单

这份清单用于提醒维护者：随芯调已经是公开仓库，任何提交都默认会被外部看到。

## 可以公开提交

- `src/` 源码。
- `tests/` 测试。
- `profiles/` 中经过脱敏的示例 Profile。
- `community/` 中的示例目录和示例策略包。
- `locales/` 语言包。
- `docs/` 文档。
- 解决方案文件、README、LICENSE、NOTICE、贡献说明和安全说明。

## 不应公开提交

- 本机运行数据：`runtime/`、`logs/`、`appearance/`。
- 构建产物：`bin/`、`obj/`、`artifacts/`、`publish/`、`release/`。
- 数据库、日志和崩溃转储：`*.db`、`*.sqlite`、`*.log`、`*.dmp`。
- `.env`、证书、密钥、发布配置、私有截图。
- 用户导出的迁移包。
- 带有个人硬件信息、设备序列号、用户名路径或不可公开窗口标题的配置包。

## 提交前检查

```powershell
git status -sb
git diff --check
git diff --name-only
rg -n "token|secret|password|api_key|client_secret|access_token|private key" -S .
```

关键词扫描可能命中文档中的示例说明，需要人工判断。

## 文档同步

以下改动必须同步文档：

- 调度事务顺序变化。
- 回滚状态格式变化。
- Profile 字段语义变化。
- 导入、导出、社区策略规则变化。
- 默认安全策略变化。
- 用户可见的功能入口变化。

## 发布态度

公开仓库里的功能说明要克制。不要承诺无法验证的性能提升，不要使用“突破限制”“强制解锁”“极限优化”等容易误导用户的描述。
