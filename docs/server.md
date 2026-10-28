# Linux / Docker 服务器版 v1.3.0

这是单账号的服务器版：网页管理、持久化计划、照片上传、只读会话核验、每天北京时间 08:00 / 12:00 / 18:00 调度及防重复记录。服务器无需一直开启桌面电脑。

服务器可以沿用有效 token，平台下发新 token 时会核验同一账号后保存。**没有实现独立重新登录微信或过期续期**；初次使用或会话过期时，需要从正常登录微信的设备取得本人 X-Token，再在管理页核验更新。服务器不安装微信、不抓取远程电脑流量，也不安装临时根证书。

## 1. 部署

目标为有 Docker Engine、Docker Compose v2 和 OpenSSL 的 Linux 主机。镜像构建会拉取微软 .NET 10 SDK / ASP.NET 运行时并恢复项目；应用使用系统 HTTPS 验证访问平台。采用框架依赖构建，可由 Docker 在目标 CPU 上构建，无额外 Node、Python 或数据库运行依赖。

将部署包上传服务器并解压，或从仓库检出 v1.3.0：

```sh
tar -xzf ClearPlate-Server-Docker-v1.3.0.tar.gz
cd clearplate-server-v1.3.0/deploy/server
sh init.sh
docker compose up -d --build
docker compose ps
```

`init.sh` 创建随机管理密码、32 字节加密密钥、`.env` 和数据目录，不打印密码，不覆盖已有秘密。初始化与 Compose 请使用同一部署用户。普通用户生成的 UID/GID 与容器一致；以 root 初始化时选择非 root 的 UID/GID 1654 并设置目录权限。应用进程以非 root 用户运行，根文件系统只读，数据目录单独挂载。

默认绑定 **127.0.0.1:8787**。在自己的电脑建立 SSH 隧道：

```sh
ssh -N -L 8787:127.0.0.1:8787 user@your-server
```

打开 `http://localhost:8787`。在服务器的部署目录读取初始管理密码，然后在网页登录：

```sh
cat .secrets/admin_password
```

