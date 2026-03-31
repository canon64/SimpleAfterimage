using System;
using System.IO;
using System.Text;
using BepInEx;
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

        // 設定
        private int _slotCount = 30;        // 残像枚数
        private int _fadeFrames = 30;       // 何フレームで消えるか
        private string _captureLayer = "Chara";

        // キャプチャ用カメラ
        private Camera _captureCamera;
        private GameObject _cameraRoot;

        // スロット: インデックス0が最新、末尾が最古
        private RenderTexture[] _slots;
        private int[] _life;        // 残りフレーム数
        private int _writeIndex;    // 次に書き込むスロット
        private int _characterMask;

        // OnGUI描画用に毎フレーム更新
        private RenderTexture[] _drawSlots;
        private float[] _drawAlpha;
        private int _drawCount;

        private void Awake()
        {
            string dir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            Logger.LogInfo($"{PluginName} {Version} loaded. dir={dir}");

            _slots = new RenderTexture[_slotCount];
            _life = new int[_slotCount];
            _drawSlots = new RenderTexture[_slotCount];
            _drawAlpha = new float[_slotCount];

            for (int i = 0; i < _slotCount; i++)
            {
                _slots[i] = CreateRT();
                _life[i] = 0;
            }

            _characterMask = LayerMask.GetMask(_captureLayer);
            SetupCaptureCamera();
        }

        private void SetupCaptureCamera()
        {
            _cameraRoot = new GameObject("SimpleAfterimageCapture");
            _cameraRoot.hideFlags = HideFlags.DontSave;
            _captureCamera = _cameraRoot.AddComponent<Camera>();
            _captureCamera.enabled = false;
        }

        private RenderTexture CreateRT()
        {
            var rt = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            rt.Create();
            return rt;
        }

        private void LateUpdate()
        {
            Camera src = Camera.main;
            if (src == null) return;

            // キャプチャカメラをメインカメラに同期
            _captureCamera.CopyFrom(src);
            _captureCamera.cullingMask = _characterMask;
            _captureCamera.clearFlags = CameraClearFlags.SolidColor;
            _captureCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _captureCamera.targetTexture = _slots[_writeIndex];
            _captureCamera.Render();
            _captureCamera.targetTexture = null;

            _life[_writeIndex] = _fadeFrames;
            _writeIndex = (_writeIndex + 1) % _slotCount;

            // ライフを1減らす（最新スロットは除く）
            for (int i = 0; i < _slotCount; i++)
            {
                if (_life[i] > 0)
                    _life[i]--;
            }

            // OnGUI用に描画リストを作成
            // 最新→最古の順でリストアップ（描画は逆順＝最新を先に＝奥に）
            _drawCount = 0;
            int idx = (_writeIndex - 1 + _slotCount) % _slotCount; // 直前に書いたスロット
            for (int i = 0; i < _slotCount; i++)
            {
                int slot = (idx - i + _slotCount) % _slotCount;
                if (_life[slot] > 0)
                {
                    _drawSlots[_drawCount] = _slots[slot];
                    // 線形フェード: 新しいほど alpha=1、古いほど alpha→0
                    float t = (float)_life[slot] / _fadeFrames; // 1=新しい, 0=古い
                    _drawAlpha[_drawCount] = t;
                    _drawCount++;
                }
            }
        }

        private void OnGUI()
        {
            if (_drawCount == 0) return;

            // 最新(index=0)が奥、最古(index=_drawCount-1)が手前
            // 後から描くほど上に来るので、最古を最後に描く
            // つまり index=0(最新)から順に描く
            for (int i = 0; i < _drawCount; i++)
            {
                if (_drawSlots[i] == null) continue;
                float alpha = _drawAlpha[i];
                if (alpha <= 0.0001f) continue;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _drawSlots[i]);
            }

            GUI.color = Color.white;
        }

        private void OnDestroy()
        {
            if (_cameraRoot != null)
                Destroy(_cameraRoot);

            if (_slots != null)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i] != null)
                    {
                        _slots[i].Release();
                        Destroy(_slots[i]);
                    }
                }
            }
        }
    }
}
