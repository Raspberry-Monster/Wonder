# Podman Quadlet（rootless）

适用于 Linux、systemd、cgroup v2、Podman 5.4 或以上版本。Wonder、PostgreSQL、Redis 和 SnowLuma 各自一个容器。SnowLuma 使用 `docker.io/motricseven7/snowluma:v2.0.3`，提供 QQ 登录与 OneBot 11 正向 WebSocket。

所有环境变量直接写在对应 `.container` 的 `[Container]` / `Environment=` 中，不需要独立 env 文件。宿主机数据统一映射到 `~/quadlet.config.d/Wonder/模块名`；Quadlet 中的 `%h` 是运行用户的 HOME。

单元采用直接配置的布局：`ContainerName`、`Image`、`Environment` 和 `Volume` 均在文件中声明；统一设置 `TZ=Asia/Shanghai`、`StopTimeout=60`，常驻服务使用 `Restart=always`，手动迁移仍为 oneshot。例如运行用户为 raspberry_kan 时，PostgreSQL 的 `%h/quadlet.config.d/Wonder/PostgreSQL` 对应 `/home/raspberry_kan/quadlet.config.d/Wonder/PostgreSQL`。

每个 `.container` 直接设置 `Network=wonder`，使用首次部署时手动创建的 Podman 网络，不需要独立 `.network` 文件或启动前创建命令。容器共用这个网络，连接串和 OneBot 地址继续使用 postgres、redis、snowluma 网络别名。

镜像用户沿用各自启动要求，Wonder 通过挂载的 `:U` 处理数据权限；没有统一覆盖为 `User=1000:1000` 或 `UserNS=keep-id`。局域网 DNS 可按实际环境在 `[Container]` 中添加 `DNS=`。

## 首次部署

在项目根目录构建镜像，并复制单元文件：

