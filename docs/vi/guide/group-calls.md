---
title: "Chạy cuộc gọi video nhóm WebRTC với SFU Pion"
description: "Chạy SFU server viết bằng Go trên Pion để gọi nhóm WebRTC giữa các app web, Android, iOS, macOS và Windows, trong mạng LAN hoặc trên cloud VM."
---

# Cuộc gọi nhóm

Với cuộc gọi 1:1, những gì trong phần [bắt đầu nhanh](/vi/guide/quick-start) là đủ. Muốn gọi với nhiều người hơn, bạn chạy thêm SFU server. Server này lo cả signaling lẫn media cho cuộc gọi nhóm, nên ở chế độ này không dùng tới signaling server Node.

## Chạy SFU server {#start-the-sfu-server}

Bạn cần [Go](https://go.dev/dl/) 1.25 trở lên. Nếu đang dùng Go 1.21 đến 1.24, lần chạy đầu tiên `go` sẽ tự tải bản 1.25.

```sh
cd sfu-server
go run .
```

```
SFU listening on :4001 (TCP: signaling, UDP: media)
Network access via: ws://192.168.1.10:4001/ws
```

Client phải truy cập được **cả TCP và UDP port 4001** trên máy đó: TCP cho WebSocket, UDP cho media của mọi peer connection. Kiểm tra từ một thiết bị khác bằng `curl http://192.168.1.10:4001/`, kết quả trả về là `{"name":"sfu-server","ok":true}`.

## Vào phòng từ các client {#join-from-the-clients}

Ở lobby của client bất kỳ, chuyển sang **Group call (SFU)**, đặt địa chỉ SFU một lần, rồi vào cùng một room ID trên mọi thiết bị.

| Client | Địa chỉ mặc định | Nhập gì |
| --- | --- | --- |
| Web | Port 4001 trên host đang phục vụ trang web | `192.168.1.10:4001` (trên web luôn ghi kèm port) |
| Android, iOS, Windows | Port 4001 trên host của signaling server | `192.168.1.10` (app tự thêm `:4001`) hoặc đầy đủ `ws://192.168.1.10:4001/ws` |
| macOS | `http://localhost:4001`, trong Settings (⌘,) | Giống iOS |

Cuộc gọi nhóm cũng dùng được E2EE. Mọi người trong phòng phải chọn cùng một thiết lập, và người vào đầu tiên là người quyết định.

## Tùy chọn {#options}

Mỗi tùy chọn là một flag hoặc một biến môi trường. Nếu đặt cả hai thì flag được ưu tiên.

| Flag | Biến | Mặc định | Tác dụng |
| --- | --- | --- | --- |
| `-port` | `PORT` | `4001` | Port TCP cho HTTP và WebSocket, đồng thời là port UDP cho media |
| `-public-ip` | `PUBLIC_IP` | không có | Địa chỉ ghi vào ICE candidate thay cho địa chỉ của chính máy. Dùng khi server nằm sau NAT 1:1, ví dụ một VM trên cloud. |
| `-max-participants` | `MAX_PARTICIPANTS` | `8` | Số người mỗi phòng. Người tiếp theo sẽ nhận `Room is full`. |

```sh
go run . -max-participants 12
PORT=5000 go run .
go run . -public-ip 203.0.113.7
```

## Bao nhiêu người là được? {#how-many-people}

Giới hạn trên chỉ là giá trị mặc định. Không có simulcast, nên mỗi client nhận và decode video của tất cả những người còn lại ở chất lượng đầy đủ: với N người, mỗi điện thoại tải về N − 1 video và server gửi đi N × (N − 1) stream. Thường thì điện thoại đuối trước server, vào khoảng 8 đến 10 video. Trang [Cuộc gọi nhóm (SFU)](/vi/how-it-works/group-calls) giải thích lý do và có sẵn một công cụ tính.

## Mạng {#networking}

- **Firewall.** Mở TCP và UDP 4001 (hoặc port bạn đặt bằng `-port`). macOS sẽ hỏi ở lần chạy đầu. Trên Linux dùng ufw: `sudo ufw allow 4001`.
- **VM trên cloud.** Mở TCP và UDP 4001 trong security group và chạy với `-public-ip <IP public của VM>`. Nếu thiếu flag này, WebSocket vẫn kết nối nhưng không có video nào tới.
- **Chỉ IPv4 và UDP.** Không có TURN server và cũng không có phương án dự phòng qua TCP cho media. Mạng nào chặn UDP chiều đi thì không nhận được video.
- **Không có TLS.** Server dùng `ws://`. Web client được phục vụ qua HTTPS thì không mở được socket `ws://`, nên trong trường hợp đó bạn đặt một TLS reverse proxy (nginx, Caddy) phía trước `/ws`.
- **Không có xác thực.** Ai truy cập được port là vào được mọi phòng. Đừng mở nguyên như vậy ra internet.

Chi tiết hơn có trong [`sfu-server/README.md`](gh:sfu-server/README.md). Nếu gặp lỗi, xem [Xử lý sự cố](/vi/guide/troubleshooting#group-calls).
