# Disk Usage Analyzer

Ứng dụng WinForms phân tích dung lượng ổ đĩa, giao diện phong cách Windows 7 Aero.

## Build

Chạy `build.bat` → tạo ra `DiskUsage.exe` (~41 KB, không cần cài đặt gì thêm —
dùng `csc.exe` có sẵn trong .NET Framework 4.x của Windows 10/11).

## Tính năng

- **Cây thư mục**: quét toàn ổ đĩa bằng Win32 `FindFirstFileEx` + đa luồng (`Parallel.For`
  ở 2 tầng đầu), bỏ qua junction/symlink để tránh lặp. Mỗi node hiển thị thanh dung lượng
  màu theo tỷ lệ % so với thư mục cha (xanh lá < 5%, xanh dương < 20%, cam < 50%, đỏ ≥ 50%).
  Chuột phải: mở Explorer / sao chép đường dẫn.
- **Tổng quan**: dung lượng ổ (thanh đã dùng/còn trống bóng kính), số tệp, số thư mục,
  tốc độ quét; Top 15 thư mục lớn nhất (nháy đúp nhảy tới cây), Top 15 tệp lớn nhất,
  Top 15 loại tệp theo dung lượng.
- **Hình ảnh**: liệt kê toàn bộ ảnh tìm thấy (jpg/png/gif/bmp/tiff/webp/ico), sắp theo
  dung lượng giảm dần, lọc theo kích cỡ tối thiểu, thumbnail tải nền từng đợt 200 ảnh.
  Nháy đúp mở trình xem ảnh (phím ←/→ chuyển ảnh, Esc đóng).

## Ghi chú

- Quét ổ hệ thống không cần quyền admin; thư mục bị từ chối truy cập sẽ được đánh dấu
  và đếm riêng (chạy bằng quyền admin sẽ quét được nhiều hơn).
- Bấm **Dừng** giữa chừng vẫn giữ lại kết quả một phần.
