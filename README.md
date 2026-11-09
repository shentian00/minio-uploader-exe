# minio-uploader

一个用于 Windows 的照片自动上传工具：把本地目录里的照片持续上传到 MinIO（S3 兼容对象存储），上传成功后按规则删除本地文件。

- 单个 EXE，仅依赖 Windows 自带的 .NET Framework 4.x（Windows 10 / 11 均自带），无需安装任何额外软件，不依赖 PowerShell。
- 直接按 S3 标准的 Signature V4 签名请求，不引入任何第三方 SDK。
- 通过 Windows 计划任务实现开机自启和看门狗自动重启。

## 功能

- 定时扫描照片目录（含子目录），默认只处理 `.jpg` / `.jpeg` / `.bmp`。
- 文件最后修改超过指定秒数才上传，避免上传尚未写完的文件。
- 多线程并发上传，带 `Content-MD5`，由服务端校验内容完整性。
- 可配置本地保留天数：当天照片先保留，次日 0 点后核对 MinIO 的大小与 MD5，一致才删除本地。
- 单张照片连续失败达到上限后移入失败目录，并记录到 CSV。
- MinIO 不可用时自动暂停、恢复后自动继续，不计入失败次数。
- 看门狗定时检查，进程异常退出时自动重启并写重启日志。
- 旧日志按天压缩归档，过期自动清理。

## 从源码构建

源码为单文件 `src/Program.cs`，使用 C# 5 语法，用 Windows 自带的 C# 编译器 `csc.exe` 编译即可。

```cmd
cd src
build.cmd
```

编译产物 `minio-uploader.exe` 会生成在上一级目录。构建不需要安装 Visual Studio。

## 配置

复制 `config.example.json` 为 `config.json`，按实际情况修改：

| 配置项 | 说明 |
| --- | --- |
| `Endpoint` | MinIO 的 API 地址，使用 9000 端口，例如 `http://your-minio-host:9000` |
| `AccessKey` / `SecretKey` | 上传专用账号的用户名 / 密码（建议单独建账号，不要用管理员账号） |
| `Bucket` | 目标桶名 |
| `ObjectPrefix` | 桶内目录前缀；留空则直接放在桶根目录 |
| `SourceDir` | 本地照片目录 |
| `FailedDir` | 失败照片目录（不能位于 `SourceDir` 之内） |
| `Extensions` | 要上传的文件类型，默认 `[".jpg", ".jpeg", ".bmp"]` |
| `UploadThreads` | 并发上传线程数，默认 4（1~32） |
| `ScanIntervalSeconds` | 扫描间隔秒数，默认 10 |
| `StableSeconds` | 文件最后修改超过多少秒才上传，默认 30 |
| `MaxFailures` | 单张照片最多失败次数，默认 5 |
| `RetryDelaySeconds` | 单张照片失败后重试间隔秒数 |
| `RequestTimeoutSeconds` | 单次请求超时秒数，默认 120 |
| `KeepLocalDays` | 上传成功后本地保留天数：0=立即删除，1=保留当天，次日核对后删除，以此类推 |
| `CleanEmptyDirs` | 是否删除传空的旧目录，默认 false |
| `EmptyDirMinAgeHours` | `CleanEmptyDirs` 为 true 时，目录创建超过多少小时才允许删除，默认 48 |
| `LogRetentionDays` | 压缩日志保留天数，默认 30；0 表示永久保留 |
| `UploadFromDate` | 只处理该日期及以后修改的照片；留空处理全部 |

MinIO 上传账号的权限策略示例见 `minio-policy.json`，在 MinIO 控制台创建 Policy 时粘贴使用。注意把其中的桶名和目录前缀改成与 `config.json` 一致。

> 注意：`config.json` 含密钥，已被 `.gitignore` 忽略，不要提交到仓库。

## 使用

在「以管理员身份运行」的命令行窗口中执行：

```cmd
minio-uploader.exe check       :: 只检查连通性和权限，不上传、不删除
minio-uploader.exe install     :: 注册开机自启与看门狗计划任务并立即启动
minio-uploader.exe uninstall   :: 停止并删除计划任务（不删除照片和日志）
minio-uploader.exe once        :: 执行一轮上传后退出
minio-uploader.exe run         :: 常驻运行（计划任务使用）
minio-uploader.exe help        :: 查看帮助
```

更详细的部署步骤、日常维护和常见问题，见 `操作手册.txt`。

## 许可

本项目基于 [MIT 许可证](LICENSE) 开源。
