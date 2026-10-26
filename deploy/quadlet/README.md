# Podman Quadlet（rootless）

适用于 Linux、systemd、cgroup v2、Podman 5.4 或以上版本。Wonder、PostgreSQL 和 SnowLuma 各自一个容器。SnowLuma 使用 `docker.io/motricseven7/snowluma:v2.0.3`，提供 QQ 登录与 OneBot 11 正向 WebSocket。

所有环境变量直接写在对应 `.container` 的 `[Container]` / `Environment=` 中，不需要独立 env 文件。宿主机数据统一映射到 `~/quadlet.config.d/Wonder/模块名`；Quadlet 中的 `%h` 是运行用户的 HOME。

单元采用直接配置的布局：`ContainerName`、`Image`、`Environment` 和 `Volume` 均在文件中声明；统一设置 `TZ=Asia/Shanghai`、`StopTimeout=60`，常驻服务使用 `Restart=always`，手动迁移仍为 oneshot。例如运行用户为 raspberry_kan 时，PostgreSQL 的 `%h/quadlet.config.d/Wonder/PostgreSQL` 对应 `/home/raspberry_kan/quadlet.config.d/Wonder/PostgreSQL`。

每个 `.container` 直接设置 `Network=wonder`，使用首次部署时手动创建的 Podman 网络，不需要独立 `.network` 文件或启动前创建命令。容器共用这个网络，连接串和 OneBot 地址继续使用 postgres、snowluma 网络别名。

镜像用户沿用各自启动要求，Wonder 通过挂载的 `:U` 处理数据权限；没有统一覆盖为 `User=1000:1000` 或 `UserNS=keep-id`。局域网 DNS 可按实际环境在 `[Container]` 中添加 `DNS=`。

## 首次部署

