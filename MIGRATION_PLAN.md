# YTB Downloader C#/.NET 移行プラン

## 概要

既存の Python 実装 (`E:\_library\projects\ytb-downloader`) を、C#/.NET の WPF デスクトップアプリとして移植する。
既存 Python 実装はそのまま維持する。

- **新規プロジェクト**: `E:\_library\projects\ytb-downloader-csharp`
- **UI**: WPF + NotifyIcon (`System.Windows.Forms.NotifyIcon`)
- **配布形式**: self-contained, single-file publish
- **ターゲット**: `net9.0-windows`（インストール済み SDK 9.0.321 に合わせる。SDK 10 導入後は `net10.0-windows` に変更可）

## 前提・制約

- Windows 専用（クリップボード、トレイ、Win32/WPF）。
- 外部ダウンロード実行は当面 `yt-dlp` + `ffmpeg` の既存バイナリ/CLI 呼び出しを維持する（C# 側はオーケストレーション/UI/設定/キューを担当）。将来、ライブラリ化や別実装に置換可能なように `IDownloadBackend` で抽象化する。
- 既存の `config.toml` / `archive.txt` / `downloads/` / `logs/` との互換性を意識する。

## 移植順

指定順に移植する。各段階で単体テストまたは最小動作確認を行う。

| 順 | コンポーネント | 目的 | 主な移植元 | 完了条件 |
|---:|---|---|---|---|
| 1 | `Queue` | FIFO ダウンロードキュー、優先度、状態通知 | `ytbdl/queue.py` | enqueue/pop/clear/pause/resume/status、ワーカー逐次実行、例外継続、archive skip |
| 2 | `Clipboard` | クリップボードから YouTube URL 抽出 | `ytbdl/clipboard.py` | Unicode/ANSI 取得、YouTube URL 正規表現抽出、末尾句読点除去 |
| 3 | `Core` | ダウンロード実行、yt-dlp 呼び出し、命名/メタデータ/ハッシュ | `ytbdl/core.py`, `ytbdl/naming.py`, `ytbdl/musicbrainz.py` | URL 正規化、playlist/index 判定、archive 判定、yt-dlp 実行、ログ、403 診断 |
| 4 | `TrayApp + ControlWindow` | トレイ常駐、右クリック制御ウィンドウ | `ytbdl/tray.py` | NotifyIcon、左クリック clipboard enqueue、右クリック ControlWindow、動的レイアウト、ボタン自動幅 |
| 5 | `ToastWindow` | 通知トースト | `ytbdl/tray.py` (`ToastWindow`) | 自動 dismiss、実行中更新、最前面非アクティブ表示 |
| 6 | `Config` | TOML 設定読込・既定値・検証 | `ytbdl/config.py`, `config.example.toml` | TOML 読込、パス解決、型検証、既定値、エラーメッセージ |
| 7 | `Main` | CLI/トレイ起動、引数解析、linklist | `main.py`, `ytbdl/cli.py` | URL/--mode/--config/--linklist/--tray、引数なしでトレイ、linklist 集計終了コード |

### 実装状況（2026-11-22）

| コンポーネント | 状態 | 備考 |
|---|---|---|
| Queue | 完了 | FIFO/priority/pause/resume/status、ワーカー逐次実行、例外継続、archive skip |
| Clipboard | 完了 | YouTube URL 抽出、末尾句読点除去 |
| Core | 完了 | yt-dlp CLI 実行、playlist/index/YouTube Music 判定、命名、メタデータ、MusicBrainz、音声ハッシュ、403 診断 |
| TrayApp + ControlWindow | 完了 | NotifyIcon、左クリック clipboard、右クリック制御ウィンドウ、動的レイアウト、ボタン自動幅 |
| ToastWindow | 完了 | 自動 dismiss、実行中更新、ControlWindow 上に配置 |
| Config | 完了 | Tomlyn による TOML 読込、パス解決、型/値検証、`yt_dlp_location` 追加 |
| Main/CLI | 完了 | URL/--mode/--config/--linklist/--tray、env URL、linklist 集計終了コード |

