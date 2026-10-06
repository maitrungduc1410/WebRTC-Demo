# Port sang nền tảng mới

Trang này dành cho ai muốn viết client thứ sáu, ví dụ bằng Flutter, React Native, Qt, trên Linux hay trên TV, mà vẫn gọi được cho năm app hiện có. Dưới đây là mọi việc client của bạn phải làm, xếp theo thứ tự bạn có lẽ sẽ làm. Làm xong mục nào thì tick mục đó. Danh sách được lưu lại trong trình duyệt này.

Nền tảng của bạn chỉ cần ba thứ: một bản cài đặt WebRTC (của trình duyệt, [webrtc-sdk](https://github.com/webrtc-sdk) hoặc libwebrtc), một WebSocket client và một JSON parser. Không cần gì thêm. Bạn nên đọc [web client](/vi/platforms/web) song song với danh sách này: engine 1:1 của nó, [`useCall.ts`](gh:web/src/call/useCall.ts), làm hết các bước bên dưới trong đúng một file.

<PortingChecklist>

### 1. Signaling và cuộc gọi {#_1-signaling-and-the-call}

- Khi người dùng vào phòng, mở WebSocket tới `ws://<host>:<port>/ws` và gửi `{"type":"join","roomId":"..."}`. Khi họ rời phòng, gửi `{"type":"leave"}` rồi mới đóng socket.
- Nhận `error` có `fatal: true` thì kết thúc cuộc gọi và hiện `message` của nó (ví dụ `Room is full`).
- Dùng `stun:stun.l.google.com:19302` làm ICE server.
- Nhận được `peer joined` thì bạn là **offerer**. Tạo peer connection, thêm track micro và một video transceiver `sendrecv` (kèm track camera, hoặc không có track nếu máy không có camera), tạo data channel `"MyApp Channel"`, rồi gửi `{"type":"offer","sdp":"..."}`.
- Nếu bạn vào phòng sau, chờ `offer`. Đặt nó làm remote description, thêm các track của bạn, nếu chưa có camera thì chuyển video transceiver `recvonly` của offer thành `sendrecv`, rồi gửi `{"type":"answer","sdp":"..."}`.
- Gửi từng ICE candidate local dưới dạng `{"type":"candidate","candidate":{"candidate":"...","sdpMid":"0","sdpMLineIndex":0}}`. ICE candidate từ bên kia thì xếp hàng chờ cho tới khi đã đặt remote description.
- Nếu socket signaling bị đóng giữa cuộc gọi, kết thúc cuộc gọi và báo cho người dùng. Không tự kết nối lại.
- Bỏ qua mọi callback đến từ peer connection bạn đã đóng hoặc đã thay bằng cái khác.

### 2. Camera và micro {#_2-camera-and-microphone}

- Gửi `{"type":"media state","state":{"audio":true,"video":true,"screen":false}}` ngay khi hai peer kết nối, và gửi lại mỗi khi có thay đổi.
- Tắt camera: gửi state trước, 300 ms sau mới disable track, rồi giải phóng camera.
- Bật camera: mở camera và enable track trước, 300 ms sau mới gửi state.
- Khi state của bên kia báo `video: false`, hiện placeholder thay cho video của họ. Khi báo `screen: true`, hiện video của họ ở chế độ fit thay vì crop.
- Tắt tiếng hay ẩn video của người kia chỉ là thao tác local. Không gửi gì cả.

### 3. Chia sẻ và chuyển nguồn {#_3-sharing-and-switching-sources}

- Chuyển giữa camera, màn hình và file video trên **cùng một video sender** (`replaceTrack()`, `setTrack()`, hoặc một video source được nhiều capturer khác nhau đẩy frame vào). Không bao giờ renegotiate chỉ để chuyển nguồn.
- Không bao giờ thay track micro, để nút tắt tiếng vẫn hoạt động khi đang chia sẻ.
- Đặt `screen: true` trong `media state` khi đang chia sẻ màn hình hoặc file.

### 4. Chat {#_4-chat}

- Nếu bạn là bên answer, lấy channel từ `ondatachannel`.
- Gửi mỗi tin nhắn chat thành một message text UTF-8 thuần trên channel, trim khoảng trắng trước khi gửi. Text nhận được thì hiện nguyên như vậy.
- Nếu channel bị đóng trong khi peer connection vẫn còn, coi như bên kia đã cúp máy.

### 5. Mã hóa đầu cuối {#_5-end-to-end-encryption}

- Nếu bật mã hóa đầu cuối (E2EE) và bạn là offerer, tạo 32 byte ngẫu nhiên và gửi đi dưới dạng `{"type":"encryption key","key":"<base64>"}` **trước khi** gửi offer.
- Nhận `encryption key` thì đặt key và trả lời `{"type":"encryption key received"}`.
- Với webrtc-sdk hoặc libwebrtc, dùng `FrameCryptor` với [các tùy chọn của key provider](/vi/how-it-works/e2ee#the-key): shared key, salt `LKFrameEncryptionKey`, ratchet window 0, không có magic bytes, failure tolerance -1, key ring size 16, PBKDF2, key index 0.
- Nếu không có FrameCryptor, bạn tự cài đặt [định dạng frame](/vi/how-it-works/e2ee#the-frame-format): PBKDF2-HMAC-SHA256 với 100 000 vòng lặp để ra key AES-128-GCM, phần header không mã hóa (tùy codec) đặt ở đầu và dùng làm additional data, tiếp theo là ciphertext kèm tag 16 byte, IV (12 byte), `0x0C` và key index `0`. Demo tương tác trên trang đó cho thấy chính xác từng byte.
- Gắn encryptor vào mọi sender và decryptor vào mọi receiver, rồi bật tất cả cùng lúc.
- Đưa VP8 lên đầu danh sách codec video ưu tiên trước khi tạo offer hoặc answer.
- Frame nào không mã hóa hoặc giải mã được thì bỏ. Không bao giờ gửi đi hay decode nó ở dạng chưa mã hóa.

### 6. Cuộc gọi nhóm (không bắt buộc) {#_6-group-calls-optional}

- Kết nối tới SFU ở `ws://<host>:4001/ws` và gửi `{"type":"join","roomId":"...","name":"YourPlatform","e2ee":false}`. Nếu dùng E2EE, đặt `e2ee: true` và thêm 32 byte ngẫu nhiên vào `e2eeKey` (base64).
- Nhận `joined` thì dùng `e2eeKey` trong đó làm key của phòng, gửi `media state` của bạn, rồi tạo connection **publish**: một audio transceiver `sendonly` và một video transceiver `sendonly`, VP8 đứng đầu, đã gắn encryptor. Gửi offer một lần với `pc: "publish"` và không bao giờ renegotiate nó.
- Trả lời mọi `offer` bằng `pc: "subscribe"`, từng cái một, đúng thứ tự. Gắn decryptor vào mọi receiver sau khi đặt remote description và trước khi gửi answer.
- Thêm `pc: "publish"` hoặc `pc: "subscribe"` vào mọi candidate bạn gửi, và chuyển candidate nhận được tới đúng connection theo `pc` của nó.
- Ghép mỗi remote track với một người tham gia theo stream ID (`streams[0].id`). Phải xử lý trường hợp m-line bị dùng lại: sau một lần renegotiate, cùng một transceiver có thể mang track của người khác.
- Tạo tile từ `joined` và `participant joined`, xóa tile khi nhận `participant left`. Track có thể đến trước hoặc sau thông tin người tham gia.
- Gửi chat dưới dạng `{"type":"chat","text":"..."}`. Chat nhận được có kèm `participantId` và `name` của người gửi.
- Đọc `audioLevel` từ stats của từng audio receiver bên kia, khoảng mỗi 300 ms, để tìm ra người đang nói.

### 7. Kiểm tra với các app hiện có {#_7-check-it-against-the-existing-apps}

- Gọi web client ở cả hai vai: một lần client của bạn vào phòng trước, một lần vào sau.
- Tắt rồi bật camera. Web client hiện placeholder của bạn, rồi video quay lại mà không bị màn hình đen hay đứng hình.
- Chia sẻ màn hình. Web client hiện nó ở chế độ fit.
- Chat theo cả hai chiều.
- Bật E2EE ở cả hai bên: audio và video chạy được cả hai chiều. Sau đó chỉ bật ở một bên: bên kia không thấy và không nghe gì, đúng như mong đợi.
- Gọi một client Android hoặc iOS với E2EE bật, để kiểm tra với `FrameCryptor` native.
- Vào một cuộc gọi nhóm cùng web client và một client native, có và không có E2EE. Rời phòng rồi vào lại.
- Tắt signaling server giữa cuộc gọi. Client của bạn kết thúc cuộc gọi và hiện thông báo.

</PortingChecklist>

## Mẹo {#tips}

- **Lúc đầu chưa cần E2EE và effect.** Một cuộc gọi 1:1 đơn giản, gọi được với web client, là nền móng cho mọi thứ còn lại.
- **Log mọi message gửi đi và nhận về.** Signaling server cũng log các lần join. Phần lớn lỗi khi gọi chéo nền tảng là do thiếu một trường hoặc gửi message sai thứ tự.
- **Đặt tên cho nền tảng của bạn** trong `join` của cuộc gọi nhóm (`name`). Các app khác sẽ hiện tên này trên tile của bạn và trong danh sách người tham gia.
- **Nếu bạn dùng webrtc-sdk**, code Android và iOS gần như map được từng dòng sang code của bạn, vì hai app dùng cùng SDK. Nếu dùng libwebrtc trực tiếp, đọc [header của C shim](gh:windows/native/RtcShim/include/rtc_shim.h) bên Windows để biết một client đầy đủ cần những lời gọi native nào.

Mọi message, trường và lỗi đều có trong [Messages](/vi/reference/messages).
