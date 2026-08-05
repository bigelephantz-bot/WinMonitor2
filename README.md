# WinMonitor

輕量級 Windows 硬體監控程式（Core Temp 風格），專為常駐系統匣設計。
Lightweight Core-Temp-style hardware monitor for Windows, built to live in the system tray.

## 功能 Features

- **感測器**：CPU（各核心/封裝溫度、功耗、頻率、使用率）、SSD/NVMe（溫度、SMART 健康度、已寫入量）、電池（電量、健康度、充放電功率）、主機板/ACPI 溫度、風扇轉速 + PWM、可選 GPU
- **多重系統匣圖示**：每個圖示獨立顯示一個感測器，或單一圖示輪播多個；自選顏色主題（門檻變色或固定色）、粗體、單位顯示
- **多層級顏色警示**：每個感測器可獨立設定黃/紅門檻，或使用系統建議值；圖示即時變色
- **警示通知**：超過紅色門檻並持續指定秒數才通知（過濾瞬間尖峰），Windows 通知 + 可選音效
- **Session 統計**：自啟動以來的最低/最高/平均；滑鼠停在系統匣圖示顯示完整資訊
- **內建圖表**：最近 10–60 分鐘溫度曲線；CSV 匯出（目前讀數 + 歷史）；可選背景記錄（自動清理保留天數）
- **感測器重新命名/隱藏**、多組設定檔（日常/遊戲/安靜）、迷你常駐視窗、°C/°F、繁體中文 + English
- **開機自動啟動**：可延遲 0–60 秒；管理員模式走工作排程器（不跳 UAC），一般權限走登錄檔
- **輕量化**：可調輪詢間隔（1/2/5 秒）、智慧輪詢（縮小化時只讀取需要的感測器群組）、低優先權背景執行緒、單一執行個體、GDI 資源零洩漏設計

## 系統需求 Requirements

- Windows 10/11 x64
- .NET 10 Desktop Runtime（未安裝時系統會提示下載）
- **完整感測器需要系統管理員權限**（CPU MSR、NVMe SMART、EC）。一般權限下仍可讀取電池與部分 ACPI 溫度。
- 安裝 [PawnIO 2.2.0+](https://github.com/namazso/PawnIO.Setup/releases) 簽章驅動；CPU 溫度、功耗與頻率等低階感測值透過此驅動讀取。

## 建置 Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

產出：
- `dist\WinMonitor\` — 安裝版（設定存於 `%AppData%\WinMonitor`）
- `dist\WinMonitor-Portable\` — 免安裝版（設定存於程式資料夾）

## 已知限制 Known limitations

- **記憶體溫度**：多數消費級記憶體（含 LG gram 的板載 LPDDR）沒有獨立溫度感測器，程式會明確標示。
- **風扇轉速**：LG gram 等部分筆電的風扇由專屬 EC 控制；`16T90R` 已有經 ACPI
  韌體與實機讀值確認的預設 RPM 對應。其他機型需先用 `tools\SensorDump` 診斷，
  再將確認的 register 加入 `KnownEcProfiles`。
- 感測器涵蓋度取決於 LibreHardwareMonitor 對該機型的支援程度。

## 架構 Architecture

| 元件 | 說明 |
|---|---|
| 後端 | LibreHardwareMonitorLib 0.9.6（MPL-2.0 / PawnIO），專用低優先權輪詢執行緒 |
| 前端 | .NET 10 WinForms（framework-dependent，無額外 UI 套件） |
| 系統匣 | 每圖示一個 NotifyIcon，GDI+ 動態繪製 32px 文字圖示，Explorer 重啟自動復原 |
| 設定 | JSON（`config.json`），原子寫入，portable 模式偵測 |

詳見 [ARCHITECTURE.md](ARCHITECTURE.md)。
