using UnityEngine;
using UnityEngine.InputSystem;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class CueControllerInput : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ARRenderer arRenderer;

        [Header("Magic Leap Controller Input")]
        [SerializeField] private InputActionReference triggerAction;
        [SerializeField] private InputActionReference bumperAction;

        [Header("Keyboard Input (for testing)")]
        [SerializeField] private Key placeCueKey = Key.Space;
        [SerializeField] private Key clearCuesKey = Key.C;

        private void OnEnable()
        {
            if (triggerAction != null)
            {
                triggerAction.action.Enable();
                triggerAction.action.performed += OnControllerButtonPressed;
            }

            if (bumperAction != null)
            {
                bumperAction.action.Enable();
                bumperAction.action.performed += OnControllerButtonPressed;
            }
        }

        private void OnDisable()
        {
            if (triggerAction != null)
            {
                triggerAction.action.performed -= OnControllerButtonPressed;
            }

            if (bumperAction != null)
            {
                bumperAction.action.performed -= OnControllerButtonPressed;
            }
        }

        private void Start()
        {
            if (arRenderer == null)
            {
                arRenderer = FindObjectOfType<ARRenderer>();
            }

            if (arRenderer == null)
            {
                Debug.LogError("[CueControllerInput] No ARRenderer found in scene!");
            }
        }

        private void Update()
        {
            if (Keyboard.current != null)
            {
                if (Keyboard.current[placeCueKey].wasPressedThisFrame)
                {
                    PlaceNextCue();
                }

                if (Keyboard.current[clearCuesKey].wasPressedThisFrame)
                {
                    ClearAllCues();
                }
            }
        }

        private void OnControllerButtonPressed(InputAction.CallbackContext context)
        {
            PlaceNextCue();
        }

        public void PlaceNextCue()
        {
            if (arRenderer == null)
            {
                Debug.LogWarning("[CueControllerInput] No ARRenderer assigned.");
                return;
            }

            if (!arRenderer.IsReadyToPlaceCues)
            {
                Debug.Log("[CueControllerInput] Calibration not complete yet. Keep looking at the ground.");
                return;
            }

            arRenderer.PlaceNextCue();
        }

        public void ClearAllCues()
        {
            if (arRenderer != null)
            {
                arRenderer.ClearAllCues();
            }
        }
    }
}
