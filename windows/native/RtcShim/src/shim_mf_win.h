// Media Foundation helpers shared by the file and camera sources (Windows only).
#pragma once

#include "shim_internal.h"

#include <cstdio>

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>

namespace rtc_shim {

template <typename T>
class Com {
 public:
  Com() = default;
  Com(const Com&) = delete;
  Com& operator=(const Com&) = delete;
  ~Com() { Reset(); }
  T** Put() {
    Reset();
    return &ptr_;
  }
  T* Get() const { return ptr_; }
  T* operator->() const { return ptr_; }
  explicit operator bool() const { return ptr_ != nullptr; }
  void Reset() {
    if (ptr_) ptr_->Release();
    ptr_ = nullptr;
  }

 private:
  T* ptr_ = nullptr;
};

inline std::wstring Widen(const std::string& utf8) {
  if (utf8.empty()) return std::wstring();
  const int length = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()),
                                         nullptr, 0);
  std::wstring wide(static_cast<size_t>(length), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), &wide[0], length);
  return wide;
}

inline std::string Narrow(const wchar_t* wide) {
  if (!wide || !*wide) return std::string();
  const int length = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
  if (length <= 1) return std::string();
  std::string utf8(static_cast<size_t>(length - 1), '\0');
  WideCharToMultiByte(CP_UTF8, 0, wide, -1, &utf8[0], length, nullptr, nullptr);
  return utf8;
}

inline std::string HrMessage(const char* what, HRESULT hr) {
  char text[128];
  std::snprintf(text, sizeof(text), "%s failed (hr=0x%08lX)", what, static_cast<unsigned long>(hr));
  return text;
}

// Copies the w x h area at (x, y) of an NV12 picture (both planes `pitch` bytes per row; x and y
// even) into I420 planes of exactly w and w / 2 bytes per row.
inline void CopyNv12ToI420(const BYTE* y_plane, const BYTE* uv_plane, LONG pitch, int x, int y,
                           int w, int h, std::vector<uint8_t>* dst_y, std::vector<uint8_t>* dst_u,
                           std::vector<uint8_t>* dst_v) {
  const int cw = w / 2;
  const int ch = h / 2;
  dst_y->resize(static_cast<size_t>(w) * h);
  dst_u->resize(static_cast<size_t>(cw) * ch);
  dst_v->resize(static_cast<size_t>(cw) * ch);
  const BYTE* src_y = y_plane + static_cast<size_t>(y) * pitch + x;
  for (int row = 0; row < h; ++row) {
    std::memcpy(&(*dst_y)[static_cast<size_t>(row) * w], src_y + static_cast<size_t>(row) * pitch,
                static_cast<size_t>(w));
  }
  const BYTE* src_uv = uv_plane + static_cast<size_t>(y / 2) * pitch + x;
  for (int row = 0; row < ch; ++row) {
    const BYTE* line = src_uv + static_cast<size_t>(row) * pitch;
    uint8_t* u = &(*dst_u)[static_cast<size_t>(row) * cw];
    uint8_t* v = &(*dst_v)[static_cast<size_t>(row) * cw];
    for (int col = 0; col < cw; ++col) {
      u[col] = line[col * 2];
      v[col] = line[col * 2 + 1];
    }
  }
}

}  // namespace rtc_shim