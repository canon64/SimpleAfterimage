# SimpleAfterimage

KoikatsuSunshine / KoikatsuSunshine_VR 用の BepInEx プラグインです。  
キャラのみを別カメラでキャプチャし、画面に重ね描きして残像表現を追加します。

## 特徴

- キャラレイヤーのみをキャプチャして残像表示
- 残像寿命、同時残像数、キャプチャ間隔を調整可能
- Tint RGBA とアルファ倍率で見た目を調整可能
- フェードカーブを切り替え可能
- HScene の速さに応じて残像寿命を自動調整可能
- プリセットを `presets.json` に保存
- 現在の設定を `config.json` に保存
- `config.json` を編集することで起動時設定を上書き可能

## 動作環境

- KoikatsuSunshine
- BepInEx 5
- .NET Framework 4.7.2

## 導入方法

1. `SimpleAfterimage.dll` を `BepInEx/plugins/` 配下の任意フォルダへ配置
2. ゲームを起動
3. 初回起動後、BepInEx の cfg が生成されます
4. プラグインフォルダに `config.json` と `presets.json` が生成されます

## 主な設定項目

### 01.一般
- `有効`
- `詳細ログ`

### 02.キャプチャ
- `残像寿命フレーム`
- `同時残像数`
- `キャプチャ間隔`
- `画面解像度を使う`
- `キャプチャ幅`
- `キャプチャ高さ`
- `キャラレイヤー名`

### 03.オーバーレイ
- `Tint R`
- `Tint G`
- `Tint B`
- `Tint A`
- `残像アルファ倍率`
- `フェードカーブ`
- `キャラ前面に表示`

### 04.元カメラ
- `Camera.main を優先`
- `カメラ名フィルタ`
- `カメラ fallback index`

### 05.プリセット
- `プリセット名`
- `プリセット操作`
- `速さ同期有効`
- `速さ最小時の残像寿命`
- `速さ最大時の残像寿命`

## config.json

`config.json` は現在設定の保存ファイルです。  
出力先はプラグイン DLL と同じフォルダです。