## 想定ディレクトリ構成

```text
ytb-downloader-csharp/
├── MIGRATION_PLAN.md
├── README.md
├── .gitignore
├── ytb-downloader-csharp.sln
└── src/
    └── YtbDownloader/
        ├── YtbDownloader.csproj
        ├── App.xaml
        ├── App.xaml.cs
        ├── Program.cs
        ├── Cli/
        │   ├── CliOptions.cs
        │   └── LinkListLoader.cs
        ├── Config/
        │   ├── AppConfig.cs
        │   └── ConfigLoader.cs
        ├── Core/
        │   ├── DownloadRequestInfo.cs
        │   ├── DownloadService.cs
        │   ├── IDownloadBackend.cs
        │   ├── YtDlpBackend.cs
        │   ├── NamingService.cs
        │   └── MusicBrainzGenreLookup.cs
        ├── Queue/
        │   ├── DownloadRequest.cs
        │   ├── DownloadQueue.cs
        │   └── QueueStatus.cs
        ├── Clipboard/
        │   └── ClipboardHelper.cs
        ├── Tray/
        │   ├── TrayApp.cs
        │   ├── ControlWindow.xaml
        │   ├── ControlWindow.xaml.cs
        │   ├── ToastWindow.xaml
        │   └── ToastWindow.xaml.cs
        └── Logging/
            └── LoggingSetup.cs
```

## 技術選定

| 領域 | 方針 | 備考 |
|---|---|---|
| UI | WPF (`UseWPF=true`) | ControlWindow/ToastWindow は XAML。動的レイアウトは WPF レイアウトパネルで実現。 |
| トレイ | `NotifyIcon` (`UseWindowsForms=true`) | Python の pywin32 tray 相当。WPF 単体には NotifyIcon がないため WinForms 併用。 |
| 設定 | TOML パーサ NuGet (`Tomlyn` 等) | `config.toml` 互換。パッケージ追加はビルド時に復元が必要。 |
| JSON | `System.Text.Json` | linklist 読込。 |
| ログ | `Microsoft.Extensions.Logging` + ファイル出力 or 簡易自前 | 既存の日時付きログファイル挙動を維持。 |
| プロセス実行 | `System.Diagnostics.Process` | yt-dlp/ffmpeg 呼び出し。 |
| 非同期/並行 | `Task`, `CancellationToken`, `lock`/`Channel` | Queue ワーカーは 1 並列。UI 更新は Dispatcher 経由。 |

## コンポーネント設計メモ

### 1. Queue

- Python の `DownloadRequest`, `QueueStatus`, `describe_item`, `DownloadQueue` を移植。
- `DownloadRequest` は可変状態（done/skipped/error/title/kind/video_id）を持つ。UI 表示用にスナップショット/通知を明確化する。
- ワーカーは `Task.Run` + `CancellationToken` または専用スレッド。**最大並列 1** を維持。
- `status_callback` は UI スレッドへマーシャル（WPF `Dispatcher.BeginInvoke`）。
- `priority=1` は先頭挿入（Run now）。
- 実行前に archive 済み動画 ID をスキップ。

### 2. Clipboard

- `System.Windows.Clipboard.GetText()` または Win32 clipboard API を使用。STA スレッド要件に注意。
- URL 抽出正規表現は既存 Python と同等: `youtube.com` / `music.youtube.com` / `youtu.be`。
- 末尾の句読点/括弧類トリムを維持。

### 3. Core

