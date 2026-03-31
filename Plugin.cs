using System;
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

        // キャラ背面描画用 MonoBehaviour
        private sealed class BehindDrawer : MonoBehaviour
        {
            internal Plugin Owner;
            private void OnPostRender() { Owner?.DrawBehind(); }
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
        private ConfigEntry<bool>   _cfgFrontOfCharacter;  // true=前面(OnGUI), false=背面(OnPostRender)
        private ConfigEntry<bool>   _cfgPreferCameraMain;
        private ConfigEntry<string> _cfgCameraNameFilter;
        private ConfigEntry<int>    _cfgCameraFallbackIndex;

        // Runtime
        private Camera _captureCamera;
        private Camera _lastSourceCamera;
        private BehindDrawer _behindDrawer;
        private GameObject _cameraRoot;
        private RenderTexture[] _slots;
        private int[] _life;
        private int _writeIndex;
        private int _frameCounter;
        private int _characterMask;
        private int _rtWidth;
        private int _rtHeight;

        // 描画リスト（LateUpdateで構築、OnGUI/OnPostRenderで使用）
        private RenderTexture[] _drawSlots;
        private float[] _drawAlpha;
        private int _drawCount;

        private void Awake()
        {
            SetupConfig();
            ApplyConfig();
            Logger.LogInfo($"{PluginName} {Version} loaded.");
        }

        private void SetupConfig()
        {
            const string cat1 = "01.一般";
            const string cat2 = "02.キャプチャ";
            const string cat3 = "03.オーバーレイ";
            const string cat4 = "04.元カメラ";

            _cfgEnabled         = Config.Bind(cat1, "有効",             true,  "機能の有効/無効");
            _cfgVerboseLog      = Config.Bind(cat1, "詳細ログ",         false, "詳細ログを出力する");
            _cfgFadeFrames      = Config.Bind(cat2, "残像寿命フレーム", 30,    new ConfigDescription("残像が消えるまでのフレーム数", new AcceptableValueRange<int>(1, 300)));
            _cfgMaxSlots        = Config.Bind(cat2, "同時残像数",       30,    new ConfigDescription("同時に保持する残像スロット数", new AcceptableValueRange<int>(1, 300)));
            _cfgCaptureInterval = Config.Bind(cat2, "キャプチャ間隔",   1,     new ConfigDescription("何フレームごとにキャプチャするか(1=毎フレーム)", new AcceptableValueRange<int>(1, 60)));
            _cfgUseScreenSize   = Config.Bind(cat2, "画面解像度を使う", true,  "キャプチャサイズに画面解像度を使う");
            _cfgCaptureWidth    = Config.Bind(cat2, "キャプチャ幅",     0,     new ConfigDescription("UseScreenSize=false時のキャプチャ幅", new AcceptableValueRange<int>(0, 8192)));
            _cfgCaptureHeight   = Config.Bind(cat2, "キャプチャ高さ",   0,     new ConfigDescription("UseScreenSize=false時のキャプチャ高さ", new AcceptableValueRange<int>(0, 8192)));
            _cfgCharaLayer      = Config.Bind(cat2, "キャラレイヤー名", "Chara", "キャプチャ対象のレイヤー名");
            _cfgTintR           = Config.Bind(cat3, "色R",             1f,    new ConfigDescription("残像色 R (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintG           = Config.Bind(cat3, "色G",             1f,    new ConfigDescription("残像色 G (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintB           = Config.Bind(cat3, "色B",             1f,    new ConfigDescription("残像色 B (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgTintA           = Config.Bind(cat3, "色A",             1f,    new ConfigDescription("残像色 A (0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgAlphaScale      = Config.Bind(cat3, "残像アルファ倍率", 1f,   new ConfigDescription("残像の全体濃度スケール(0..1)", new AcceptableValueRange<float>(0f, 1f)));
            _cfgFrontOfCharacter = Config.Bind(cat3, "キャラ前面に表示", true, "true=キャラの前面(OnGUI) / false=キャラの背面(OnPostRender)");
            _cfgPreferCameraMain    = Config.Bind(cat4, "Camera.main優先",       true, "Camera.mainを優先する");
            _cfgCameraNameFilter    = Config.Bind(cat4, "カメラ名フィルタ",       "",   "カメラ名の部分一致フィルタ(空なら無効)");
            _cfgCameraFallbackIndex = Config.Bind(cat4, "カメラ候補フォールバック", 0,  new ConfigDescription("候補カメラのフォールバックインデックス", new AcceptableValueRange<int>(0, 64)));

            Config.SettingChanged += (_, _) => ApplyConfig();
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
                if (_slots[i] != null)
                {
                    _slots[i].Release();
                    Destroy(_slots[i]);
                    _slots[i] = null;
                }
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

            var candidates = new System.Collections.Generic.List<Camera>(all.Length);
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

        // 背面モード: BehindDrawer をソースカメラにアタッチ/解除
        private void SyncBehindDrawer(Camera srcCamera)
        {
            bool needBehind = _cfgEnabled.Value && !_cfgFrontOfCharacter.Value && srcCamera != null;

            if (!needBehind)
            {
                if (_behindDrawer != null) { Destroy(_behindDrawer); _behindDrawer = null; }
                _lastSourceCamera = null;
                return;
            }

            // ソースカメラが変わったら付け直す
            if (_lastSourceCamera != srcCamera)
            {
                if (_behindDrawer != null) { Destroy(_behindDrawer); _behindDrawer = null; }
                _behindDrawer = srcCamera.gameObject.AddComponent<BehindDrawer>();
                _behindDrawer.Owner = this;
                _lastSourceCamera = srcCamera;
            }
        }

        private void LateUpdate()
        {
            if (!_cfgEnabled.Value || _slots == null) return;

            Camera src = ResolveCamera();
            SyncBehindDrawer(src);

            _frameCounter++;
            int interval = Mathf.Max(1, _cfgCaptureInterval.Value);
            if (interval > 1 && (_frameCounter % interval) != 0)
            {
                AgeThenBuildDrawList();
                return;
            }

            if (src == null) { AgeThenBuildDrawList(); return; }

            _captureCamera.CopyFrom(src);
            _captureCamera.cullingMask = _characterMask;
            _captureCamera.clearFlags = CameraClearFlags.SolidColor;
            _captureCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _captureCamera.targetTexture = _slots[_writeIndex];
            _captureCamera.Render();
            _captureCamera.targetTexture = null;

            _life[_writeIndex] = Mathf.Max(1, _cfgFadeFrames.Value);
            _writeIndex = (_writeIndex + 1) % _slots.Length;

            AgeThenBuildDrawList();
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

                float t = (float)_life[slot] / fadeFrames; // 1=新しい, 0=古い
                float alpha = tintA * alphaScale * t;
                if (alpha <= 0.0001f) continue;

                _drawSlots[_drawCount] = _slots[slot];
                _drawAlpha[_drawCount] = alpha;
                _drawCount++;
            }
        }

        private void DrawSlots()
        {
            if (_drawCount == 0) return;
            float r = Mathf.Clamp01(_cfgTintR.Value);
            float g = Mathf.Clamp01(_cfgTintG.Value);
            float b = Mathf.Clamp01(_cfgTintB.Value);
            Rect rect = new Rect(0, 0, Screen.width, Screen.height);

            // 最新(index=0)が先＝奥、最古(index=_drawCount-1)が後＝手前
            for (int i = 0; i < _drawCount; i++)
            {
                if (_drawSlots[i] == null) continue;
                float alpha = _drawAlpha[i];
                if (alpha <= 0.0001f) continue;
                GUI.color = new Color(r, g, b, alpha);
                GUI.DrawTexture(rect, _drawSlots[i]);
            }
            GUI.color = Color.white;
        }

        // 前面モード: OnGUIで描画（全カメラ描画後 → キャラより前面）
        private void OnGUI()
        {
            if (!_cfgEnabled.Value || !_cfgFrontOfCharacter.Value) return;
            DrawSlots();
        }

        // 背面モード: BehindDrawer の OnPostRender から呼ばれる
        internal void DrawBehind()
        {
            if (!_cfgEnabled.Value || _cfgFrontOfCharacter.Value || _drawCount == 0) return;
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
            if (_behindDrawer != null) Destroy(_behindDrawer);
            if (_cameraRoot != null) Destroy(_cameraRoot);
            ReleaseSlots();
        }
    }
}
