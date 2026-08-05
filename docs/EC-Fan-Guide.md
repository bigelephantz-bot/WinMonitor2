# LG gram EC 風扇監測

WinMonitor 透過 PawnIO **唯讀**存取 ACPI Embedded Controller（EC）。主程式不提供
EC 探索器；已確認機型會由 `KnownEcProfiles` 自動套用風扇 register。

## 已確認映射

| 機型 | BIOS 系列 | Register | 解讀 |
|---|---|---|---|
| LG gram 360 `16T90R` | `GP`（實測 `GP121`） | `0xB0` + `0xB1` | LE16 直讀 RPM |

ACPI DSDT 將 `0xB0`、`0xB1` 分別命名為 `RPM1`、`RPM2`。實機唯讀取樣約為
4300 RPM；反向解為 BE16 會超過 53000 RPM，因此使用 LE16。程式僅在產品名稱、
主機板、產品家族與 BIOS 系列全部吻合時加入此感測器。

## 執行需求

- 以系統管理員身分執行 WinMonitor。
- 安裝官方簽章版 PawnIO。
- 發佈目錄須包含 `pawnio\LpcACPIEC.bin`。

程式只送出 ACPI `RD_EC` 讀取命令，絕不寫入 EC register。請勿加入任何 EC 寫入功能；
錯誤寫值可能影響風扇或散熱控制。

## 驗證

啟動發佈版後，主視窗「風扇」群組應顯示「CPU 風扇」及合理 RPM。也可執行：

```powershell
dotnet run --project tools\SensorDump\SensorDump.csproj -c Release -- --report
```

## 支援其他機型

開發者可先掃描 ACPI EC 欄位，再以閒置／負載／冷卻三階段進行唯讀取樣：

```powershell
dotnet run --project tools\SensorDump\SensorDump.csproj -c Release -- --acpi-ec-fields
dotnet run --project tools\SensorDump\SensorDump.csproj -c Release -- --ec-probe --idle 20 --load 60 --cooldown 60 --out ec-probe.json
```

若已有候選 register，可加上 `--registers 0xB0,0xB1` 限縮讀取範圍。確認 register、
位元序與換算方式後，在 `src\WinMonitor\Config\KnownEcProfiles.cs` 新增精確機型條件；
不要對未知機型套用通用映射。