密码和加密密钥通过只读 Docker secrets 文件提供，未写进镜像、环境变量或 Compose 配置。Compose 本地文件型 secrets 的读取权限还取决于宿主文件权限，本方案由初始化脚本匹配 UID/GID、设置 600 权限。[Docker secrets 说明](https://docs.docker.com/compose/how-tos/use-secrets/)

## 2. 初次配置

1. 在“登录会话”填写本人完整 X-Token，点击“核验并更新”。平台返回昵称后确认是本人账号，才保存；以后只接受原绑定账号。网页不会回显 token。
2. 填写组织 ID 和当天真实位置（纬度,经度）。组织 ID 可按原请求留空，位置不会自动推测。
3. 可更换当天照片。支持 PNG / JPG，1 KB 至 20 MB；没有更换时使用内置的默认照片。后续计划使用当前保存的照片，平台仍正常校验内容。
4. 保存设置，按需开启自动计划。**首次安装默认关闭计划**。

启用服务器计划前，请关闭同账号桌面端的自动计划。不同设备没有共享本地防重复记录，初次导入 token 也不会自动迁移桌面打卡历史；请先核对当天已完成的记录。Windows DPAPI 与 Mac 钥匙串文件不能直接复制为服务器状态文件。

## 3. 调度、重试与记录

- 08:00 / 12:00 / 18:00 使用固定 UTC+8 时间，不依赖 Linux 宿主时区。每 15 秒检查一次；各时段只在开始后的 5 分钟窗口内执行，结束时刻不包含在内。
- 上传前查询平台剩余次数及最近成功时间，必须满足两小时间隔；接口返回 10007 且成功次数增加，才记录为成功。
- 查询账号时临时断网可在原窗口内自动重试。照片准备、TLS、服务器等其他上传前失败允许明确的手动重试，避免自动循环；错过窗口不自动补交。
- 已进入上传、提交结果不明、已成功或缺少明确阶段的记录继续锁定防重复。磁盘写入成功后才发起后续上传/提交；Linux 下同步文件及目录。
- 已保存的确定未上传旧失败可恢复为只允许手动重试，原错误和时间保留。不能通过清空记录来核对平台结果。
- 单进程内所有修改与定时任务共用互斥锁，同时使用持久化目录锁阻止同一目录的多个服务实例。支持一个容器、一个账号；不配置副本扩容或多个独立数据目录同时运行同一账号。
- 会话失效暂停自动提交。存储写入失败也会暂停后续提交，需要修复存储并重启；已初始化数据丢失、解密失败时拒绝启动，保留原文件。

“立即打卡”是明确的手动操作；HTTP 请求处理完成不等于打卡成功，请以页面显示的阶段与平台核对结果为准。

## 4. 公网 HTTPS 与端口

默认的 SSH 隧道方案不需要域名。公网访问可使用已有 HTTPS 反向代理，将其指向主机 `127.0.0.1:8787`，并修改 `.env`：

```dotenv
CLEARPLATE_PUBLIC_ORIGIN=https://your-domain.example
```

然后运行 `docker compose up -d` 重建服务。浏览器必须使用完全相同的站点来源（协议、主机、端口），否则修改请求会被拒绝。HTTPS 模式使用 Secure、HttpOnly、SameSite=Strict 会话 cookie。管理会话最多 8 小时，服务重启后需重新登录；页面自带 CSRF 校验及登录限速。

如调整宿主端口，需要同时调整 `.env` 中的 `CLEARPLATE_PORT` 和公开来源。例如 SSH 隧道访问 `http://localhost:8888` 时，设置端口 8888 并将来源设置为该地址。 如在跨地域或高延迟网络中部署，可在 `.env` 中设置 `CLEARPLATE_HTTP_TIMEOUT=90`（默认 90 秒）延长请求超时时间。

## 5. 数据、备份和升级

部署目录中的以下文件必须保留：

| 路径 | 内容 |
| --- | --- |
| `.env` | 容器 UID/GID、端口、站点来源 |
| `.secrets/admin_password` | 随机管理密码 |
| `.secrets/session_key` | 加密密钥；更换会导致旧数据无法读取 |
| `data/state.enc` | AES-GCM 加密的本人会话、位置、计划及防重复记录 |
| `data/photo-*.enc` | 当前照片的加密内容 |
| `data/initialized` | 已初始化标记，防止状态文件丢失后悄悄重置 |
| `data/server.lock` | 单实例文件锁 |

备份应同时包含 `.env`、`.secrets`、`data`，并限制备份访问权限。先停止容器确保状态一致，备份到自己选择的私有目录，再启动：

```sh
docker compose stop
umask 077
tar -czf /path/to/private-backup.tar.gz .env .secrets data
docker compose start
```

升级前停止旧版。将原 `.env`、`.secrets`、`data` 完整复制到新版的 `deploy/server/`，保留原权限，再运行 `docker compose up -d --build`。不要重新生成加密密钥，也不要将个人数据放进 Git 或公开部署包。部署包及 Docker 构建上下文都排除了本地数据和秘密文件。

修改 `.secrets/admin_password` 为新的强随机密码后，执行 `docker compose up -d --force-recreate` 生效。现有网页会话随重启失效；会话加密密钥及打卡记录保持原样。

## 6. 检查与诊断

```sh
docker compose ps
docker compose logs --tail=100
curl http://127.0.0.1:8787/healthz
```

健康端点只报告进程可响应，不表示 token 可用或打卡成功。启动失败使用固定代码，不输出 token、管理密码或原始请求：`STATE_DECRYPT_FAILED` 检查原密钥与加密数据；`DATA_ACCESS_DENIED` 检查 UID/GID 和权限；`REQUIRED_FILE_MISSING` 检查秘密及引用的照片文件；`DATA_LOCK_OR_IO_FAILED` 检查其他实例、磁盘和状态目录。

## 7. 构建验证范围

```sh
dotnet run --project tests/ClearPlate.Tests/ClearPlate.Tests.csproj -c Release
dotnet run --project tests/ClearPlate.Server.Tests/ClearPlate.Server.Tests.csproj -c Release
docker compose -f deploy/server/compose.yml build
```

已通过本机 ASP.NET 服务的真实 HTTP 测试，平台请求使用模拟接口，覆盖管理认证、CSRF、账号确认、加密存储、照片、并发、调度、失败保护及重启恢复；浏览器检查使用模拟账号。没有使用真实账号测试上传或打卡。

当前开发机没有 Docker Engine，**尚未执行 Linux Docker 镜像构建、容器运行及 Linux 目录同步分支**。提供的是源码部署包，不是预先构建的镜像或已经上线的服务。镜像构建参考 [微软容器化文档](https://learn.microsoft.com/en-us/dotnet/core/docker/build-container)。
