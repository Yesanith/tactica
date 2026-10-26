using TMPro;
using UnityEngine;

namespace Tactica.UI
{
    // One callout. World-space TextMeshPro facing the camera.
    public class FloatingText : MonoBehaviour
    {
        private const float Lifetime = 1.1f;
        private const float RiseSpeed = 0.8f;

        private TextMeshPro label;
        private Color baseColor;
        private float age;

        public static void Create(Vector3 position, string text, Color color, float scale)
        {
            GameObject textObject = new GameObject("FloatingText");
            textObject.transform.position = position;
            textObject.transform.localScale = Vector3.one * scale;

            FloatingText floating = textObject.AddComponent<FloatingText>();
            floating.label = textObject.AddComponent<TextMeshPro>();
            floating.label.text = text;
            floating.label.fontSize = 5f;
            floating.label.fontStyle = FontStyles.Bold;
            floating.label.alignment = TextAlignmentOptions.Center;
            floating.label.color = color;
            floating.label.outlineWidth = 0.25f;
            floating.label.outlineColor = new Color32(0, 0, 0, 255);
            floating.label.textWrappingMode = TextWrappingModes.NoWrap;
            floating.baseColor = color;
            floating.Face();
        }

        private void LateUpdate()
        {
            age += Time.deltaTime;

            if (age >= Lifetime)
            {
                Destroy(gameObject);
                return;
            }

            transform.position += Vector3.up * RiseSpeed * Time.deltaTime;
            Color color = baseColor;
            color.a = 1f - Mathf.Clamp01((age - Lifetime * 0.6f) / (Lifetime * 0.4f));
            label.color = color;
            Face();
        }

        private void Face()
        {
            UnityEngine.Camera camera = UnityEngine.Camera.main;

            if (camera != null)
            {
                transform.rotation = camera.transform.rotation;
            }
        }
    }
}
