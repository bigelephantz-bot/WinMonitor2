# WinMonitor

[English](README.md) | [繁體中文](README.zh-TW.md)

WinMonitor 是以 .NET 10 WinForms 開發的輕量級 Windows 硬體監控工具，整合即時感測器數值、可自訂系統匣圖示、獨立刻度圖表、警示與 CSV 歷史紀錄。

## 主要功能

- 透過 LibreHardwareMonitor 監控支援的 CPU、GPU、儲存裝置、記憶體、電池、主機板與風扇感測器。
- 顯示溫度、功率、頻率、負載、電壓、資料量、PWM 與 RPM，並統計本次執行期間的最低、最高與平均值。
- 提供可自訂的系統匣圖示，支援單位、門檻顏色、多感測器輪播及精簡模式。
- 可顯示最近 1、3、5、10、20、30 或 60 分鐘的感測器曲線；不同物理量使用獨立 Y 軸，並以不同標記及滑鼠提示區分曲線。
- 支援門檻警示、設定檔、自動啟動、電池自適應輪詢、每日重設峰值，以及英文／繁體中文介面。
- 可將程式本次執行期間的所有取樣值匯出為時間序列 CSV；亦可啟用每日背景 CSV 紀錄。
- 在硬體支援時顯示 CPU 熱降頻狀態；無法直接讀取 MSR 狀態時使用溫度判定作為備援。
- 針對已知 LG gram 機型提供唯讀 ACPI EC 風扇轉速讀取；WinMonitor 不會寫入 EC register。

## 系統需求

- Windows 10 或 Windows 11 x64
- 執行 framework-dependent 版本需安裝 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- 從原始碼建置需安裝 .NET 10 SDK
- 建議以系統管理員身分執行，以取得較完整的 CPU、NVMe SMART 與 EC 感測器資訊
- 支援需權限或 EC 遙測時，需安裝 [PawnIO 2.2.0 或更新版本](https://github.com/namazso/PawnIO.Setup/releases)

## 從原始碼執行

```powershell
dotnet run --project .\src\WinMonitor\WinMonitor.csproj
```

若要檢查硬體感測器，請在系統管理員終端機中執行：

```powershell
dotnet run --project .\tools\SensorDump\SensorDump.csproj
```

## 建置、測試與發佈

```powershell
dotnet build .\src\WinMonitor\WinMonitor.csproj -c Release
dotnet run --project .\tests\WinMonitor.Tests\WinMonitor.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

`publish.ps1` 會在 `dist\` 建立安裝版與可攜版。可攜版將設定檔存放於執行檔旁；安裝版則使用 `%AppData%\WinMonitor`。

## 專案結構

| 路徑 | 用途 |
|---|---|
| `src/WinMonitor` | WinForms 主程式、感測器服務、UI、系統匣整合與設定管理 |
| `tests/WinMonitor.Tests` | 不依賴額外測試套件的迴歸測試程式 |
| `tools/SensorDump` | 需提高權限執行的感測器與 EC 診斷工具 |
| `docs` | 特定硬體說明與風扇遙測指南 |
| `ARCHITECTURE.md` | 模組契約、執行緒規則與資料流程 |

## 硬體注意事項

可取得的感測器取決於硬體、韌體、執行權限與驅動程式版本。目前內建的 LG 風扇設定對應已知 `16T90R`／gram 360 韌體欄位；其他系統可能需要新增唯讀設定檔。回報感測器缺失前，請先使用 `SensorDump` 取得診斷資料。

## 授權

WinMonitor 自有原始碼之著作權為 Copyright (c) 2026 Michael Lin，並以 [MIT License](LICENSE) 開放使用。第三方元件仍適用各自的授權；確切版本、原始碼位置及隨程式散布的授權全文，請參閱 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 與 `licenses/`。Release 壓縮檔會包含這些文件，重新散布 WinMonitor 時必須一併保留。
