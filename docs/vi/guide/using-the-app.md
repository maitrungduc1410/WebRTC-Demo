# Dùng app

Cả năm app có cùng một màn hình cuộc gọi. Trên điện thoại bạn điều khiển bằng cảm ứng, trên máy tính bằng chuột và bàn phím.

## Điện thoại: Android và iOS {#phones-android-and-ios}

<DemoMedia src="/media/call-ios.png" :width="320">
Một iPhone đang trong cuộc gọi 1:1, màn hình dọc: video của người kia chiếm cả màn hình, video của bạn nằm thành một tile nhỏ ở góc, toolbar hiện ở phía dưới.
</DemoMedia>

- Khi bạn đang ở một mình, camera của bạn chiếm cả màn hình và có một thẻ hiện room ID kèm nút copy. Khi người kia vào, video của bạn thu nhỏ thành một tile ở góc.
- Chạm vào video để hiện hoặc ẩn các nút điều khiển. Sau vài giây chúng tự ẩn.
- Chạm đúp vào video của người kia để chuyển giữa **fit** (thấy trọn frame, có viền đen) và **fill** (crop cho vừa màn hình). Chia sẻ màn hình mặc định ở chế độ fit. Camera cầm ngược hướng với màn hình của bạn cũng vậy, ví dụ một điện thoại dọc gọi tới một điện thoại đang xoay ngang.
- Kéo tile của bạn tới góc nào cũng được. Chạm đúp vào nó, hoặc dùng nút ở góc trên bên phải, để chuyển giữa camera trước và camera sau.
- Toolbar có các nút micro, camera, chia sẻ, chat, **More** và nút cúp máy. More chứa loa, effect, audio của người kia, video của người kia, fit hoặc fill và nút đổi camera.
- Cả hai app đều chạy được khi xoay ngang. Các nút điều khiển không bị che bởi phần khoét camera hay thanh điều hướng.

## Máy tính: Web, macOS và Windows {#desktop-web-macos-and-windows}

- Video chiếm cả cửa sổ. Di chuột để hiện các nút điều khiển. Nếu không di chuột khoảng bốn giây thì chúng tự ẩn.
- Nút trên toolbar có tooltip ghi phím tắt (xem bên dưới).
- Micro, loa và camera được chọn từ menu cạnh nút micro và nút camera. Nút chia sẻ mở một hộp chọn có thumbnail trực tiếp của các màn hình và cửa sổ (trình duyệt thì dùng hộp chọn riêng của nó).
- Ở cửa sổ rộng, chat mở thành một panel bên cạnh. Trên web, khi cửa sổ hẹp hơn 1024 px thì chat mở thành bottom sheet, còn trên trình duyệt điện thoại thì More mở một sheet chứa các tùy chọn còn lại.
- Kéo video của bạn tới góc nào cũng được, và double-click vào video của người kia để chuyển giữa fit và fill.
- Nút picture-in-picture giữ cuộc gọi nằm trên các app khác, kèm các nút tắt tiếng, camera, quay lại và cúp máy. Trên Mac, nó cũng tự mở khi bạn thu nhỏ cửa sổ cuộc gọi.

### Phím tắt {#keyboard-shortcuts}

| Phím | Tác dụng |
| --- | --- |
| `M` | Bật hoặc tắt micro |
| `V` | Bật hoặc tắt camera |
| `C` | Hiện hoặc ẩn chat |
| `B` | Phông nền và effect |
| `F` | Fit hoặc fill (chỉ trong cuộc gọi 1:1) |
| `P` | Picture-in-picture hoặc cửa sổ nổi |
| `Esc` | Đóng chat hoặc panel effect |

Trên Mac, các phím tắt này cũng có trong menu **Call**, thêm ⇧⌘S để chia sẻ, ⌘O để chia sẻ một file video, ⇧⌘P để mở danh sách người tham gia trong cuộc gọi nhóm và ⇧⌘E để rời phòng. Khi bạn đang gõ trong chat thì các phím tắt bị tắt.

## Những thứ chỉ ảnh hưởng tới bạn {#things-that-only-affect-you}

Tắt tiếng người kia hoặc ẩn video của họ (trong More) chỉ thay đổi trên thiết bị của bạn. Họ không được báo gì.

Khi người kia tắt camera, hoặc bạn ẩn video của họ, bạn sẽ thấy một bản làm mờ của frame cuối cùng nằm sau avatar của họ. Vòng tròn quanh avatar nhấp nháy khi họ nói.

## Cuộc gọi nhóm {#group-calls}

<DemoMedia src="/media/group-web.png" :width="720">
Web client trong một cuộc gọi nhóm bốn hoặc năm người trên các nền tảng khác nhau: lưới tile kèm nhãn (ví dụ "Android · 3f2a1c"), một tile có vòng xanh báo đang nói, một tile có icon micro tắt, một tile hiện avatar vì camera đang tắt.
</DemoMedia>

- Những người khác hiện thành một lưới. Mỗi tile ghi nền tảng và một ID ngắn của người đó, ví dụ `Android · 3f2a1c`, và hiện icon micro tắt khi họ tắt micro.
- Vòng xanh đánh dấu người đang nói.
- Số người cạnh đồng hồ đếm giờ mở ra danh sách mọi người trong phòng. Nhãn của bạn đứng đầu và được tô nổi, để bạn tìm ra tile của mình trên các thiết bị khác.
- Người đang chia sẻ màn hình được hiện ở chế độ fit, những người còn lại fill kín tile. Chạm đúp vào tile (trên máy tính thì double-click) để chuyển.
- Chat đi qua SFU server và hiện người gửi của từng tin nhắn. Trong More, tắt tiếng hoặc ẩn video áp dụng cho tất cả mọi người, kể cả người vào sau.
- Trên web và iOS, tile trong cuộc gọi nhóm chỉ hiện avatar, không có frame cuối bị làm mờ. Trên iOS, cửa sổ picture-in-picture của hệ thống chỉ có trong cuộc gọi 1:1.
