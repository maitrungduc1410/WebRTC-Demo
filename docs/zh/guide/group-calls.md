---
title: "用 Pion SFU 运行 WebRTC 多人视频通话"
description: "启动基于 Pion 的 Go SFU 服务器，让 Web、Android、iOS、macOS 和 Windows 应用在局域网或云服务器上进行 WebRTC 多人通话。"
---

# 多人通话

[快速开始](/zh/guide/quick-start)里的内容足够支撑 1:1 通话。如果要和更多人通话，还需要启动 SFU 服务器。多人通话的信令和媒体都由它负责，所以这种模式下不会用到 Node 信令服务器。

## 启动 SFU 服务器 {#start-the-sfu-server}

需要 [Go](https://go.dev/dl/) 1.25 或更高版本。如果是 Go 1.21 到 1.24，第一次运行时 `go` 会自动下载 1.25。

```sh
cd sfu-server
go run .
```

```
SFU listening on :4001 (TCP: signaling, UDP: media)
Network access via: ws://192.168.1.10:4001/ws
```

客户端必须能访问这台机器的 **TCP 和 UDP 4001 端口**：TCP 用于 WebSocket，UDP 用于所有 peer connection 的媒体。可以在另一台设备上用 `curl http://192.168.1.10:4001/` 检查，正常会返回 `{"name":"sfu-server","ok":true}`。

## 从客户端加入 {#join-from-the-clients}

在任意客户端的大厅中切换到 **Group call (SFU)**，设置一次 SFU 地址，然后在每台设备上加入同一个房间 ID。

| 客户端 | 默认地址 | 填写什么 |
| --- | --- | --- |
| Web | 提供页面的主机上的 4001 端口 | `192.168.1.10:4001`（Web 端一定要带上端口） |
| Android、iOS、Windows | 信令服务器所在主机的 4001 端口 | `192.168.1.10`（会自动补上 `:4001`），或完整的 `ws://192.168.1.10:4001/ws` |
| macOS | `http://localhost:4001`，在 Settings（⌘,）中修改 | 与 iOS 相同 |

多人通话同样支持 E2EE。房间里所有人的设置必须一致，以第一个加入的人为准。

## 选项 {#options}

每个选项都可以通过命令行参数或环境变量设置，两者同时存在时以参数为准。

| 参数 | 环境变量 | 默认值 | 作用 |
| --- | --- | --- | --- |
| `-port` | `PORT` | `4001` | HTTP 和 WebSocket 使用的 TCP 端口，同时也是媒体使用的 UDP 端口 |
| `-public-ip` | `PUBLIC_IP` | 无 | 写进 ICE candidate 的地址，用来替代本机地址。机器位于一对一 NAT 之后时使用，比如云服务器。 |
| `-max-participants` | `MAX_PARTICIPANTS` | `8` | 每个房间的人数上限。超出后再加入的人会收到 `Room is full`。 |

```sh
go run . -max-participants 12
PORT=5000 go run .
go run . -public-ip 203.0.113.7
```

## 能容纳多少人？ {#how-many-people}

这个上限只是默认值。由于没有 simulcast，每个客户端都要以完整画质接收并解码其他所有人的视频：N 个人通话时，每部手机要下载 N − 1 路视频，服务器要发送 N × (N − 1) 路 stream。通常在 8 到 10 路视频左右，手机会比服务器先扛不住。[多人通话（SFU）](/zh/how-it-works/group-calls)解释了原因，还附带一个计算器。

## 网络配置 {#networking}

- **防火墙。** 放行 TCP 和 UDP 4001（或你通过 `-port` 指定的端口）。macOS 第一次运行时会弹窗询问。Linux 上使用 ufw 的话：`sudo ufw allow 4001`。
- **云服务器。** 在安全组中开放 TCP 和 UDP 4001，并用 `-public-ip <云服务器的公网 IP>` 启动。不加这个参数，WebSocket 能连上，但收不到视频。
- **只支持 IPv4 和 UDP。** 没有 TURN 服务器，媒体也不会回退到 TCP。屏蔽出站 UDP 的网络收不到视频。
- **没有 TLS。** 服务器使用 `ws://`。通过 HTTPS 提供的 Web 客户端无法打开 `ws://` socket，这种情况下需要在 `/ws` 前面加一层 TLS 反向代理（nginx、Caddy）。
- **没有身份认证。** 任何能访问这个端口的人都能加入任意房间。不要原样暴露到公网。

更多细节见 [`sfu-server/README.md`](gh:sfu-server/README.md)。遇到问题请参考[常见问题](/zh/guide/troubleshooting#group-calls)。