- 当面は `yt-dlp` CLI/実行ファイルを `Process` で呼ぶバックエンドを実装。
- Python 実装の責務を分割:
  - `YtDlpBackend`: 引数構築、プロセス実行、stdout/stderr ログ、終了コード。
  - `DownloadService`: URL 正規化、index/YouTube Music 判定、archive 判定、出力テンプレート、命名、後処理。
  - `NamingService`: `naming.py` の feat/remix/cover/アーティスト推定。
  - `MusicBrainzGenreLookup`: User-Agent、1 秒間隔、失敗時警告。
- 403 診断メッセージと `force_ipv4` の挙動を維持。
- 音声ストリームハッシュは ffmpeg `streamhash` + metadata 埋め込みを移植。

### 4. TrayApp + ControlWindow

- `TrayApp`: NotifyIcon 初期化、左クリックで clipboard enqueue、右クリックで ControlWindow 表示、ツールチップ更新。
- `ControlWindow`: 実行中/待機キュー/入力/Stack/Run now/Clear/Quit。
- **動的レイアウト**: WPF `Grid`/`StackPanel` + `SizeToContent` または再計測で、キュー表示量に応じて高さを調整。
- **ボタン自動幅**: `Button` の `Auto` 幅 + `Padding`、固定幅を避ける。日本語/英語併記ラベルでも崩れないこと。
- 入力解析は既存 `parse_stacked_input` と互換: `<URL> [--mode audio|video]`。

### 5. ToastWindow

- WPF `Window` (`WindowStyle=None`, `AllowsTransparency`, `Topmost`, `ShowActivated=false`)。
- 自動 dismiss タイマー。実行中項目の更新を反映。
- 作業領域右下、ControlWindow の上に配置。

### 6. Config

- `config.toml` 読込。既存キー互換を維持。
- パス解決: 設定ファイル基準、`ffmpeg_location` は app 基準。
- 型/値検証: `default_mode`, `audio_format`, bool, `audio_bitrate`, `audio_bit_depth`, `audio_sample_rate`。
- Windows パスの TOML エスケープエラーに分かりやすいメッセージ。

### 7. Main

- `System.CommandLine` は依存追加になるため、最初は軽量な自前 parser でもよい（要件が小さい）。
- 引数なし + Windows: トレイ起動。非 Windows は URL/linklist 必須エラー。
- `--linklist` は JSON 配列（文字列 or `{url, mode}`）。全件処理後に成功/スキップ/失敗集計、失敗があれば終了コード 1。
- `YTB_DOWNLOADER_URL` 環境変数フォールバックを維持。

## テスト方針

| 段階 | テスト |
|---|---|
| Queue | enqueue 順序、priority、clear/remove、pause/resume、worker 例外継続、archive skip、status snapshot |
| Clipboard | URL 抽出、末尾トリム、非 URL、空/null |
| Core | URL/index/Music Music 判定、output template、archive 判定、403 診断、naming、genre/hash は外部依存をモック |
| Tray/Toast | 主に手動 UI 確認。入力解析は単体テスト化。 |
| Config | 既定値、検証、パス解決、TOML エラー |
| Main/CLI | 引数解析、linklist 読込、終了コード、env URL |

## ビルド/発行

> **AGENTS.md 承認必要**: SDK インストール・ビルド/発行は 100MB 超の可能性があるため、実行前にユーザー承認が必要。

### 想定 publish コマンド

