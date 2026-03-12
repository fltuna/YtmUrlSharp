# YTM URL Sharp

VRChat の動画プレイヤーで再生に失敗した YouTube 動画のストリーム URL を自動抽出するツールです。  
クリップボードへの YouTube URL コピーにも対応しており、デスクトップウィンドウと SteamVR DashboardUIの両方で操作できます。

## 機能

- VRChat の動画再生失敗を自動検出し、ストリーム URL を抽出
- クリップボードにコピーした YouTube URL を自動検出・処理
- yt-dlp の自動ダウンロード・更新
- 変換履歴の保存・復元(最大 50 件)
- ストリーム種別によるフィルター表示
- SteamVR DashboardUI 対応

## 必要環境

- Windows 10/11
- .NET 10 SDK
- (任意)SteamVR — Dashboard機能を使用する場合

## ビルド・実行

ビルド済みバイナリの配布は行っていません。各自でビルドしてください。

```bash
dotnet publish src -c Release -r win-x64 --self-contained
```

`src/bin/Release/net10.0-windows/win-x64/publish/` に .NET ランタイム込みの実行ファイルが生成されます。

一応起動時に `--console` フラグを付けるとコンソールウィンドウが表示され、デバッグログを確認できます。

## セキュリティに関する留意事項

本ツールは利便性のために以下の動作を行います。利用前にご理解ください。

### 概要

- クリップボードを監視します — YouTube URL がコピーされたかどうかを確認するためです。YouTube URL 以外の内容(パスワードや個人情報など)を保存・送信することはありません
- yt-dlp を自動でダウンロード・実行します — YouTube の動画情報を取得するためのツールです。公式サイトからのみダウンロードし、改ざん検知のためのハッシュ検証も行います
- VRChat のログファイルを読み取ります — 再生失敗した動画を自動検出するためです。ログの内容を外部に送信することはありません
- 通信先は YouTube と GitHub のみです — 本ツール独自のサーバーへの通信は一切行いません
- 取得したストリーム URL にはあなたの IP アドレスが含まれます — 第三者への共有にはご注意ください

本ツールの使用は自己責任でお願いします。

<details>
<summary>技術的な詳細</summary>

何してるかめっちゃ端的に以下にまとめてます

- クリップボード監視 — Win32 `AddClipboardFormatListener` によるイベント駆動。YouTube URL 単体の場合のみ処理
- yt-dlp ダウンロード — [yt-dlp GitHub Releases](https://github.com/yt-dlp/yt-dlp/releases)から `%LOCALAPPDATA%\YtmUrlSharp\` に保存。SHA-256 ハッシュ検証による自動更新つけてます
- VRChat ログ読み取り — 読み取り専用・非排他で開き、起動後に追記された新規行のみ処理するようにしてます
- ネットワーク通信先 — `youtube.com` / `googlevideo.com`(ストリーム取得)、`github.com`(yt-dlp ダウンロード・ハッシュ検証)のみにしてます

</details>

## 使用ライブラリ

- [YoutubeExplode](https://github.com/Tyrrrz/YoutubeExplode) — YouTube ストリーム情報の取得
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) — YouTube 直接ストリーム URL の取得(実行時に自動ダウンロード)
- [SkiaSharp](https://github.com/mono/SkiaSharp) — UI レンダリング
- [OVRSharp](https://github.com/OVRTools/OVRSharp) — SteamVR DashboardUI 操作
- [TextCopy](https://github.com/CopyText/TextCopy) — クリップボード操作
- [Microsoft.Extensions.Logging](https://github.com/dotnet/runtime) — ログ出力

## ライセンス

MIT