首次应用当前 `InitialCreate` 基线需要空库。旧迁移链的开发库需手动重建并修改应用及迁移单元的连接串；迁移服务不会转换或删除旧库。已采用相同基线时重复运行迁移会跳过已应用项。详细边界见 [数据库基线与首次初始化](../../docs/operations.md#数据库基线与首次初始化)。

在项目根目录构建镜像，并复制单元文件。镜像构建命令与 Docker Compose 的对应方式见 [主 README：构建容器镜像](../../README.md#构建容器镜像)。

~~~bash
podman build -t localhost/wonder:0.1 -f Dockerfile .
podman pull docker.io/pgvector/pgvector:pg18
podman pull docker.io/motricseven7/snowluma:v2.0.3
podman network create --ignore wonder
mkdir -p ~/.config/containers/systemd
mkdir -p ~/quadlet.config.d/Wonder/{PostgreSQL,Host}
mkdir -p ~/quadlet.config.d/Wonder/SnowLuma/{Data,Config,Share}
cp deploy/quadlet/*.container ~/.config/containers/systemd/
chmod 600 ~/.config/containers/systemd/wonder-*.container
~~~

网络创建命令只需首次部署时执行；容器或主机重启后网络配置仍保留。使用运行 `systemctl --user` 的同一个用户执行，不加 sudo。`--ignore` 在网络已存在时直接复用它。如果网络被手动删除或清理，启动服务前重新执行该命令；可用 `podman network inspect wonder` 检查。

编辑 `~/.config/containers/systemd/` 下的文件：

- `wonder-postgres.container`：设置 `POSTGRES_PASSWORD`。
- `wonder-app.container` 与 `wonder-migrate.container`：将连接串密码改成上面相同的值。包含空格的整个 `KEY=value` 必须用双引号包住；这里不展开 shell 变量，百分号字面值需写为 `%%`。
- `wonder-app.container`：默认只配置时区和数据库连接串。其他选项按需添加 `Environment=Authentication__AllowRegistration=false` 或 `Environment=Chat__TimeZone=Asia/Shanghai`。
- `wonder-snowluma.container`：沿用镜像默认参数；需要自定义远程桌面密码时添加 `Environment=VNC_PASSWD=你的密码`，未设置时首次启动生成随机密码，在日志中查看。
- `wonder-snowluma.container`：将两处 `PublishPort=192.168.1.102:...` 改为宿主机实际局域网 IP；仅通过 SSH 转发管理时可改为 `127.0.0.1`。绑定不存在的宿主 IP 会导致容器启动失败，后文访问地址也要对应修改。

应用默认使用 Production 环境，同时支持 HTTP 访问和登录，无需配置 Development；可直接访问宿主的 `http://127.0.0.1:5080` 或通过 SSH 转发访问。登录 Cookie 随请求协议设置 Secure；需要仅允许 Cookie 经 HTTPS 传输时，按需添加 `Environment=Authentication__RequireHttps=true`。上面预先拉取镜像，避免首次启动时拉取耗时超过 systemd 默认启动超时。

校验单元生成并启动基础设施：

~~~bash
systemctl --user daemon-reload
# Ubuntu / Fedora 常见路径；具体位置以发行版为准。
/usr/lib/systemd/system-generators/podman-system-generator --user --dryrun
systemctl --user start wonder-postgres.service wonder-snowluma.service
systemctl --user status wonder-postgres.service wonder-snowluma.service
~~~

服务依赖只控制启动顺序。首次初始化 PostgreSQL 后，先确认下面命令报告 accepting connections，再运行迁移；若尚未就绪，稍后重新检查。SnowLuma 启动成功不代表 QQ 已登录，Wonder 会自动重连。

~~~bash
podman exec wonder-postgres pg_isready -U wonder -d wonder
~~~

迁移独立执行，成功后再启动应用：

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

在日志中查看远程桌面密码和 SnowLuma WebUI 初始登录信息。打开 `http://192.168.1.102:6081/` 扫码登录 QQ，再打开 `http://192.168.1.102:5099/` 配置该账号的 OneBot 11 正向 WebSocket：监听 `0.0.0.0:3001`，使用数组消息段，并复制该 WebSocket 服务的 Access Token。

在 Wonder 控制台的 QQ 连接中填写已登录 QQ 账号、`ws://snowluma:3001/` 和相同 Access Token。宿主机直接运行 .NET 时使用 `ws://127.0.0.1:3001/`。SnowLuma WebUI 密码、远程桌面密码和 OneBot Access Token 是不同的配置。

SnowLuma 管理端口绑定宿主局域网地址 `192.168.1.102`，可从管理电脑直接访问：

| 用途 | 地址 |
| --- | --- |
| SnowLuma WebUI | `http://192.168.1.102:5099/` |
| noVNC 远程桌面、QQ 扫码 | `http://192.168.1.102:6081/` |

`192.168.1.102` 是仓库预设地址，部署时需替换为实际地址，防火墙按需允许管理电脑访问 TCP 5099 和 6081。修改已安装的单元后执行：

~~~bash
systemctl --user daemon-reload
systemctl --user restart wonder-snowluma.service
~~~

应用 5080、PostgreSQL 5432 和 OneBot WebSocket 3001 发布到宿主回环地址。Wonder 控制台可用 SSH 转发访问：

~~~bash
ssh -L 5080:127.0.0.1:5080 user@192.168.1.102
~~~

## 外部管理 PostgreSQL

`wonder-postgres.container` 通过 `PublishPort=127.0.0.1:5432:5432` 将数据库端口映射到宿主机。宿主机上的数据库客户端可连接 `127.0.0.1:5432`。从另一台电脑管理时，先在那台电脑建立 SSH 隧道：

~~~bash
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:15432:127.0.0.1:5432 raspberry_kan@服务器地址
~~~

保持 SSH 连接打开，在 DBeaver、DataGrip、pgAdmin 或 psql 中填写：主机 `127.0.0.1`、端口 `15432`、数据库 `wonder`、用户 `wonder`，密码为 `wonder-postgres.container` 中配置的数据库密码。也可使用客户端自带的 SSH 隧道功能，SSH 目标填写服务器，数据库目标填写服务器侧 `127.0.0.1:5432`。连接方式参考 [PostgreSQL SSH 隧道说明](https://www.postgresql.org/docs/18/ssh-tunnels.html)。

如需局域网直接连接，将端口映射改为服务器实际的局域网 IP，例如 `PublishPort=192.168.1.100:5432:5432`；数据库客户端对应连接 `192.168.1.100:5432`，并在宿主机防火墙中允许管理电脑访问该端口。IP 需替换成实际地址。

修改已安装的单元文件后执行：

~~~bash
systemctl --user daemon-reload
systemctl --user restart wonder-postgres.service
~~~

## 宿主机目录

| 宿主机目录（相对于 `~/quadlet.config.d/Wonder/`） | 容器路径 | 内容 |
| --- | --- | --- |
| `PostgreSQL` | `/var/lib/postgresql` | PostgreSQL 18 + pgvector；数据库文件在 `18/docker` 子目录 |
| `Host` | `/app/data` | Wonder Data Protection 密钥与媒体 |
| `SnowLuma/Data` | `/app/data` | 协议端配置与运行数据 |
| `SnowLuma/Config` | `/app/.config` | QQ 配置与登录状态 |
| `SnowLuma/Share` | `/app/.local/share` | QQ 客户端数据 |

Wonder 的挂载使用 `:U,z`：Podman 将目录所有者映射给镜像内应用用户，并允许应用和手动迁移容器共享 SELinux 标签。其他独占挂载使用 `:Z`。目录仅供容器数据使用，rootless UID 映射后的文件可用 `podman unshare` 管理。

PostgreSQL 18 镜像的默认 PGDATA 为 `/var/lib/postgresql/18/docker`，因此挂载父目录 `/var/lib/postgresql`，对应宿主 `~/quadlet.config.d/Wonder/PostgreSQL/18/docker`。[镜像目录说明](https://hub.docker.com/_/postgres)

这些目录默认仍在同一物理主机；数据库、密钥及需要的媒体应由运维另行备份。不要同时运行 Compose 和 Quadlet 访问这些目录。

各容器采用默认资源管理；实际内存与 CPU 用量取决于聊天负载、数据库规模和 QQ 客户端，目标机器容量需实测。

## 更新、日志与恢复

以下更新命令适用于已采用当前 `InitialCreate` 基线的部署，执行前备份数据库和配套文件；不适用于旧迁移链的数据库。Quartz 触发器在内存中保存，每次启动重新注册，业务任务通过同一 PostgreSQL 中的 Outbox 持久化并恢复。调度周期和停机补排规则见 [调度器与重启恢复](../../docs/operations.md#调度器与重启恢复)。

~~~bash
set -e
systemctl --user stop wonder-app.service
podman build -t localhost/wonder:0.1 -f Dockerfile .
systemctl --user restart wonder-migrate.service
systemctl --user start wonder-app.service
journalctl --user -u wonder-app.service -f
~~~

修改 `.container` 后先 `daemon-reload`，再重启对应服务。更新 SnowLuma 时修改 `wonder-snowluma.container` 的镜像标签，再 reload / restart。PostgreSQL 已有数据目录的密码不会随 `POSTGRES_PASSWORD` 自动改变；已有库需同步修改数据库角色密码。

外部 pg_dump／pg_restore 备份恢复步骤见 [运行指南](../../docs/operations.md#运维备份与恢复)。恢复时停止应用，恢复与当前基线匹配的数据库、Data Protection 密钥及需要的媒体，确认基线后启动。数据库状态以备份时点为准，不自动重放后续删除。

单元 generator 校验、QQ 扫码、容器联调及备份恢复需在实际部署的 Linux 主机验收。完整检查顺序见 [内部自部署验收](../../docs/operations.md#内部自部署验收)；应用的工具能力默认值和图片 FakeIP／代理路径要求见同一运行指南。

参考：[SnowLuma Docker 部署](https://snowluma.github.io/zh/docs/guide/deploy/docker)、[OneBot 对接](https://snowluma.github.io/zh/docs/guide/connect-bot)、[Podman Quadlet](https://docs.podman.io/en/v5.4.0/markdown/podman-systemd.unit.5.html)。
