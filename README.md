# TokenVault · 令牌与密钥管家

本机加密保存 API Key、账号密码、数据库连接串的 Windows 桌面小工具（WPF / .NET 8）。
主密码 → AES-256-GCM + PBKDF2-SHA256 加密落盘，数据只存在本机；附带 MiMo / DeepSeek / 阿里云百炼的余量查询。

## 功能

- **加密保险库**：`%LOCALAPPDATA%\TokenVault\vault.json`，AES-256-GCM 认证加密，错误主密码直接被 GCM 拒绝
- **凭据管理**：名称 / 类别 / API Key / 账号 / 密码 / 地址 / 备注 / 任意自定义字段；密码与 Key 分开掩码、分开复制
- **临时区（7 天缓冲）**：修改保留旧版本、删除进临时区，7 天内可随时恢复，到期自动彻底删除；可提前手动清除
- **余量查询**：MiMo Token Plan（tp- key）、DeepSeek 余额（sk- key）、阿里云 Token Plan 额度（**需要 AccessKey**：官方 OpenAPI `GetSubscriptionStats` / `ListSubscriptionSharedPackages` 采用 ACS3 签名，不支持 `sk-`/`sk-sp-` 这类 API Key；把 AK 填在「账号」、SK 填在「密码」，类别选「阿里云 AccessKey（AK/SK）」）
- **消耗估算**：每次查询写入本地快照，按快照增量估算今日 / 本周 / 本月消耗
- **安全细节**：剪贴板 30 秒后自动清空（只清自己写入的值）、空闲 N 分钟自动锁定、锁定即清剪贴板

## 开发

```bash
dotnet build -c Release                 # 构建
dotnet run -c Release                   # 运行（首次会要求设置主密码）
```

### 自检与冒烟测试

```bash
# 逻辑自检：加密往返 / CRUD / 临时区 7 天 / 消耗估算 / 百炼 JSON 解析 / 空闲与剪贴板判定
./bin/Release/net8.0-windows/TokenVault.exe --selftest

# 冒烟测试：临时保险库 → 解锁 → 主窗口三个页面渲染 + 剪贴板与空闲链路
./bin/Release/net8.0-windows/TokenVault.exe --smoketest
```

自检报告同时写入 `%LOCALAPPDATA%\TokenVault\selftest-report.txt`；退出码 0 = 全部通过。

## 打包

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
"/c/Users/qq/AppData/Local/Programs/Inno Setup 6/ISCC.exe" setup.iss
# 产物：dist/TokenVault-Setup-<version>.exe
```

安装包用仓库里的 `TokenVault-Dev.pfx` 自签名（开发用途，根证书不受信属正常现象）。

## 数据与隐私

- 所有凭据仅保存在本机 `%LOCALAPPDATA%\TokenVault\`，不上传任何服务器
- 网络请求只发生在余量查询时，直连各厂商官方接口
- `app.log` 只记录启动 / 解锁成功与失败，不含密钥内容
