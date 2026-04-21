using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class AdaptiveCueMenuController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ARRenderer arRenderer;
        [SerializeField] private Transform menuAnchor;

        [Header("Presentation")]
        [SerializeField] private bool showOnStart = true;
        [SerializeField, Min(0.5f)] private float menuDistance = 1.15f;
        [SerializeField] private float verticalOffset = -0.08f;
        [SerializeField] private Vector2 canvasSize = new Vector2(1280f, 540f);
        [SerializeField] private Vector3 canvasScale = new Vector3(0.0012f, 0.0012f, 0.0012f);

        [Header("Colors")]
        [SerializeField] private Color panelColor = new Color(0.05f, 0.06f, 0.08f, 0.94f);
        [SerializeField] private Color cardColor = new Color(0.13f, 0.15f, 0.18f, 0.96f);
        [SerializeField] private Color selectedCardColor = new Color(0.20f, 0.24f, 0.29f, 1f);
        [SerializeField] private Color selectedOutlineColor = new Color(0.96f, 0.87f, 0.49f, 1f);
        [SerializeField] private Color labelColor = new Color(0.90f, 0.93f, 0.97f, 1f);
        [SerializeField] private Color subtitleColor = new Color(0.72f, 0.78f, 0.86f, 1f);

        private readonly List<CardWidgets> cards = new List<CardWidgets>();
        private readonly List<Texture2D> previewTextures = new List<Texture2D>();

        private CuePresetDefinition[] presets;
        private Font runtimeFont;
        private GameObject canvasRoot;
        private RectTransform canvasRect;
        private Text selectionSummary;
        private int selectedIndex;
        private bool menuPositioned;

        public bool IsVisible => canvasRoot != null && canvasRoot.activeSelf;

        public CuePresetDefinition SelectedPreset => presets != null && presets.Length > 0
            ? presets[Mathf.Clamp(selectedIndex, 0, presets.Length - 1)]
            : default;

        private void Awake()
        {
            presets = CuePresetLibrary.GetBuiltInPresets();
            runtimeFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            if (arRenderer == null)
            {
                arRenderer = GetComponent<ARRenderer>();
                if (arRenderer == null)
                {
                    arRenderer = FindObjectOfType<ARRenderer>();
                }
            }

            EnsureCanvas();
        }

        private void Start()
        {
            if (arRenderer != null)
            {
                SetSelection(arRenderer.SelectedCuePreset, false);
            }

            if (showOnStart)
            {
                ShowMenu();
            }
            else
            {
                HideMenu();
            }
        }

        private void LateUpdate()
        {
            // Menu is world-locked: only position once when first shown, then stay in place
        }

        private void OnDestroy()
        {
            for (int index = 0; index < previewTextures.Count; index++)
            {
                if (previewTextures[index] != null)
                {
                    Destroy(previewTextures[index]);
                }
            }
        }

        public void Bind(ARRenderer renderer, Transform anchor)
        {
            arRenderer = renderer;
            menuAnchor = anchor;

            if (arRenderer != null)
            {
                SetSelection(arRenderer.SelectedCuePreset, false);
            }
        }

        public void ShowMenu()
        {
            EnsureCanvas();
            canvasRoot.SetActive(true);
            
            // Only position the menu the first time it's shown; after that it stays world-locked
            if (!menuPositioned)
            {
                UpdateMenuPose();
                menuPositioned = true;
            }
            
            RefreshVisualState();
        }

        public void HideMenu()
        {
            if (canvasRoot != null)
            {
                canvasRoot.SetActive(false);
            }
        }

        public void RepositionMenu()
        {
            if (canvasRoot != null)
            {
                UpdateMenuPose();
                menuPositioned = true;
                Debug.Log("[AdaptiveCueingMenu] Menu repositioned to current view.");
            }
        }

        public void ToggleMenu()
        {
            if (IsVisible)
            {
                HideMenu();
            }
            else
            {
                ShowMenu();
            }
        }

        public void MoveSelection(int direction)
        {
            if (presets == null || presets.Length == 0)
            {
                return;
            }

            selectedIndex = (selectedIndex + direction + presets.Length) % presets.Length;
            RefreshVisualState();
        }

        public void ConfirmSelection()
        {
            if (arRenderer == null || presets == null || presets.Length == 0)
            {
                Debug.LogWarning("[AdaptiveCueingMenu] Unable to apply cue preset selection.");
                return;
            }

            CuePresetDefinition preset = presets[selectedIndex];
            arRenderer.SetCuePreset(preset.Id);
            arRenderer.ClearAllCues();
            HideMenu();

            Debug.Log($"[AdaptiveCueingMenu] Selected cue preset: {preset.DisplayName}");
        }

        public void SetSelection(CuePresetId presetId, bool refreshVisuals)
        {
            if (presets == null || presets.Length == 0)
            {
                return;
            }

            for (int index = 0; index < presets.Length; index++)
            {
                if (presets[index].Id == presetId)
                {
                    selectedIndex = index;
                    if (refreshVisuals)
                    {
                        RefreshVisualState();
                    }
                    return;
                }
            }
        }

        private void EnsureCanvas()
        {
            if (canvasRoot != null)
            {
                return;
            }

            canvasRoot = new GameObject("Adaptive Cue Menu");
            // Don't parent to this transform - keep it in world space so it stays locked

            Canvas canvas = canvasRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;

            canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = canvasSize;
            canvasRect.localScale = canvasScale;

            CanvasScaler scaler = canvasRoot.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 16f;

            Image panel = canvasRoot.AddComponent<Image>();
            panel.color = panelColor;

            CreateText(
                canvasRoot.transform,
                "Cue Presets",
                52,
                FontStyle.Bold,
                labelColor,
                new Vector2(0f, 210f),
                new Vector2(1120f, 72f),
                TextAnchor.MiddleCenter);

            CreateText(
                canvasRoot.transform,
                "Use the trackpad or bumper to move, trigger to select, and the menu button to reopen this panel.",
                26,
                FontStyle.Normal,
                subtitleColor,
                new Vector2(0f, 154f),
                new Vector2(1120f, 42f),
                TextAnchor.MiddleCenter);

            selectionSummary = CreateText(
                canvasRoot.transform,
                string.Empty,
                24,
                FontStyle.Normal,
                labelColor,
                new Vector2(0f, -208f),
                new Vector2(1120f, 44f),
                TextAnchor.MiddleCenter);

            CreateCards();
            RefreshVisualState();
        }

        private void CreateCards()
        {
            const float cardWidth = 220f;
            const float cardHeight = 248f;
            const float cardGap = 18f;
            float totalWidth = (presets.Length * cardWidth) + ((presets.Length - 1) * cardGap);
            float startX = -(totalWidth * 0.5f) + (cardWidth * 0.5f);

            for (int index = 0; index < presets.Length; index++)
            {
                CuePresetDefinition preset = presets[index];
                GameObject cardObject = new GameObject($"{preset.Id}_Card");
                cardObject.transform.SetParent(canvasRoot.transform, false);

                RectTransform cardRect = cardObject.AddComponent<RectTransform>();
                cardRect.anchoredPosition = new Vector2(startX + (index * (cardWidth + cardGap)), -4f);
                cardRect.sizeDelta = new Vector2(cardWidth, cardHeight);

                Image cardBackground = cardObject.AddComponent<Image>();
                cardBackground.color = cardColor;

                Outline outline = cardObject.AddComponent<Outline>();
                outline.effectColor = Color.clear;
                outline.effectDistance = new Vector2(5f, 5f);

                GameObject previewFrameObject = new GameObject("PreviewFrame");
                previewFrameObject.transform.SetParent(cardObject.transform, false);
                RectTransform previewFrameRect = previewFrameObject.AddComponent<RectTransform>();
                previewFrameRect.anchoredPosition = new Vector2(0f, 24f);
                previewFrameRect.sizeDelta = new Vector2(180f, 112f);
                Image previewFrame = previewFrameObject.AddComponent<Image>();
                previewFrame.color = new Color(0.08f, 0.09f, 0.11f, 1f);

                GameObject previewObject = new GameObject("Preview");
                previewObject.transform.SetParent(previewFrameObject.transform, false);
                RectTransform previewRect = previewObject.AddComponent<RectTransform>();
                previewRect.anchorMin = Vector2.zero;
                previewRect.anchorMax = Vector2.one;
                previewRect.offsetMin = new Vector2(6f, 6f);
                previewRect.offsetMax = new Vector2(-6f, -6f);
                RawImage previewImage = previewObject.AddComponent<RawImage>();
                Texture2D previewTexture = CuePresetLibrary.CreatePreviewTexture(preset);
                previewTextures.Add(previewTexture);
                previewImage.texture = previewTexture;

                Text label = CreateText(
                    cardObject.transform,
                    preset.DisplayName,
                    24,
                    FontStyle.Bold,
                    labelColor,
                    new Vector2(0f, -82f),
                    new Vector2(188f, 56f),
                    TextAnchor.MiddleCenter);

                cards.Add(new CardWidgets
                {
                    Background = cardBackground,
                    Outline = outline,
                    Label = label
                });
            }
        }

        private void RefreshVisualState()
        {
            if (presets == null || presets.Length == 0 || cards.Count == 0)
            {
                return;
            }

            for (int index = 0; index < cards.Count; index++)
            {
                bool isSelected = index == selectedIndex;
                cards[index].Background.color = isSelected ? selectedCardColor : cardColor;
                cards[index].Outline.effectColor = isSelected ? selectedOutlineColor : Color.clear;
                cards[index].Label.color = isSelected ? Color.white : labelColor;
            }

            CuePresetDefinition preset = presets[selectedIndex];
            selectionSummary.text = $"{preset.DisplayName} selected. After choosing, localize the space, look at the floor, and press trigger again to set the ground.";
        }

        private void UpdateMenuPose()
        {
            Transform anchor = ResolveMenuAnchor();
            if (anchor == null)
            {
                return;
            }

            Vector3 forward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 position = anchor.position + (forward * menuDistance) + (Vector3.up * verticalOffset);
            canvasRoot.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));

            Canvas canvas = canvasRoot.GetComponent<Canvas>();
            if (canvas != null && Camera.main != null)
            {
                canvas.worldCamera = Camera.main;
            }
        }

        private Transform ResolveMenuAnchor()
        {
            if (menuAnchor != null)
            {
                return menuAnchor;
            }

            if (Camera.main != null)
            {
                return Camera.main.transform;
            }

            return arRenderer != null ? arRenderer.transform : null;
        }

        private Text CreateText(
            Transform parent,
            string textValue,
            int fontSize,
            FontStyle fontStyle,
            Color color,
            Vector2 anchoredPosition,
            Vector2 size,
            TextAnchor anchor)
        {
            GameObject textObject = new GameObject(textValue);
            textObject.transform.SetParent(parent, false);

            RectTransform rectTransform = textObject.AddComponent<RectTransform>();
            rectTransform.anchoredPosition = anchoredPosition;
            rectTransform.sizeDelta = size;

            Text text = textObject.AddComponent<Text>();
            text.text = textValue;
            text.font = runtimeFont;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private sealed class CardWidgets
        {
            public Image Background;
            public Outline Outline;
            public Text Label;
        }
    }
}
