using UnityEditor;
using UnityEngine;
using TheLaw.UI;

namespace TheLaw.EditorTools
{
    /// <summary>
    /// UI 摄像机搭建：正交、仅渲染 UI 层、纯黑背景、动态 16:9 viewport。
    /// 顺带兜底：场景缺主相机/灯光时创建（SolidColor 黑背景——UI 黑边区透出的是主相机背景）。
    /// </summary>
    public static class UISetup
    {
        const string UICamName = "UICamera";

        [MenuItem("Tools/UI/Setup UI Camera")]
        public static void Setup()
        {
            EnsureMainCameraAndLight();
            EnsureUICamera();
            Debug.Log("[UISetup] UI 摄像机就绪");
        }

        static void EnsureUICamera()
        {
            var existing = GameObject.Find(UICamName);
            if (existing != null)
            {
                Selection.activeGameObject = existing;
                return;
            }

            var go = new GameObject(UICamName);
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.Depth; // 全屏 UI 层：透明区域透出主相机
            cam.cullingMask = LayerMask.GetMask("UI");
            cam.depth = 1; // 高于主相机
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.orthographicSize = 5.4f; // 1080/2 /100
            go.AddComponent<UICameraViewport>();
        }

        static void EnsureMainCameraAndLight()
        {
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f); // 黑边（UI viewport 外区域）
                camGo.AddComponent<AudioListener>();
                camGo.transform.position = new Vector3(0f, 9f, -3.58f);
                camGo.transform.rotation = Quaternion.Euler(60f, 0f, 0f);
            }
            if (Object.FindObjectOfType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1f;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }
    }
}
