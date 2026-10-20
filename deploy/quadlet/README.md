# Podman Quadlet（rootless）

适用于 Linux、systemd、cgroup v2、Podman 5.4 或以上版本。使用与 Compose 相同的 Dockerfile；应用、PostgreSQL 和 Redis 各自一个容器，由用户级 systemd 管理。数据库 / Redis 不发布宿主端口，应用仅发布 127.0.0.1:5080。

## 首次部署

在项目根目录构建镜像，并准备配置文件：

~~~bash
podman build -t localhost/wonder:0.1 -f Dockerfile .
mkdir -p ~/.config/containers/systemd ~/.config/wonder
cp deploy/quadlet/*.container deploy/quadlet/*.network deploy/quadlet/*.volume ~/.config/containers/systemd/
cp deploy/quadlet/postgres.env.example ~/.config/wonder/postgres.env
cp deploy/quadlet/app.env.example ~/.config/wonder/app.env
chmod 600 ~/.config/wonder/postgres.env ~/.config/wonder/app.env
~~~

编辑两个 env 文件，设置相同的数据库密码。app.env 的连接串需要写完整值，Podman 不会像 Compose 一样展开变量。文件只供运行此服务的用户读取。初始为 Development 以便回环 HTTP 联调；HTTPS 上线后改为 Production，并按需关闭注册。

先校验 Quadlet 生成，再启动基础设施：

~~~bash
systemctl --user daemon-reload
# Ubuntu / Fedora 常见路径；具体位置可用发行版包管理器查询。
/usr/lib/systemd/system-generators/podman-system-generator --user --dryrun
systemctl --user start wonder-postgres.service wonder-redis.service
systemctl --user status wonder-postgres.service wonder-redis.service
~~~

数据库和 Redis 使用 Notify=healthy，systemd 会等健康检查成功后将依赖视为就绪。迁移单独手动执行，然后启动应用：

~~~bash
# 先创建应用卷，使迁移和正式应用使用同一份存储。
systemctl --user start wonder-data-volume.service wonder-deletion-ledger-volume.service
podman run --rm --network wonder \
  --env-file ~/.config/wonder/app.env \
  -v wonder-data:/app/data \
  -v wonder-deletion-ledger:/app/data/deletion-ledger \
  localhost/wonder:0.1 --migrate
systemctl --user start wonder-app.service
curl --fail http://localhost:5080/health/ready
~~~

如需退出登录后继续运行并在主机启动后启动用户服务，按宿主机管理方式启用该用户的 linger，例如由管理员执行 loginctl enable-linger 用户名。Quadlet 已有 WantedBy=default.target；不对生成的临时 .service 执行 systemctl enable。

## 存储与配置

- wonder-postgres：数据库。
- wonder-redis：Redis AOF。
- wonder-data：应用的 Data Protection 密钥、媒体和加密备份。
- wonder-deletion-ledger：独立挂载的加密删除账本。

独立命名卷默认仍位于同一宿主机。需要异盘 / 异机保存账本时，将对应 Volume 替换为实际备份路径；使用 SELinux 的绑定挂载需按宿主策略设置标签。

NapCat 请填写容器可访问的正向 WebSocket 地址；Podman 的宿主机别名为 host.containers.internal，与 Compose 示例不同。

## 更新、查看日志与恢复

~~~bash
systemctl --user stop wonder-app.service
podman build -t localhost/wonder:0.1 -f Dockerfile .
# 重新执行上面的 --migrate 命令。
systemctl --user start wonder-app.service
journalctl --user -u wonder-app.service -f
~~~

修改 app.env 后重启应用即可。修改 Quadlet 文件后先 daemon-reload，再重启对应服务。不要同时运行 Compose 和这套 Quadlet 来消费同一数据库。

备份 API 和账本导出配置见 [运行指南](../../docs/operations.md)。恢复时停止应用，恢复数据库及密钥，再将最新删除账本解密到私有临时文件。可以用一次性容器重放：

~~~bash
podman run --rm --network wonder \
  --env-file ~/.config/wonder/app.env \
  -v wonder-data:/app/data \
  -v wonder-deletion-ledger:/app/data/deletion-ledger \
  -v /private/latest-ledger.json:/run/wonder-ledger.json:ro,Z \
  localhost/wonder:0.1 --replay-ledger /run/wonder-ledger.json
~~~

临时文件需允许运行容器的应用用户读取；也可以在受控主机直接执行 .NET CLI 重放。完成验证后再启动应用。当前 Windows 开发环境未安装 Podman / systemd，这些单元已核对官方选项，实际 generator 校验和容器联调仍需在目标 Linux 主机执行。

参考：[Podman Quadlet 官方文档](https://docs.podman.io/en/v5.4.0/markdown/podman-systemd.unit.5.html)。
