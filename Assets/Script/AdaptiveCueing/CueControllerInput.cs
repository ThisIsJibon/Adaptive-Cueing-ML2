using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class CueControllerInput : MonoBehaviour
    {
        private const string ControllerActionMapName = "Controller";

        [Header("References")]
        [SerializeField] private ARRenderer arRenderer;
        [SerializeField] private MLSpaceGroundDetector groundDetector;
        [SerializeField] private AdaptiveCueMenuController menuController;

        [Header("Magic Leap Controller Input")]
        [SerializeField] private InputActionReference triggerAction;
        [SerializeField] private InputActionReference bumperAction;
        [SerializeField] private InputActionReference menuButtonAction;
        [SerializeField] private InputActionReference trackpadAction;
        [SerializeField] private InputActionReference trackpadClickAction;
        [SerializeField, Range(0.2f, 0.95f)] private float trackpadNavigationThreshold = 0.55f;
        [SerializeField, Min(0.1f)] private float trackpadNavigationRepeatDelay = 0.2f;

        [Header("Keyboard Input (for testing)")]
        [SerializeField] private Key placeCueKey = Key.Space;
        [SerializeField] private Key clearCuesKey = Key.C;
        [SerializeField] private Key toggleMenuKey = Key.M;
        [SerializeField] private Key previousOptionKey = Key.LeftArrow;
        [SerializeField] private Key nextOptionKey = Key.RightArrow;
        [SerializeField] private Key selectOptionKey = Key.Enter;

        private InputAction resolvedTriggerAction;
        private InputAction resolvedBumperAction;
        private InputAction resolvedMenuAction;
        private InputAction resolvedTrackpadValueAction;
        private InputAction resolvedTrackpadClickAction;
        private float nextTrackpadNavigationTime;
        private bool trackpadNavigationLatched;

        private void OnEnable()
        {
            ResolveSceneReferences();
            ResolveInputActions();
            SubscribeActions();
        }

        private void OnDisable()
        {
            UnsubscribeActions();
        }

        private void Start()
        {
            ResolveSceneReferences();

            if (arRenderer == null)
            {
                Debug.LogError("[CueControllerInput] No ARRenderer found in scene.");
            }
        }

        private void Update()
        {
            HandleKeyboardShortcuts();
            HandleTrackpadNavigation();
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
                Debug.Log("[CueControllerInput] Ground not set. Look at the ground and press trigger first.");
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

        private void HandleKeyboardShortcuts()
        {
            if (Keyboard.current == null)
            {
                return;
            }

            if (Keyboard.current[toggleMenuKey].wasPressedThisFrame && menuController != null)
            {
                menuController.ToggleMenu();
            }

            if (menuController != null && menuController.IsVisible)
            {
                if (Keyboard.current[previousOptionKey].wasPressedThisFrame)
                {
                    menuController.MoveSelection(-1);
                }

                if (Keyboard.current[nextOptionKey].wasPressedThisFrame)
                {
                    menuController.MoveSelection(1);
                }

                if (Keyboard.current[selectOptionKey].wasPressedThisFrame || Keyboard.current[placeCueKey].wasPressedThisFrame)
                {
                    menuController.ConfirmSelection();
                }

                return;
            }

            if (Keyboard.current[placeCueKey].wasPressedThisFrame)
            {
                HandlePrimaryAction();
            }

            if (Keyboard.current[clearCuesKey].wasPressedThisFrame)
            {
                ClearAllCues();
            }
        }

        private void HandleTrackpadNavigation()
        {
            if (menuController == null || !menuController.IsVisible || resolvedTrackpadValueAction == null)
            {
                trackpadNavigationLatched = false;
                return;
            }

            Vector2 axis = resolvedTrackpadValueAction.ReadValue<Vector2>();
            if (Mathf.Abs(axis.x) < trackpadNavigationThreshold)
            {
                trackpadNavigationLatched = false;
                return;
            }

            if (!trackpadNavigationLatched || Time.time >= nextTrackpadNavigationTime)
            {
                menuController.MoveSelection(axis.x > 0f ? 1 : -1);
                trackpadNavigationLatched = true;
                nextTrackpadNavigationTime = Time.time + trackpadNavigationRepeatDelay;
            }
        }

        private void OnTriggerPerformed(InputAction.CallbackContext context)
        {
            HandlePrimaryAction();
        }

        private void OnBumperPerformed(InputAction.CallbackContext context)
        {
            if (menuController != null && menuController.IsVisible)
            {
                menuController.MoveSelection(1);
                return;
            }

            HandlePrimaryAction();
        }

        private void OnMenuPerformed(InputAction.CallbackContext context)
        {
            if (menuController != null)
            {
                menuController.ToggleMenu();
            }
        }

        private void OnTrackpadClickPerformed(InputAction.CallbackContext context)
        {
            if (menuController != null && menuController.IsVisible)
            {
                menuController.MoveSelection(1);
            }
        }

        private void HandlePrimaryAction()
        {
            if (menuController != null && menuController.IsVisible)
            {
                menuController.ConfirmSelection();
                return;
            }

            if (groundDetector != null && groundDetector.IsWaitingForGroundLook)
            {
                groundDetector.TrySetGroundFromCrosshair();
                return;
            }

            PlaceNextCue();
        }

        private void ResolveSceneReferences()
        {
            if (arRenderer == null)
            {
                arRenderer = FindObjectOfType<ARRenderer>();
            }

            if (groundDetector == null)
            {
                groundDetector = FindObjectOfType<MLSpaceGroundDetector>();
            }

            if (menuController == null)
            {
                menuController = FindObjectOfType<AdaptiveCueMenuController>();
            }
        }

        private void ResolveInputActions()
        {
            resolvedTriggerAction = ResolveAction(triggerAction, "Trigger");
            resolvedBumperAction = ResolveAction(bumperAction, "Bumper");
            resolvedMenuAction = ResolveAction(menuButtonAction, "MenuButton");
            resolvedTrackpadValueAction = ResolveAction(trackpadAction, "Trackpad");
            resolvedTrackpadClickAction = ResolveAction(trackpadClickAction, "TrackpadClick");
        }

        private InputAction ResolveAction(InputActionReference actionReference, string actionName)
        {
            if (actionReference != null && actionReference.action != null)
            {
                return actionReference.action;
            }

            InputActionManager actionManager = FindObjectOfType<InputActionManager>();
            if (actionManager == null || actionManager.actionAssets == null)
            {
                return null;
            }

            for (int assetIndex = 0; assetIndex < actionManager.actionAssets.Count; assetIndex++)
            {
                InputActionAsset actionAsset = actionManager.actionAssets[assetIndex];
                if (actionAsset == null)
                {
                    continue;
                }

                InputActionMap controllerMap = actionAsset.FindActionMap(ControllerActionMapName, false);
                InputAction action = controllerMap?.FindAction(actionName, false);
                if (action != null)
                {
                    action.Enable();
                    return action;
                }
            }

            return null;
        }

        private void SubscribeActions()
        {
            Subscribe(resolvedTriggerAction, OnTriggerPerformed);
            Subscribe(resolvedBumperAction, OnBumperPerformed);
            Subscribe(resolvedMenuAction, OnMenuPerformed);
            Subscribe(resolvedTrackpadClickAction, OnTrackpadClickPerformed);
        }

        private void UnsubscribeActions()
        {
            Unsubscribe(resolvedTriggerAction, OnTriggerPerformed);
            Unsubscribe(resolvedBumperAction, OnBumperPerformed);
            Unsubscribe(resolvedMenuAction, OnMenuPerformed);
            Unsubscribe(resolvedTrackpadClickAction, OnTrackpadClickPerformed);
        }

        private static void Subscribe(InputAction action, System.Action<InputAction.CallbackContext> callback)
        {
            if (action == null)
            {
                return;
            }

            action.Enable();
            action.performed -= callback;
            action.performed += callback;
        }

        private static void Unsubscribe(InputAction action, System.Action<InputAction.CallbackContext> callback)
        {
            if (action == null)
            {
                return;
            }

            action.performed -= callback;
        }
    }
}