```powershell
dotnet publish src/YtbDownloader/YtbDownloader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

- 実測サイズ: **約 70.94MB**（self-contained, single-file, compression 有効）
- 参考: compression 無効時は **約 162.53MB**（WPF の self-contained single-file は大きくなりやすい）
- `EnableCompressionInSingleFile=true` を付けることで目標の 60-100MB に収まる
- 出力先: `src/YtbDownloader/bin/Release/net9.0-windows/win-x64/publish/`
- 追加で入れるもの: なし（ffmpeg/yt-dlp/config は実行環境に配置する方針。必要なら別途同梱設計）

## 検証結果（2026-11-22）

- SDK: `9.0.321`（`E:\app\dotnet`）
- TargetFramework: `net9.0-windows`
- publish コマンド: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- 出力: `YtbDownloader.exe` **70.96MB**（`EnableCompressionInSingleFile=true` を csproj に既定化）
- スモークテスト: 起動後 5 秒間クラッシュせず稼働（publish 済み exe でも確認）
- `--version` / `--help`: 終了コード 0
- 再現スクリプト: `scripts/smoke-test.ps1`
- publish 済み exe で audio 実ダウンロード・タグ付与を再確認
- 実ダウンロード確認（YouTube, audio）:
  - 出力: `Never Gonna Give You Up - Rick Astley.mp3`
  - archive: `youtube dQw4w9WgXcQ`
  - タグ: `title` / `track` / `artist` / `genre` / `AUDIO_STREAM_SHA256` を確認
  - `embed_album_art=true`: PNG カバーアート埋め込み、一時サムネイル削除を確認
  - 再実行: archive により「すべて取得済みです。」で終了コード 0
- 実ダウンロード確認（video）: 小さな MP4 で `--mode video` の保存・命名を確認
- linklist: 文字列形式と `{url, mode}` 形式の両方を処理し、成功/スキップ/失敗を集計（アーカイブ済み URL は network アクセス前にスキップ）
- トレイ: `--tray` 起動後 5 秒間クラッシュせず稼働
- MusicBrainz: 503 応答時に警告を出して処理継続（外部サービス依存のため）

## 100MB 超操作の承認が必要な項目

AGENTS.md に従い、以下は **実行前に明示承認** を得る。

| 操作 | 目的 | 推定容量 | 対象 | 削除可否 | 備考 |
|---|---|---:|---|---|---|
| .NET SDK インストール | C# プロジェクトの build/publish に必要 | ~300MB | `E:\app\dotnet` | 可（ディレクトリ削除） | 未インストールの場合のみ |
| `dotnet restore`/build | NuGet 復元・ビルド成果物生成 | ~100MB+（累積見込み） | `E:\DevCache` (NuGet) + プロジェクト `bin/obj` | 可 | キャッシュ含む累積 |
| `dotnet publish` | self-contained single-file 配布物生成 | ~60-100MB | `src/YtbDownloader/bin/.../publish` | 可 | Release 成果物 |

## 既知リスク/確認事項

- **yt-dlp の扱い**: Python 版は yt-dlp Python ライブラリを直接使用。C# 版は CLI 呼び出しにすると挙動差分が出る可能性。`info-json`/進捗出力の解析設計が必要。
- **WPF NotifyIcon**: WinForms 併用の初期化順・Dispatcher/STA に注意。
- **TOML 互換**: `Tomlyn` 等で既存 `config.toml` を読めることを確認。
- **Single-file + WPF**: trimming は WPF で問題が出やすい。まずは `PublishSingleFile=true` のみ、trim は無効で開始。
- **SDK バージョン**: 現環境は SDK 9.0.321 のため `net9.0-windows` を使用。`net10.0-windows` にする場合は SDK 10 の追加インストール（承認必要）後に変更する。
- **パス/文字コード**: Windows パス、日本語ログ/ファイル名、ffmpeg/yt-dlp の出力エンコード。
- **既存ユーザーデータ**: `archive.txt`, `config.toml`, `downloads`, `logs` の配置/互換を壊さない。

## 次の実装ステップ

1. 実運用の `config.toml` / `yt-dlp.exe` / `ffmpeg` 配置で最終確認する（開発環境での確認は完了）。
2. 必要に応じて C# 側の自動テストプロジェクトを追加する（現状は Python テストを仕様参照）。
3. 旧 Python 版との並行運用で、プレイリスト/カバー曲/YouTube Music の命名差分を実データで確認する。
4. SDK 10 導入後に `net10.0-windows` へ切り替える場合は、再度 publish とサイズ確認を行う（承認が必要な場合は AGENTS.md に従う）。
