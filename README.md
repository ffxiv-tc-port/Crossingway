# Crossingway

在遊戲畫面內渲染瀏覽器疊加層（overlay）的插件，讓你在全螢幕（含 G-SYNC）下仍能顯示 ACT 一類的網頁疊加層。

本專案 fork 自 ackwell 的 BrowserHost 插件。

## 功能

- DPI 感知：疊加層依螢幕 DPI 正確縮放，顯示比例與瀏覽器一致
- 縮放：可像瀏覽器一樣個別放大縮小每個疊加層
- 透明度：可調整每個疊加層的不透明度
- 影格率：可個別設定每個疊加層的渲染影格率
- 停用：可完全停用某個疊加層而不用刪除它
- 靜音：可個別靜音疊加層
- ACT 最佳化：依 ACT 是否正在執行自動啟用／停用對應的疊加層
- Linux 支援（實驗性，不提供官方支援）

## ACT 疊加層設定

需開啟 ACT 的 Overlay WSServer，並用 ACT 的 URL 產生器建立對應網址；也建議對個別疊加層開啟「ACT 最佳化」。

## Linux 疑難排解

- 刪除 `~/.xlcore/pluginConfigs/Crossingway` 底下的內容可讓插件在下次啟動時重新安裝 CEF
  （切換 Wine/Proton 版本後常需要這麼做）
- Wine/Proton 版本變更時，可能需要在 XIVLauncher 設定的「Wine」分頁清除對應的 prefix

## 安裝

在 Dalamud 設定的「自訂插件庫」加入
`https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
並啟用，再從插件列表安裝。
