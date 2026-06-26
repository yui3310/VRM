# VRM MediaPipe Holistic 驅動

實時鏡頭動作捕捉，把 MediaPipe Holistic 偵測到的全身姿勢、手指與臉部表情映射到 VRM 1.0 模型上。偵測完全在 Unity 內執行（透過 MediaPipe Unity Plugin），不需要外部 Python 程序；同時保留一條可選的 Python + UDP 路徑作為備援。

## 環境需求

| 項目 | 版本 |
| --- | --- |
| Unity | 2022.3.22f1 (LTS) |
| MediaPipe Unity Plugin (`com.github.homuler.mediapipe`) | 0.16.3 |
| UniVRM10 (`com.vrmc.vrm` / `com.vrmc.gltf`) | 0.131.1 |
| 平台 | Windows (Editor / Standalone)，CPU 推論 |

其他相依（Unity 內建或專案既有）：`com.unity.animation.rigging` 1.2.1、`com.unity.textmeshpro` 3.0.6、TMP/ugui 等標準模組。

> MediaPipe Unity Plugin 與 UniVRM 在本專案中是以 embedded package 放在 `Packages/` 底下，不在 `Packages/manifest.json` 的依賴清單裡。升級時請直接替換 `Packages/com.github.homuler.mediapipe` 與 `Packages/com.vrmc.*` 資料夾。

## 內容物

所有腳本位於 `Assets/vrm_convert/`，命名空間 `CCMediaPipe`：

| 檔案 | 說明 |
| --- | --- |
| `VRMHolisticController.cs` | 主控制器（MonoBehaviour）。選擇偵測來源、把每幀 landmark 套用到 VRM。 |
| `MediaPipeHolisticDetector.cs` | 在 Unity 內跑 MediaPipe Holistic（Tasks API、LIVE_STREAM），輸出 `HolisticFrame`。 |
| `HolisticToVRMBoneMapper.cs` | 把 landmark 轉成 Humanoid 骨骼旋轉（身體、四肢、手指、臉部表情）。 |
| `HolisticLandmarks.cs` | `HolisticFrame` 資料結構與 MediaPipe landmark 索引常數。 |
| `IMediaPipeReceiver.cs` | 偵測來源的共用介面。 |
| `UdpMediaPipeReceiver.cs` | 可選：接收 Python sender 透過 UDP 傳來的 landmark。 |
| `mediapipe_holistic_sender.py` | 可選：外部 Python 偵測器（UDP 路徑使用）。 |

Holistic 模型檔已內含於套件：`Packages/com.github.homuler.mediapipe/PackageResources/MediaPipe/holistic_landmarker.bytes`，編輯器中由 `LocalResourceManager` 直接載入，無需額外設定。

## 使用步驟

1. 在場景中放入 VRM 1.0 模型（需有 Humanoid 的 `Animator`，並掛有 `Vrm10Instance`）。
2. 建立一個空 GameObject，加上 `VRMHolisticController`。
3. 把 VRM 模型根物件拖到 `Vrm Model` 欄位。
4. 確認 `Detection Source` 設為 `UnityMediaPipe`（預設）。
5. 按 Play。第一次會要求鏡頭權限；偵測啟動後模型即跟著鏡頭動作。

`MediaPipeHolisticDetector` 會在執行時自動掛到同一個 GameObject 上，不需手動新增。

## Inspector 參數

`VRMHolisticController` 上的主要欄位：

- `Detection Source`：`UnityMediaPipe`（Unity 內偵測，預設）或 `Udp`（接收 Python sender）。
- `Web Cam Device Index`：使用第幾個鏡頭裝置（`UnityMediaPipe` 時）。
- `Udp Port`：與 Python sender 對應的埠號，預設 9999（`Udp` 時）。
- `Mirror X` / `Invert Y` / `Invert Z`：座標軸轉換，預設值對應一般正面鏡頭。
- `Landmark Scale`：world landmark 到 Unity 單位的縮放。
- `Body Smoothing` / `Hand Smoothing`：身體與手指旋轉的平滑度（0 = 即時，1 = 重平滑）。
- `Enable Face Blend Shapes`：用臉部 landmark 驅動 VRM 表情（眨眼、嘴型）。
- `Enable Hands`：驅動手指骨骼。

## 可選：Python + UDP 路徑

若要改用外部 Python 偵測：

1. 把 `Detection Source` 設為 `Udp`，並確認 `Udp Port`（預設 9999）。
2. 安裝 Python 相依（建議用虛擬環境）：`pip install mediapipe opencv-python numpy`。
3. 執行 `python mediapipe_holistic_sender.py`，再按 Unity 的 Play。

## 運作方式

- 偵測：`MediaPipeHolisticDetector` 以 `WebCamSource` + `TextureFramePool` 取得影像幀，丟給 `HolisticLandmarker`（LIVE_STREAM 非同步回呼）。結果（pose / hand 的 normalized 與 world landmark、face landmark）轉成 `HolisticFrame`。
- 映射：`HolisticToVRMBoneMapper` 在啟動時擷取模型 rest pose，之後每幀用「rest 方向 → 追蹤方向」的旋轉差來瞄準各骨骼。
  - 身體四肢：用 world landmark 的方向，與骨骼朝向無關的 rig-agnostic 瞄準。
  - 手指：先用手腕與指根建立手掌座標系（palm frame），把追蹤到的手指方向投影到模型手掌，並校正手腕本身的朝向，使手掌正對螢幕、彎曲角度正確。
  - 臉部：以眼睛長寬比推算眨眼、以嘴唇間距推算張嘴，透過 UniVRM10 的 `Blink` / `Aa` 表情輸出。

## 已知注意事項

- 編輯器與 Windows Standalone 使用 CPU 推論（套件原生庫設定）。
- MediaPipe 原生 glog 一個 process 只能初始化一次。`MediaPipeHolisticDetector` 在自己初始化 glog 時會於停止播放時配對呼叫 `Glog.Shutdown()`，確保可重複多次 Play 不閃退。若場景中已有 sample 的 `Bootstrap`，則沿用它的初始化，不重複呼叫。
- 表情功能需要 VRM 1.0（`Vrm10Instance`）。若找不到會自動停用表情，其餘映射照常運作。
