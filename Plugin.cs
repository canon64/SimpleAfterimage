using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SimpleAfterimage
{
    [BepInPlugin(Guid, PluginName, Version)]
    [BepInProcess("KoikatsuSunshine")]
    [BepInProcess("KoikatsuSunshine_VR")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.kks.maingame.simpleafterimage";
        public const string PluginName = "SimpleAfterimage";
        public const string Version = "0.1.0";

        public static Plugin Instance { get; private set; }

        // 描画用 MonoBehaviour
        private sealed class OverlayDrawer : MonoBehaviour
        {
            internal Plugin Owner;
            private void OnPostRender() { Owner?.DrawOnPostRender(); }
        }

        // プリセットデータ
        private class PresetData
        {
            public int    FadeFrames      = 30;
            public int    MaxSlots        = 30;
            public int    CaptureInterval = 1;
            public float  TintR           = 1f;
            public float  TintG           = 1f;
            public float  TintB           = 1f;
            public float  TintA           = 1f;
            public float  AlphaScale      = 1f;
            public string FadeCurve       = "Linear";
            public bool   FrontOfCharacter = true;
        }

        // Config
        private ConfigEntry<bool>   _cfgEnabled;
        private ConfigEntry<bool>   _cfgVerboseLog;
        private ConfigEntry<int>    _cfgFadeFrames;
        private ConfigEntry<int>    _cfgMaxSlots;
        private ConfigEntry<int>    _cfgCaptureInterval;
        private ConfigEntry<bool>   _cfgUseScreenSize;
        private ConfigEntry<int>    _cfgCaptureWidth;
        private ConfigEntry<int>    _cfgCaptureHeight;
        private ConfigEntry<string> _cfgCharaLayer;
        private ConfigEntry<float>  _cfgTintR;
        private ConfigEntry<float>  _cfgTintG;
        private ConfigEntry<float>  _cfgTintB;
        private ConfigEntry<float>  _cfgTintA;
        private ConfigEntry<float>  _cfgAlphaScale;
        private ConfigEntry<string> _cfgFadeCurve;
        private ConfigEntry<bool>   _cfgFrontOfCharacter;
        private ConfigEntry<bool>   _cfgPreferCameraMain;
        private ConfigEntry<string> _cfgCameraNameFilter;
        private ConfigEntry<int>    _cfgCameraFallbackIndex;
        private ConfigEntry<string> _cfgPresetName;
        private ConfigEntry<string> _cfgPresetAction;
        private ConfigEntry<bool>   _cfgBeatSyncFadeEnabled;
        private ConfigEntry<int>    _cfgBeatSyncFadeMin;
        private ConfigEntry<int>    _cfgBeatSyncFadeMax;

        // Runtime
        private Camera _captureCamera;
        private Camera _lastSourceCamera;
        private OverlayDrawer _overlayDrawer;
        private GameObject _cameraRoot;
        private RenderTexture[] _slots;
        private int[] _life;
        private int _writeIndex;
        private int _frameCounter;
        private int _characterMask;
        private int _rtWidth;
        private int _rtHeight;

        private RenderTexture[] _drawSlots;
        private float[] _drawAlpha;
        private int _drawCount;

        // HScene速さ連携
        private HSceneProc _hSceneProc;
        private float _nextHSceneScanTime;

        // プリセット
        private string _presetsPath;
        private string _configJsonPath;
        private Dictionary<string, PresetData> _presets = new Dictionary<string, PresetData>();
        private string _pendingAction;

        private void Awake()
        {
            Instance = this;
            string pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            _presetsPath = Path.Combine(pluginDir, "presets.json");
            _configJsonPath = Path.Combine(pluginDir, "config.json");
            LoadPresetsFile();
            SetupConfig();
            ApplyConfig();
            SaveConfigJson();
            Logger.LogInfo($"{PluginName} {Version} loaded.");
        }

        private void Update()
        {
            if (_pendingAction == null) return;
            string action = _pendingAction;
            _pendingAction = null;

            string name = _cfgPresetName.Value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                Logger.LogWarning("プリセット名が空です");
                _cfgPresetAction.Value = "なし";
                return;
            }

            if (action == "保存")
            {
                SavePreset(name);
                Logger.LogInfo($"プリセット保存: {name}");
            }
            else if (action == "読込")
            {
                if (!LoadPreset(name))
                    Logger.LogWarning($"プリセットが見つかりません: {name}");
                else
                    Logger.LogInfo($"プリセット読込: {name}");
            }

            _cfgPresetAction.Value = "なし";
        }

        private void SetupConfig()
        {
            const string cat1 = "01.一般";
            const string cat2 = "02.キャプチャ";
            const string cat3 = "03.オーバーレイ";
            const string cat4 = "04.元カメラ";
            const string cat5 = "05.プリセット";

            _cfgEnabled         = Config.Bind(cat1, "有効",             true,  "機能の有効/無効");
            _cfgVerboseLog      = Config.Bind(cat1, "詳細ログ",         false, "詳細ログを出力する");
            _cfgFadeFrames      = Config.Bind(cat2, "残像寿命フレーム", 120,    new ConfigDescription("残像が消えるまでのフレーム数", new AcceptableValueRange<int>(1, 300)));
            _cfgMaxSlots        = Config.Bind(cat2, "同時残像数",       120,    new ConfigDescription("同時に保持する残像スロット数", new AcceptableValueRange<int>(1, 300)));
            _cfgCaptureInterval = Config.Bind(cat2, "キャプチャ間隔",   1,     new ConfigDescription("何フレームごとにキャプチャするか(1=毎フレーム)", new AcceptableValueRange<int>(1, 60)));
            _cfgUseScreenSize   = Config.Bind(cat2, "画面解像度を使う", true,  "キャプチャサイズに画面解像度を使う");
            _cfgCaptureWidth    = Config.Bind(cat2, "キャプチャ幅",     0,     new ConfigDescription("UseScreenSize=false時のキャプチャ幅", new AcceptableValueRange<int>(0, 8192)));
            _cfgCaptureHeight   = Config.Bind(cat2, "キャプチャ高さ",   0,     new ConfigDescription("UseScreenSize=false時のキャプチャ高さ", new AcceptableValueRange<int>(0, 8192)));
            _cfgCharaLayer      = Config.Bind(cat2, "キャラレイヤー名", "Chara", "キャプチャ対象のレイヤー名");
            _cfgTintR           = Config.Bind(cat3, "色R",             0.5f,    new ConfigDescription("残像色 R (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintG           = Config.Bind(cat3, "色G",             0.5f,    new ConfigDescription("残像色 G (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintB           = Config.Bind(cat3, "色B",             0.5f,    new ConfigDescription("残像色 B (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintA           = Config.Bind(cat3, "色A",             1f,    new ConfigDescription("残像色 A (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgAlphaScale      = Config.Bind(cat3, "残像アルファ倍率", 0.03f,   new ConfigDescription("残像の全体濃度スケール(0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgFadeCurve       = Config.Bind(cat3, "フェードカーブ",   "Linear", new ConfigDescription("Linear=線形 / EaseIn=最初ゆっくり後半急 / EaseOut=最初急後半ゆっくり / Square=三乗", new AcceptableValueList<string>("Linear", "EaseIn", "EaseOut", "Square")));
            _cfgFrontOfCharacter = Config.Bind(cat3, "キャラ前面に表示", true, "true=キャラの前面 / false=キャラの背面");
            _cfgPreferCameraMain    = Config.Bind(cat4, "Camera.main優先",       true, "Camera.mainを優先する");
            _cfgCameraNameFilter    = Config.Bind(cat4, "カメラ名フィルタ",       "",   "カメラ名の部分一致フィルタ(空なら無効)");
            _cfgCameraFallbackIndex = Config.Bind(cat4, "カメラ候補フォールバック", 0,  new ConfigDescription("候補カメラのフォールバックインデックス", new AcceptableValueRange<int>(0, 64)));
            _cfgPresetName         = Config.Bind(cat5, "プリセット名", "default", "保存・読込するプリセット名");
            _cfgPresetAction       = Config.Bind(cat5, "プリセット操作", "なし", new ConfigDescription("保存/読込を選んで実行", new AcceptableValueList<string>("なし", "保存", "読込")));
            _cfgBeatSyncFadeEnabled = Config.Bind(cat5, "速さ同期有効", false, "BeatSyncの速さスライダーに残像寿命フレームを同期する");
            _cfgBeatSyncFadeMin    = Config.Bind(cat5, "速さ最小時FadeFrames", 120,  new ConfigDescription("速さ0の時の残像寿命フレーム", new AcceptableValueRange<int>(1, 300)));
            _cfgBeatSyncFadeMax    = Config.Bind(cat5, "速さ最大時FadeFrames", 60, new ConfigDescription("速さ1の時の残像寿命フレーム", new AcceptableValueRange<int>(1, 300)));

            Config.SettingChanged += (_, e) =>
            {
                if (e.ChangedSetting == _cfgPresetAction && _cfgPresetAction.Value != "なし")
                    _pendingAction = _cfgPresetAction.Value;
                else
                    ApplyConfig();
            };
        }

        private void ApplyConfig()
        {
            _characterMask = LayerMask.GetMask(_cfgCharaLayer.Value ?? "Chara");

            int newSlots = Mathf.Clamp(_cfgMaxSlots.Value, 1, 300);
            int newW = _cfgUseScreenSize.Value || _cfgCaptureWidth.Value <= 0  ? Screen.width  : _cfgCaptureWidth.Value;
            int newH = _cfgUseScreenSize.Value || _cfgCaptureHeight.Value <= 0 ? Screen.height : _cfgCaptureHeight.Value;
            newW = Mathf.Max(16, newW);
            newH = Mathf.Max(16, newH);

            bool needRebuild = _slots == null
                || _slots.Length != newSlots
                || _rtWidth != newW
                || _rtHeight != newH;

            if (needRebuild)
            {
                ReleaseSlots();
                _slots     = new RenderTexture[newSlots];
                _life      = new int[newSlots];
                _drawSlots = new RenderTexture[newSlots];
                _drawAlpha = new float[newSlots];
                for (int i = 0; i < newSlots; i++)
                    _slots[i] = CreateRT(newW, newH);
                _rtWidth    = newW;
                _rtHeight   = newH;
                _writeIndex = 0;
                _frameCounter = 0;
                if (_cfgVerboseLog.Value)
                    Logger.LogInfo($"slots rebuilt: {newW}x{newH} slots={newSlots}");
            }

            if (_cameraRoot == null)
                SetupCaptureCamera();
        }

        // ---- プリセット公開API ----

        public bool SavePreset(string name)
        {
            _presets[name] = new PresetData
            {
                FadeFrames       = _cfgFadeFrames.Value,
                MaxSlots         = _cfgMaxSlots.Value,
                CaptureInterval  = _cfgCaptureInterval.Value,
                TintR            = _cfgTintR.Value,
                TintG            = _cfgTintG.Value,
                TintB            = _cfgTintB.Value,
                TintA            = _cfgTintA.Value,
                AlphaScale       = _cfgAlphaScale.Value,
                FadeCurve        = _cfgFadeCurve.Value,
                FrontOfCharacter = _cfgFrontOfCharacter.Value,
            };
            SavePresetsFile();
            return true;
        }

        private void SaveConfigJson()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"Enabled\": {(_cfgEnabled.Value ? "true" : "false")},");
                sb.AppendLine($"  \"VerboseLog\": {(_cfgVerboseLog.Value ? "true" : "false")},");
                sb.AppendLine($"  \"FadeFrames\": {_cfgFadeFrames.Value},");
                sb.AppendLine($"  \"MaxSlots\": {_cfgMaxSlots.Value},");
                sb.AppendLine($"  \"CaptureInterval\": {_cfgCaptureInterval.Value},");
                sb.AppendLine($"  \"UseScreenSize\": {(_cfgUseScreenSize.Value ? "true" : "false")},");
                sb.AppendLine($"  \"CaptureWidth\": {_cfgCaptureWidth.Value},");
                sb.AppendLine($"  \"CaptureHeight\": {_cfgCaptureHeight.Value},");
                sb.AppendLine($"  \"CharaLayer\": \"{Esc(_cfgCharaLayer.Value)}\",");
                sb.AppendLine($"  \"TintR\": {_cfgTintR.Value:0.####},");
                sb.AppendLine($"  \"TintG\": {_cfgTintG.Value:0.####},");
                sb.AppendLine($"  \"TintB\": {_cfgTintB.Value:0.####},");
                sb.AppendLine($"  \"TintA\": {_cfgTintA.Value:0.####},");
                sb.AppendLine($"  \"AlphaScale\": {_cfgAlphaScale.Value:0.####},");
                sb.AppendLine($"  \"FadeCurve\": \"{Esc(_cfgFadeCurve.Value)}\",");
                sb.AppendLine($"  \"FrontOfCharacter\": {(_cfgFrontOfCharacter.Value ? "true" : "false")},");
                sb.AppendLine($"  \"PreferCameraMain\": {(_cfgPreferCameraMain.Value ? "true" : "false")},");
                sb.AppendLine($"  \"CameraNameFilter\": \"{Esc(_cfgCameraNameFilter.Value)}\",");
                sb.AppendLine($"  \"CameraFallbackIndex\": {_cfgCameraFallbackIndex.Value},");
                sb.AppendLine($"  \"PresetName\": \"{Esc(_cfgPresetName.Value)}\",");
                sb.AppendLine($"  \"BeatSyncFadeEnabled\": {(_cfgBeatSyncFadeEnabled.Value ? "true" : "false")},");
                sb.AppendLine($"  \"BeatSyncFadeMin\": {_cfgBeatSyncFadeMin.Value},");
                sb.AppendLine($"  \"BeatSyncFadeMax\": {_cfgBeatSyncFadeMax.Value}");
                sb.AppendLine("}");
                File.WriteAllText(_configJsonPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("config json save failed: " + ex.Message);
            }
        }



        public bool LoadPreset(string name)
        {
            if (!_presets.TryGetValue(name, out PresetData p)) return false;
            _cfgFadeFrames.Value = Mathf.Clamp(p.FadeFrames, 1, 300);
            return true;
        }

        public string[] GetPresetNames()
        {
            var names = new string[_presets.Count];
            _presets.Keys.CopyTo(names, 0);
            return names;
        }

        // ---- プリセットJSON ----

        private void LoadPresetsFile()
        {
            _presets = new Dictionary<string, PresetData>();
            if (!File.Exists(_presetsPath)) return;
            try
            {
                string json = File.ReadAllText(_presetsPath, Encoding.UTF8);
                // シンプルなパーサー: {"name": {...}, ...}
                json = json.Trim();
                if (!json.StartsWith("{") || !json.EndsWith("}")) return;
                json = json.Substring(1, json.Length - 2).Trim();

                int pos = 0;
                while (pos < json.Length)
                {
                    string name = ReadJsonString(json, ref pos);
                    if (name == null) break;
                    SkipColon(json, ref pos);
                    PresetData p = ReadPresetObject(json, ref pos);
                    if (p != null) _presets[name] = p;
                    SkipComma(json, ref pos);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("presets load failed: " + ex.Message);
                _presets = new Dictionary<string, PresetData>();
            }
        }

        private void SavePresetsFile()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                bool first = true;
                foreach (var kv in _presets)
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    PresetData p = kv.Value;
                    sb.Append($"  \"{Esc(kv.Key)}\": {{");
                    sb.Append($"\"FadeFrames\":{p.FadeFrames},");
                    sb.Append($"\"MaxSlots\":{p.MaxSlots},");
                    sb.Append($"\"CaptureInterval\":{p.CaptureInterval},");
                    sb.Append($"\"TintR\":{p.TintR:0.####},");
                    sb.Append($"\"TintG\":{p.TintG:0.####},");
                    sb.Append($"\"TintB\":{p.TintB:0.####},");
                    sb.Append($"\"TintA\":{p.TintA:0.####},");
                    sb.Append($"\"AlphaScale\":{p.AlphaScale:0.####},");
                    sb.Append($"\"FadeCurve\":\"{Esc(p.FadeCurve)}\",");
                    sb.Append($"\"FrontOfCharacter\":{(p.FrontOfCharacter ? "true" : "false")}");
                    sb.Append("}");
                }
                sb.AppendLine();
                sb.Append("}");
                File.WriteAllText(_presetsPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("presets save failed: " + ex.Message);
            }
        }

        private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

        private static string ReadJsonString(string json, ref int pos)
        {
            SkipWs(json, ref pos);
            if (pos >= json.Length || json[pos] != '"') return null;
            pos++;
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && pos < json.Length) { sb.Append(json[pos++]); continue; }
                sb.Append(c);
            }
            return null;
        }

        private static void SkipWs(string json, ref int pos) { while (pos < json.Length && json[pos] <= ' ') pos++; }
        private static void SkipColon(string json, ref int pos) { SkipWs(json, ref pos); if (pos < json.Length && json[pos] == ':') pos++; }
        private static void SkipComma(string json, ref int pos) { SkipWs(json, ref pos); if (pos < json.Length && json[pos] == ',') pos++; }

        private static PresetData ReadPresetObject(string json, ref int pos)
        {
            SkipWs(json, ref pos);
            if (pos >= json.Length || json[pos] != '{') return null;
            pos++;
            var p = new PresetData();
            while (pos < json.Length)
            {
                SkipWs(json, ref pos);
                if (pos < json.Length && json[pos] == '}') { pos++; break; }
                string key = ReadJsonString(json, ref pos);
                if (key == null) break;
                SkipColon(json, ref pos);
                SkipWs(json, ref pos);
                if (json[pos] == '"')
                {
                    string val = ReadJsonString(json, ref pos);
                    if (key == "FadeCurve") p.FadeCurve = val;
                }
                else
                {
                    int end = pos;
                    while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
                    string val = json.Substring(pos, end - pos).Trim();
                    pos = end;
                    switch (key)
                    {
                        case "FadeFrames":       if (int.TryParse(val, out int fi))    p.FadeFrames = fi; break;
                        case "MaxSlots":         if (int.TryParse(val, out int ms))    p.MaxSlots = ms; break;
                        case "CaptureInterval":  if (int.TryParse(val, out int ci))    p.CaptureInterval = ci; break;
                        case "TintR":            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tr)) p.TintR = tr; break;
                        case "TintG":            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tg)) p.TintG = tg; break;
                        case "TintB":            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float tb)) p.TintB = tb; break;
                        case "TintA":            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ta)) p.TintA = ta; break;
                        case "AlphaScale":       if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float as_)) p.AlphaScale = as_; break;
                        case "FrontOfCharacter": p.FrontOfCharacter = val == "true"; break;
                    }
                }
                SkipComma(json, ref pos);
            }
            return p;
        }

        // ---- 内部 ----

        private float GetHSceneSpeedIntensity()
        {
            if (Time.unscaledTime >= _nextHSceneScanTime)
            {
                _nextHSceneScanTime = Time.unscaledTime + 1f;
                if (_hSceneProc == null || !_hSceneProc)
                    _hSceneProc = FindObjectOfType<HSceneProc>();
            }
            if (_hSceneProc == null || _hSceneProc.flags == null) return -1f;
            float maxSpeed = _hSceneProc.flags.speedMaxBody > 0f ? _hSceneProc.flags.speedMaxBody : 1f;
            return Mathf.Clamp01(_hSceneProc.flags.speedCalc / maxSpeed);
        }

        private void SetupCaptureCamera()
        {
            _cameraRoot = new GameObject("SimpleAfterimageCapture");
            _cameraRoot.hideFlags = HideFlags.DontSave;
            _captureCamera = _cameraRoot.AddComponent<Camera>();
            _captureCamera.enabled = false;
        }

        private RenderTexture CreateRT(int w, int h)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            rt.Create();
            return rt;
        }

        private void ReleaseSlots()
        {
            if (_slots == null) return;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null) { _slots[i].Release(); Destroy(_slots[i]); _slots[i] = null; }
            }
        }

        private Camera ResolveCamera()
        {
            string filter = _cfgCameraNameFilter.Value ?? "";
            bool hasFilter = filter.Length > 0;

            if (_cfgPreferCameraMain.Value && Camera.main != null && Camera.main.enabled)
            {
                if (!hasFilter || Camera.main.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return Camera.main;
            }

            Camera[] all = Camera.allCameras;
            if (all == null || all.Length == 0) return null;

            var candidates = new List<Camera>(all.Length);
            foreach (Camera c in all)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                if (c == _captureCamera) continue;
                candidates.Add(c);
            }
            if (candidates.Count == 0) return null;
            candidates.Sort((a, b) => b.depth.CompareTo(a.depth));

            if (hasFilter)
            {
                foreach (Camera c in candidates)
                    if (c.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;
            }

            int idx = Mathf.Clamp(_cfgCameraFallbackIndex.Value, 0, candidates.Count - 1);
            return candidates[idx];
        }

        private void SyncOverlayDrawer(Camera srcCamera)
        {
            if (!_cfgEnabled.Value || srcCamera == null)
            {
                if (_overlayDrawer != null) { Destroy(_overlayDrawer); _overlayDrawer = null; }
                _lastSourceCamera = null;
                return;
            }

            if (_lastSourceCamera != srcCamera)
            {
                if (_overlayDrawer != null) { Destroy(_overlayDrawer); _overlayDrawer = null; }
                _overlayDrawer = srcCamera.gameObject.AddComponent<OverlayDrawer>();
                _overlayDrawer.Owner = this;
                _lastSourceCamera = srcCamera;
            }
        }

        private void LateUpdate()
        {
            if (!_cfgEnabled.Value || _slots == null) return;

            // HScene速さ同期
            if (_cfgBeatSyncFadeEnabled.Value)
            {
                float intensity = GetHSceneSpeedIntensity();
                if (intensity >= 0f)
                {
                    int fade = Mathf.RoundToInt(Mathf.Lerp(_cfgBeatSyncFadeMin.Value, _cfgBeatSyncFadeMax.Value, intensity));
                    _cfgFadeFrames.Value = Mathf.Clamp(fade, 1, 300);
                }
            }

            Camera src = ResolveCamera();
            SyncOverlayDrawer(src);

            _frameCounter++;
            int interval = Mathf.Max(1, _cfgCaptureInterval.Value);
            bool shouldCapture = src != null && (interval <= 1 || (_frameCounter % interval) == 0);
            if (shouldCapture)
                StartCoroutine(CaptureEndOfFrame(src));

            AgeThenBuildDrawList();
        }

        private IEnumerator CaptureEndOfFrame(Camera src)
        {
            yield return new WaitForEndOfFrame();
            if (!_cfgEnabled.Value || _slots == null || src == null) yield break;
            _captureCamera.CopyFrom(src);
            _captureCamera.cullingMask = _characterMask;
            _captureCamera.clearFlags = CameraClearFlags.SolidColor;
            _captureCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _captureCamera.targetTexture = _slots[_writeIndex];
            _captureCamera.Render();
            _captureCamera.targetTexture = null;
            _life[_writeIndex] = Mathf.Max(1, _cfgFadeFrames.Value);
            _writeIndex = (_writeIndex + 1) % _slots.Length;
        }

        private void AgeThenBuildDrawList()
        {
            int fadeFrames = Mathf.Max(1, _cfgFadeFrames.Value);
            float alphaScale = Mathf.Clamp01(_cfgAlphaScale.Value);
            float tintA = Mathf.Clamp01(_cfgTintA.Value);

            for (int i = 0; i < _slots.Length; i++)
                if (_life[i] > 0) _life[i]--;

            _drawCount = 0;
            int newest = (_writeIndex - 1 + _slots.Length) % _slots.Length;
            for (int i = 0; i < _slots.Length; i++)
            {
                int slot = (newest - i + _slots.Length) % _slots.Length;
                if (_life[slot] <= 0) continue;

                float t = (float)_life[slot] / fadeFrames;
                t = ApplyCurve(t, _cfgFadeCurve.Value);
                float alpha = tintA * alphaScale * t;
                if (alpha <= 0.0001f) continue;

                _drawSlots[_drawCount] = _slots[slot];
                _drawAlpha[_drawCount] = alpha;
                _drawCount++;
            }
        }

        private static float ApplyCurve(float t, string curve)
        {
            switch (curve)
            {
                case "EaseIn":  return t * t;
                case "EaseOut": return Mathf.Sqrt(t);
                case "Square":  return t * t * t;
                default:        return t;
            }
        }

        internal void DrawOnPostRender()
        {
            if (!_cfgEnabled.Value || _drawCount == 0) return;
            float r = Mathf.Clamp01(_cfgTintR.Value);
            float g = Mathf.Clamp01(_cfgTintG.Value);
            float b = Mathf.Clamp01(_cfgTintB.Value);
            Rect rect = new Rect(0, 0, Screen.width, Screen.height);

            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);
            try
            {
                for (int i = 0; i < _drawCount; i++)
                {
                    if (_drawSlots[i] == null) continue;
                    float alpha = _drawAlpha[i];
                    if (alpha <= 0.0001f) continue;
                    Graphics.DrawTexture(rect, _drawSlots[i], new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0,
                        new Color(r, g, b, alpha));
                }
            }
            finally
            {
                GL.PopMatrix();
            }
        }

        private void OnDestroy()
        {
            if (_overlayDrawer != null) Destroy(_overlayDrawer);
            if (_cameraRoot != null) Destroy(_cameraRoot);
            ReleaseSlots();
            if (Instance == this) Instance = null;
        }
    }
}
