# 随芯调社区服务端

这里是随芯调的社区 API（ASP.NET Core Minimal API）。第一期只提供经过版本化的只读社区目录，避免未经认证的请求直接写入或发布调度策略。

## 启动

在仓库根目录执行：

```powershell
dotnet run --project .\服务端\src\SuiXinTiao.Community.Api\SuiXinTiao.Community.Api.csproj --launch-profile SuiXinTiao.Community.Api
```

默认地址：`http://localhost:5180`

## API v1

- `GET /api/v1/health`：服务健康检查。
- `GET /api/v1/community/manifest`：目录数量和 API 版本。
- `GET /api/v1/community/profiles`：应用 Profile 目录；支持 `q`、`source` 查询参数。
- `GET /api/v1/community/profiles/{entryId}`：读取单个应用 Profile。
- `GET /api/v1/community/device-strategies`：机型策略包目录；支持 `q`、`source` 查询参数。
- `GET /api/v1/community/device-strategies/{entryId}`：读取单个机型策略包。

当前 `data/` 里的 JSON 是开发样例，不是用户运行时配置。后续再增加登录、审核队列、签名、版本兼容性、下载统计和社区提交接口。