~~~bash
podman build -t localhost/wonder:0.1 -f Dockerfile .
podman network create --ignore wonder
mkdir -p ~/.config/containers/systemd
mkdir -p ~/quadlet.config.d/Wonder/{PostgreSQL,Redis,Host,DeletionLedger}
mkdir -p ~/quadlet.config.d/Wonder/SnowLuma/{Data,Config,Share}
cp deploy/quadlet/*.container ~/.config/containers/systemd/
chmod 600 ~/.config/containers/systemd/wonder-*.container
~~~

网络创建命令只需首次部署时执行；容器或主机重启后网络配置仍保留。使用运行 `systemctl --user` 的同一个用户执行，不加 sudo。`--ignore` 在网络已存在时直接复用它。如果网络被手动删除或清理，启动服务前重新执行该命令；可用 `podman network inspect wonder` 检查。

编辑 `~/.config/containers/systemd/` 下的文件：

- `wonder-postgres.container`：设置 `POSTGRES_PASSWORD`。
- `wonder-app.container` 与 `wonder-migrate.container`：将连接串密码改成上面相同的值。包含空格的整个 `KEY=value` 必须用双引号包住；这里不展开 shell 变量，百分号字面值需写为 `%%`。
- `wonder-app.container`：按需设置注册、备份 age 收件人、管理员账号 UUID、时区。回环 HTTP 联调用 Development；通过 HTTPS 上线后设为 Production。
- `wonder-snowluma.container`：按需设置远程桌面密码。`VNC_PASSWD` 留空时，首次启动生成随机密码，在日志中查看。

校验单元生成并启动基础设施：

~~~bash
systemctl --user daemon-reload
# Ubuntu / Fedora 常见路径；具体位置以发行版为准。
/usr/lib/systemd/system-generators/podman-system-generator --user --dryrun
systemctl --user start wonder-postgres.service wonder-redis.service wonder-snowluma.service
systemctl --user status wonder-postgres.service wonder-redis.service wonder-snowluma.service
~~~

PostgreSQL 和 Redis 使用 `Notify=healthy`，依赖会等健康检查成功。SnowLuma 启动成功不代表 QQ 已登录，Wonder 会自动重连。迁移独立执行，成功后再启动应用：

~~~bash
systemctl --user restart wonder-migrate.service
systemctl --user start wonder-app.service
curl --fail http://localhost:5080/health/ready
~~~

`wonder-migrate.service` 是手动 oneshot 单元，不随登录自动执行。迁移失败时先检查日志，修正后再次 restart；运行迁移前必须停止应用。

如需退出登录后继续运行并在主机启动后启动用户服务，按宿主机管理方式启用该用户的 linger，例如由管理员执行 `loginctl enable-linger 用户名`。常驻单元已有 `WantedBy=default.target`；不对生成的临时 `.service` 执行 `systemctl enable`。

## QQ 登录与 Wonder 连接

~~~bash
podman logs wonder-snowluma
~~~

在日志中查看远程桌面密码和 SnowLuma WebUI 初始登录信息。打开 `http://127.0.0.1:6081/` 扫码登录 QQ，再打开 `http://127.0.0.1:5099/` 配置该账号的 OneBot 11 正向 WebSocket：监听 `0.0.0.0:3001`，使用数组消息段，并复制该 WebSocket 服务的 Access Token。

在 Wonder 控制台的 QQ 连接中填写已登录 QQ 账号、`ws://snowluma:3001/` 和相同 Access Token。宿主机直接运行 .NET 时使用 `ws://127.0.0.1:3001/`。SnowLuma WebUI 密码、远程桌面密码和 OneBot Access Token 是不同的配置。

应用 5080、SnowLuma WebUI 5099、远程桌面 6081 和 OneBot WebSocket 3001 只发布到宿主回环地址。远程管理可用 SSH 转发：

~~~bash
ssh -L 5080:127.0.0.1:5080 -L 5099:127.0.0.1:5099 -L 6081:127.0.0.1:6081 user@server
~~~

## 宿主机目录

| 宿主机目录（相对于 `~/quadlet.config.d/Wonder/`） | 容器路径 | 内容 |
| --- | --- | --- |
| `PostgreSQL` | `/var/lib/postgresql/data` | 数据库 |
| `Redis` | `/data` | Redis AOF |
| `Host` | `/app/data` | Wonder 密钥、媒体与加密备份 |
| `DeletionLedger` | `/app/data/deletion-ledger` | 独立加密删除账本 |
| `SnowLuma/Data` | `/app/data` | 协议端配置与运行数据 |
| `SnowLuma/Config` | `/app/.config` | QQ 配置与登录状态 |
| `SnowLuma/Share` | `/app/.local/share` | QQ 客户端数据 |

Wonder 的挂载使用 `:U,z`：Podman 将目录所有者映射给镜像内应用用户，并允许应用和手动迁移容器共享 SELinux 标签。其他独占挂载使用 `:Z`。目录仅供容器数据使用，rootless UID 映射后的文件可用 `podman unshare` 管理。

这些目录默认仍在同一物理主机；账本需要异盘 / 异机保存时修改对应挂载来源或备份同步。不要同时运行 Compose 和 Quadlet 访问这些目录。旧版本使用命名卷；升级前停止旧容器并备份，将原卷内容迁到对应目录，保留数据库数据及 `Host/keys`。空目录不会自动导入旧卷。

应用、PostgreSQL、Redis、SnowLuma 的内存限制合计约 4.25 GiB；还需给操作系统、共享内存和峰值留空间，目标机器容量需实测。

## 更新、日志与恢复

~~~bash
systemctl --user stop wonder-app.service
podman build -t localhost/wonder:0.1 -f Dockerfile .
systemctl --user restart wonder-migrate.service
systemctl --user start wonder-app.service
journalctl --user -u wonder-app.service -f
~~~

修改 `.container` 后先 `daemon-reload`，再重启对应服务。更新 SnowLuma 时修改 `wonder-snowluma.container` 的镜像标签，再 reload / restart。PostgreSQL 已有数据目录的密码不会随 `POSTGRES_PASSWORD` 自动改变；已有库需同步修改数据库角色密码。

备份 API 与账本导出见 [运行指南](../../docs/operations.md)。恢复时停止应用，恢复数据库及密钥，再重放最新删除账本。重放沿用 `wonder-migrate.container` 的网络、挂载和连接串：

1. 停止 `wonder-app.service`，将解密账本放到私有路径。
2. 临时修改 `wonder-migrate.container`：将 `Exec=--migrate` 改成 `Exec=--replay-ledger /run/wonder-ledger.json`，并在 `[Container]` 加入 `Volume=/private/latest-ledger.json:/run/wonder-ledger.json:ro,Z`。文件需允许运行容器的应用用户读取。
3. 执行 `systemctl --user daemon-reload`、`systemctl --user restart wonder-migrate.service`，确认成功。
4. 还原该单元的 Exec 和临时挂载，再 daemon-reload。验证恢复结果后启动应用。

当前开发环境未安装 Podman / systemd，实际 generator 校验、QQ 扫码和容器联调需在目标 Linux 主机执行。

参考：[SnowLuma Docker 部署](https://snowluma.github.io/zh/docs/guide/deploy/docker)、[OneBot 对接](https://snowluma.github.io/zh/docs/guide/connect-bot)、[Podman Quadlet](https://docs.podman.io/en/v5.4.0/markdown/podman-systemd.unit.5.html)。
