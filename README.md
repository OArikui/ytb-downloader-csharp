# ytb-downloader-csharp

Python 実装 `ytb-downloader` の C#/.NET (WPF + NotifyIcon) 移行プロジェクト。

## 状態

- 移植順 `queue → clipboard → core → TrayApp+ControlWindow → ToastWindow → config → main` の実装が完了。
- 既存 Python 実装は `E:\_library\projects\ytb-downloader` にそのまま維持。
- yt-dlp CLI + ffmpeg を外部実行する構成（`IDownloadBackend` で抽象化）。

## 要件

- Windows
- .NET SDK 9.0（`net9.0-windows`。SDK 10 導入時は `TargetFramework` を `net10.0-windows` に変更可）
- 実行時: `yt-dlp`, `ffmpeg`/`ffprobe`（既存運用に合わせて配置）

## ビルド

> **注意**: .NET SDK インストール/restore/build/publish はディスク使用量が 100MB を超える可能性があります。実行前に AGENTS.md の承認手順に従ってください。

```powershell
dotnet build src/YtbDownloader/YtbDownloader.csproj -c Release
```

## Publish

`src/YtbDownloader` で実行します。

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

`EnableCompressionInSingleFile=true` は csproj に既定化しています。  
実測出力サイズ: 約 70.96MB（self-contained, single-file, compression 有効）。  
compression 無効時は約 162.53MB でした。  
publish 済み exe で `--version` / `--help` / 実ダウンロード / トレイ起動の動作確認済み。

## リリース（CI）

`v*` タグを push すると、GitHub Actions（`.github/workflows/release.yml`）が自動でビルドと同梱を行います。

```powershell
git tag v0.1.9
git push origin v0.1.9
```

CI の処理内容:

1. タグからバージョンを取得し、`VersionInfo.cs` の `AppVersion` と exe のバージョンに反映
2. `win-x64` / self-contained / single-file で publish
3. `YtbDownloader.exe`、`start-tray.bat`、`config.example.toml`、`README.md` を zip に同梱
4. SHA-256 チェックサムを生成
5. GitHub Release を作成し、zip とチェックサムを添付

- 成果物: `YtbDownloader-vX.Y.Z-win-x64.zip`
- ハイフン付きタグ（例: `v0.2.0-beta.1`）は prerelease として公開
- `yt-dlp` / `ffmpeg` は同梱しません。PATH 上に置くか、`config.toml` の `yt_dlp_location` / `ffmpeg_location` で指定してください
- zip 内の `start-tray.bat` は同じ階層の `YtbDownloader.exe` を起動します

## スモークテスト

publish 済みの exe を起動し、一定時間クラッシュせずに動作するか確認します。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

## 実装状況

| コンポーネント | 状態 |
|---|---|
| Queue | 実装済み（FIFO/priority/pause/resume/status、archive skip、例外継続） |
| Clipboard | 実装済み（YouTube URL 抽出、末尾句読点除去） |
| Core | 実装済み（yt-dlp 実行、playlist/index、Python 版準拠の audio 命名、メタデータ、MusicBrainz、音声ハッシュ、403 診断） |
| TrayApp + ControlWindow | 実装済み（NotifyIcon、タイトル先行取得、単動画の優先実行、件数/進捗/速度表示、index 付きスクロール一覧、yt-dlp オプション透過） |
| ToastWindow | 実装済み（自動 dismiss、実行中更新、ControlWindow 上に配置） |
| Config | 実装済み（Tomlyn による TOML 読込、パス解決、型/値検証） |
| Main/CLI | 実装済み（URL/--mode/--config/--linklist/--tray、env URL、アーカイブ事前スキップ、集計終了コード、トレイ起動時の yt-dlp オプション透過） |

## 使い方

```powershell
# 単体ダウンロード
.\YtbDownloader.exe "https://www.youtube.com/watch?v=..." --mode audio

# リンクリスト
.\YtbDownloader.exe --linklist links.json

# トレイアプリ（URL/--linklist なしで起動）
.\YtbDownloader.exe
```

トレイ起動用バッチもあります（`scripts\start-tray.bat`）。コンソールを開いたままにせず起動します。

```powershell
.\scripts\start-tray.bat
.\scripts\start-tray.bat PATH\config.toml   # 設定ファイルを明示指定する場合
```

### トレイのキュー

- キューに追加すると、ダウンロードとは別にタイトルを先行取得し、タイトルで表示します。
- プレイリストと単動画を区別し、待機中の単動画はプレイリストより先に処理します（実行中の項目は完了してから切り替わります）。
- 実行中項目の進捗率・速度・ETA と、完了/待ち件数を表示します。
- キュー一覧は index 付きでスクロールできるため、大量に積んでも見やすくなっています。
- `tray_issues.txt` には失敗・曲名不明・ジャンル不明を記録します。MusicBrainz の `Music` はジャンルとして意味を成さないため、ジャンル不明として記録します。

### トレイ入力のオプション

入力形式は `<YouTube URL> [--mode audio|video|--audio|--video] [--yt-dlp options]` です。
`--mode` / `--audio` / `--video` 以外のオプション（`--list-formats` や `--proxy URL` など）は
yt-dlp へそのまま渡されます。起動時に渡した未知の long option も、トレイの全タスクに適用されます。
なお、出力先やアーカイブなど内部で使用するオプションは上書きされないよう除外されます。

```powershell
.\YtbDownloader.exe --tray --proxy http://proxy.example:8080
```

`config.toml` は実行ファイルと同じディレクトリに置きます。`--config` で明示指定も可能です。
`yt-dlp` と `ffmpeg` は PATH 上、または `yt_dlp_location` / `ffmpeg_location` で指定します。

## ドキュメント

- [MIGRATION_PLAN.md](./MIGRATION_PLAN.md)
