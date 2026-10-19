# Crossingway

[Crossingway](https://github.com/PlusoneChiang/Crossingway) 的台服移植版。在遊戲畫面內渲染瀏覽器疊加層（overlay）的插件，讓你在全螢幕（含 G-SYNC）下仍能顯示 ACT／IINACT 一類的網頁疊加層。

Crossingway 本身 fork 自 [Browsingway](https://github.com/Styr1x/Browsingway)，而 Browsingway 源自 ackwell 的 BrowserHost 插件。

## 功能

- DPI 感知：疊加層依螢幕 DPI 正確縮放，顯示比例與瀏覽器一致
- 縮放：可像瀏覽器一樣個別放大縮小每個疊加層
- 透明度：可調整每個疊加層的不透明度
- 影格率：可個別設定每個疊加層的渲染影格率
- 鎖定、隱藏、穿透輸入（Type Through）、穿透點擊（Click Through）、全螢幕：每個疊加層各自設定
- 自動隱藏：非戰鬥中隱藏、PvP 中隱藏，可設定隱藏延遲
- 自訂 CSS：每個疊加層可另外套用自己的 CSS
- 停用：可完全停用某個疊加層而不用刪除它
- 靜音：可個別靜音疊加層
- ACT／IINACT 最佳化：依 ACT／IINACT 是否正在執行自動啟用／停用對應的疊加層
- Linux 支援（實驗性，不提供官方支援）

## 指令

- `/cw config`：開啟設定視窗
- `/cw overlay <疊加層指令名> <設定> <值>`：從聊天列改某個疊加層的設定
  - 設定：`url`、`locked`、`hidden`、`typethrough`、`clickthrough`、`fullscreen`、`muted`、`disabled`、`act`、`reload`
  - 開關類設定的值：`on`／`off`／`toggle`
  - 疊加層指令名＝設定視窗裡該疊加層的「指令名稱」欄

## ACT 疊加層設定

需開啟 ACT 的 Overlay WSServer，並用 ACT 的 URL 產生器建立對應網址；也建議對個別疊加層開啟「ACT 最佳化」。

## 台服版的修改

- 介面繁體中文化（設定視窗與第一次啟用時的相依套件下載流程）。
- 修正更新或停用 Crossingway 之後過幾分鐘遊戲崩潰的問題。
- 修正關閉遊戲時的執行緒問題；以系統管理員身分執行遊戲的提示不再記成錯誤。
- 降低記憶體用量。

## Linux 疑難排解

- 刪除 `~/.xlcore/pluginConfigs/Crossingway` 底下的內容可讓插件在下次啟動時重新安裝 CEF
  （切換 Wine/Proton 版本後常需要這麼做）
- Wine/Proton 版本變更時，可能需要在 XIVLauncher 設定的「Wine」分頁清除對應的 prefix

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
並啟用，再從插件列表安裝。

原作者：[PlusoneChiang](https://github.com/PlusoneChiang)（Crossingway）、[Styr1x](https://github.com/Styr1x)（Browsingway）
